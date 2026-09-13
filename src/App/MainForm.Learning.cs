using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Dictionary<string, ILearnedInputDeviceSource> learnedSources = new Dictionary<string, ILearnedInputDeviceSource>(StringComparer.Ordinal);
        LearnedInputRouting learnedInputRouting;
        ReaderSession routingReader;
        int learnedSourceGeneration;
        bool learnedSourcesOpening, rebuildingInputRouting;
        string learnedPressureIdentity;
        ToolStripMenuItem learnUnknownMenu;
        readonly object learnedOpenGate = new object();
        readonly HashSet<LearnedSourceBatch> learnedOpenBatches = new HashSet<LearnedSourceBatch>();
        sealed class LearnedSourceBatch
        {
            List<ILearnedInputDeviceSource> values;
            internal LearnedSourceBatch(List<ILearnedInputDeviceSource> sources) { values = sources; }
            internal List<ILearnedInputDeviceSource> Take() { lock (this) { var result = values; values = null; return result; } }
        }
        void CancelLearnedOpenBatches()
        {
            LearnedSourceBatch[] batches;
            lock (learnedOpenGate) { batches = learnedOpenBatches.ToArray(); learnedOpenBatches.Clear(); }
            foreach (var batch in batches) DisposeSourceBatch(batch);
        }
        void DisposeSourceBatch(LearnedSourceBatch batch)
        {
            var sources = batch.Take(); if (sources == null) return;
            foreach (var source in sources) if (source != null)
                try { source.Dispose(); } catch (Exception error) { store.Event("Input cleanup failed: " + error.Message); }
        }

        LearnedInputRouting InputView
        {
            get
            {
                if (learnedInputRouting == null || !Object.ReferenceEquals(routingReader, reader)) RebuildInputRouting();
                return learnedInputRouting;
            }
        }
        bool HasLearnedInputs { get { return UiReadProfile.LearnedInputs != null && UiReadProfile.LearnedInputs.Count != 0; } }
        bool LiveInputReading { get { return !rebuildingInputRouting && HasLearnedInputs ? InputView.IsReading : reader != null && reader.IsReading; } }
        bool LiveInputSamples { get { return HasLearnedInputs ? InputView.HasReceivedSamples : reader != null && reader.HasReceivedSamples; } }
        KeyStateSnapshot[] InputUiSnapshot()
        { return HasLearnedInputs ? InputView.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds) : reader == null ? new KeyStateSnapshot[0] : reader.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds); }

        void RebuildInputRouting()
        {
            if (rebuildingInputRouting) return;
            rebuildingInputRouting = true;
            try {
            CancelPressureCapture(); CancelInputThresholdCapture();
            var old = learnedInputRouting;
            routingReader = reader;
            learnedInputRouting = new LearnedInputRouting(reader, UiReadProfile.LearnedInputs ?? new List<LearnedKeyBinding>(),
                sharedPressureRange.ScaleMaximum, learnedSources.Values);
            if (!deviceDetachInProgress) { if (HasLearnedInputs) runtime.SetInputSource(learnedInputRouting); else runtime.SetReader(reader); }
            if (old != null) old.Dispose();
            rgbPlanCache = null;
            } finally { rebuildingInputRouting = false; }
        }
        void ConfigureLearnedInputs()
        {
            ++learnedSourceGeneration; learnedSourcesOpening = false; CancelLearnedOpenBatches();
            var needed = new HashSet<string>((UiReadProfile.LearnedInputs ?? new List<LearnedKeyBinding>()).Where(binding => binding.Backend != "travel").Select(binding => binding.SourceDeviceId));
            foreach (string obsolete in learnedSources.Keys.Where(id => !needed.Contains(id)).ToArray())
            { var owned = learnedSources[obsolete]; learnedSources.Remove(obsolete); owned.Dispose(); }
            if (!HasLearnedInputs)
            {
                CancelPressureCapture(); CancelInputThresholdCapture();
                if (learnedInputRouting != null) learnedInputRouting.Dispose();
                learnedInputRouting = null; routingReader = null; runtime.SetReader(reader); return;
            }
            RebuildInputRouting();
            if (!previewMode && IsHandleCreated && needed.Any(id => !learnedSources.ContainsKey(id))) OpenSavedLearnedSources();
        }
        void OpenSavedLearnedSources()
        {
            if (learnedSourcesOpening || previewMode || closing || deviceDetachInProgress || applicationResourcesDisposed || !IsHandleCreated || discoveryContext == null) return;
            foreach (string inactive in learnedSources.Where(pair => !pair.Value.IsReading).Select(pair => pair.Key).ToArray())
            { var owned = learnedSources[inactive]; learnedSources.Remove(inactive); owned.Dispose(); }
            var requested = (UiReadProfile.LearnedInputs ?? new List<LearnedKeyBinding>()).Where(binding => binding.Backend != "travel" && !learnedSources.ContainsKey(binding.SourceDeviceId))
                .Select(binding => binding.SourceDeviceId).Distinct().ToArray();
            if (requested.Length == 0) return;
            int generation = learnedSourceGeneration; learnedSourcesOpening = true;
            var uiContext = discoveryContext;
            ThreadPool.QueueUserWorkItem(delegate {
                var opened = new List<ILearnedInputDeviceSource>();
                try
                {
                    foreach (var option in DiscoverLearnInputDevices(null))
                        if (requested.Contains(option.DeviceId))
                            try { opened.Add(option.Open()); } catch (Exception error) { store.Event("Learned input unavailable: " + error.Message); }
                }
                catch (Exception error) { store.Event("Learned input discovery failed: " + error.Message); }
                var batch = new LearnedSourceBatch(opened);
                lock (learnedOpenGate)
                {
                    if (closing || applicationResourcesDisposed || generation != learnedSourceGeneration) { DisposeSourceBatch(batch); return; }
                    learnedOpenBatches.Add(batch);
                }
                try
                {
                    uiContext.Post(delegate {
                        lock (learnedOpenGate) learnedOpenBatches.Remove(batch);
                        if (closing || IsDisposed || applicationResourcesDisposed || generation != learnedSourceGeneration) { DisposeSourceBatch(batch); return; }
                        if (deviceDetachInProgress) { learnedSourcesOpening = false; DisposeSourceBatch(batch); return; }
                        var inputs = batch.Take(); if (inputs == null) return;
                        learnedSourcesOpening = false;
                        bool added = false;
                        foreach (var input in inputs)
                            if (input != null && input.IsReading && !learnedSources.ContainsKey(input.DeviceId)) { learnedSources.Add(input.DeviceId, input); added = true; }
                            else if (input != null) input.Dispose();
                        if (added) { RebuildInputRouting(); RefreshKeySources(); UpdateKeyCard(); }
                    }, null);
                }
                catch (InvalidOperationException)
                { lock (learnedOpenGate) learnedOpenBatches.Remove(batch); DisposeSourceBatch(batch); }
            });
        }
        void DisposeLearnedInputs()
        {
            ++learnedSourceGeneration; learnedSourcesOpening = false; CancelLearnedOpenBatches();
            var view = learnedInputRouting; learnedInputRouting = null;
            if (view != null) view.Dispose();
            foreach (var source in learnedSources.Values) source.Dispose();
            learnedSources.Clear();
        }
        void EnsureLearnedPressureRange()
        {
            if (reader != null) { learnedPressureIdentity = null; return; }
            if (!HasLearnedInputs)
            {
                if (calibration != null && calibration.DeviceIdentity == learnedPressureIdentity) calibration = null;
                learnedPressureIdentity = null; return;
            }
            string identity, fingerprint;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                identity = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes("logical-pressure\n" + profilePath))).Replace("-", "").ToLowerInvariant();
                fingerprint = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes("normalized-logical-pressure-v1"))).Replace("-", "").ToLowerInvariant();
            }
            if (calibration == null || calibration.DeviceIdentity != identity) calibration = store.LoadCalibration(identity, fingerprint);
            learnedPressureIdentity = identity;
        }
        bool AutomaticallyKnownKey(int key)
        {
            return reader != null && reader.IsReading && automaticLayoutAvailable && keyboard.LayoutModel != null && keyboard.LayoutModel.FindByIndex(key) != null ||
                reader != null && reader.IsReading && keymap != null && keymap.Entries.Any(entry => entry.KeyIndex == key);
        }
        bool KnownInput(int key)
        { return AutomaticallyKnownKey(key) || UiReadProfile.LearnedInputs != null && UiReadProfile.LearnedInputs.Any(binding => binding.KeyIndex == key); }
        int[] UnknownSelectedInputs()
        {
            var selected = new HashSet<int>(SelectedKeys());
            var layout = keyboard.LayoutModel;
            var visual = layout == null ? new int[0] : layout.Keys.Where(key => key.KeyIndex.HasValue && selected.Contains(key.KeyIndex.Value))
                .OrderBy(key => key.Bounds.Y).ThenBy(key => key.Bounds.X).Select(key => key.KeyIndex.Value).ToArray();
            return visual.Concat(selected.OrderBy(key => key)).Distinct().Where(key => !KnownInput(key)).ToArray();
        }
        void AddLearningMenu(ContextMenuStrip menu)
        {
            learnUnknownMenu = new ToolStripMenuItem(); menu.Items.Insert(0, learnUnknownMenu);
            var forget = new ToolStripMenuItem(Tr("Gelernte Eingaben vergessen", "Forget learned inputs")); menu.Items.Insert(1, forget); menu.Items.Insert(2, new ToolStripSeparator());
            forget.Click += delegate { Attempt(delegate {
                var edited = history.Current; var selected = new HashSet<int>(SelectedKeys());
                if (edited.LearnedInputs == null) return;
                edited.LearnedInputs.RemoveAll(binding => selected.Contains(binding.KeyIndex));
                if (edited.LearnedInputs.Count == 0) edited.LearnedInputs = null;
                Commit(edited); SaveProfile();
            }); };
            learnUnknownMenu.Click += delegate { Attempt(LearnUnknownInputs); };
            menu.Opening += delegate {
                int count = UnknownSelectedInputs().Length;
                learnUnknownMenu.Text = count == 0 ? Tr("Eingaben bereits bekannt", "Inputs already identified") :
                    String.Format(Tr("Unbekannte Eingaben lernen … ({0})", "Learn unknown inputs… ({0})"), count);
                learnUnknownMenu.Enabled = count != 0 && !closing && !deviceDetachInProgress;
                forget.Visible = UiReadProfile.LearnedInputs != null && UiReadProfile.LearnedInputs.Any(binding => SelectedKeys().Contains(binding.KeyIndex));
            };
        }
        static LearnInputDeviceChoice[] DiscoverLearnInputDevices(LearnInputDeviceChoice primary)
        {
            var choices = new List<LearnInputDeviceChoice>(); if (primary != null) choices.Add(primary);
            foreach (var device in HidInventory.Enumerate())
            {
                if (device.error != null) continue;
                var selected = device;
                string name = String.IsNullOrWhiteSpace(device.product) ? String.Format("USB {0:X4}:{1:X4}", device.vendorId, device.productId) : device.product;
                if (KeyboardLearningSource.IsEligible(device)) choices.Add(new LearnInputDeviceChoice { Backend = "keyboard", Name = name + " · " + Tr("Tastatur", "Keyboard"),
                    DeviceId = KeyboardLearningSource.IdentifyDevice(device), Open = delegate { return KeyboardLearningSource.Open(selected); } });
                else if (HidLearningSource.IsEligible(device)) choices.Add(new LearnInputDeviceChoice { Backend = "hid", Name = name,
                    DeviceId = HidLearningSource.IdentifyDevice(device), Open = delegate { return HidLearningSource.Open(selected); } });
            }
            return choices.GroupBy(option => option.DeviceId).Select(group => group.First()).ToArray();
        }
        void LearnUnknownInputs()
        {
            int[] unknown = UnknownSelectedInputs(); if (unknown.Length == 0) return;
            FlushInputDraft(); CancelPressureCapture(); CancelInputThresholdCapture(); CancelMappingDrag();
            runtime.Disable(Tr("Eingaben lernen – Controller aus", "Learning inputs – controllers off"));
            int[] selection = SelectedKeys(); var originalHistory = history; var token = history.SnapshotToken; var input = reader;
            var layout = keyboard.LayoutModel; string path = profilePath;
            var labels = unknown.Select(Label).ToArray();
            var names = Enumerable.Range(0, 256).Select(Label).ToArray();
            var known = new HashSet<int>(Enumerable.Range(0, 256).Where(AutomaticallyKnownKey));
            LearnInputDeviceChoice primary = input == null || !input.IsReading ? null : new LearnInputDeviceChoice {
                Backend = "travel", DeviceId = LearnedInputRouting.TravelDeviceId(input), Name = input.Device.product + " · Analog",
                Open = delegate { return new TravelLearningSource(input, index => names[index], known.Contains); } };
            Func<bool> valid = delegate { return !closing && !deviceDetachInProgress && !applicationResourcesDisposed &&
                Object.ReferenceEquals(originalHistory, history) && Object.ReferenceEquals(token, history.SnapshotToken) &&
                Object.ReferenceEquals(input, reader) && Object.ReferenceEquals(layout, keyboard.LayoutModel) && path == profilePath && selection.SequenceEqual(SelectedKeys()); };
            using (var dialog = new LearnInputsDialog(unknown, labels, selection.Length - unknown.Length,
                delegate { return DiscoverLearnInputDevices(primary); }, valid))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !valid()) return;
                Profile edited = history.Current;
                if (edited.LearnedInputs == null) edited.LearnedInputs = new List<LearnedKeyBinding>();
                foreach (var binding in dialog.Bindings)
                {
                    if (!unknown.Contains(binding.KeyIndex) || edited.LearnedInputs.Any(item => item.KeyIndex == binding.KeyIndex))
                        throw new InvalidOperationException(Tr("Eine vorhandene Zuordnung wurde inzwischen geändert.", "An existing assignment has changed."));
                    edited.LearnedInputs.Add(binding);
                }
                MappingValidation.RequireValid(edited);
                // Publish the whole batch only after the atomic profile save succeeds.
                WorkspaceStore.WriteAtomic(profilePath, ProfileJson.Serialize(edited));
                var source = dialog.TakeSource();
                if (source != null)
                {
                    if (dialog.Bindings.Any(binding => binding.Backend != "travel"))
                    {
                        ILearnedInputDeviceSource previous;
                        if (learnedSources.TryGetValue(source.DeviceId, out previous)) previous.Dispose();
                        learnedSources[source.DeviceId] = source;
                    }
                    else source.Dispose();
                }
                Commit(edited); RefreshKeySources(); UpdateKeyCard();
                store.Event("Learned input batch saved: " + dialog.Bindings.Length + " keys");
            }
        }
        void RefreshKeySources()
        {
            var entries = UiReadProfile.LearnedInputs ?? new List<LearnedKeyBinding>();
            for (int index = 0; index < 256; index++)
            {
                var entry = entries.FirstOrDefault(binding => binding.KeyIndex == index);
                keyboard.SetInputSourceDescription(index, entry == null ? null : entry.SourceName + " · " + entry.ControlId);
            }
        }
        string LearnedSourceHint(int[] keys)
        {
            var entries = UiReadProfile.LearnedInputs;
            if (entries == null) return "";
            var names = entries.Where(binding => keys.Contains(binding.KeyIndex)).Select(binding => binding.SourceName + " · " + binding.ControlId).Distinct().ToArray();
            return names.Length == 0 ? "" : " · " + (names.Length == 1 ? names[0] : Tr("Gelernte Eingaben", "Learned inputs"));
        }
        int? PhysicalInputKey(int index)
        { return HasLearnedInputs ? InputView.ResolvePhysicalKey(index) : (int?)index; }
        bool TryInputSuppressionKey(Profile profile, int index, out SuppressionKey result)
        {
            result = new SuppressionKey();
            var route = profile.LearnedInputs == null ? null : profile.LearnedInputs.FirstOrDefault(binding => binding.KeyIndex == index);
            if (route != null && route.Backend == "keyboard")
            {
                ILearnedInputDeviceSource source;
                return learnedSources.TryGetValue(route.SourceDeviceId, out source) && source.IsReading &&
                    KeyboardLearningSource.TryParseControlId(route.ControlId, out result);
            }
            int? physical = PhysicalInputKey(index); if (!physical.HasValue) return false;
            var key = keyboard.LayoutModel == null ? null : keyboard.LayoutModel.FindByIndex(physical.Value);
            return key != null && KeyboardScanCodes.TryGet(key.Code, out result);
        }
        bool CanCapturePressure(int[] selection)
        {
            if (!LiveInputReading || calibration == null || selection.Length == 0) return false;
            var routes = UiReadProfile.LearnedInputs;
            return routes == null || selection.All(index => !routes.Any(route => route.KeyIndex == index && route.Kind != (int)InputControlKind.Absolute));
        }
    }
}
