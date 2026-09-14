using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        const string RgbStartupAmbiguous = "Configured key colors were found, but their original colors are ambiguous. Lighting was left unchanged.";
        const string RgbStartupDeclined = "Startup cleanup was not approved. Lighting stays unchanged until the keyboard is reconnected.";
        bool rgbStartupPreferenceLoaded, rgbStartupAutoRepair;
        RgbStartupConfirmationDialog rgbStartupDialog;
        RgbBackupWork rgbStartupDialogWork;

        void LoadRgbStartupPreference(bool refresh = false)
        {
            if (rgbStartupPreferenceLoaded && !refresh) return;
            rgbStartupPreferenceLoaded = true;
            rgbStartupAutoRepair = false;
            try { rgbStartupAutoRepair = RgbStartupPreferences.Load(store.Root); }
            catch (Exception error) { store.Event("Startup lighting preference unavailable: " + error.Message); }
        }

        void SetRgbStartupAutomatic(bool enabled)
        {
            RgbStartupPreferences.Save(store.Root, enabled);
            rgbStartupAutoRepair = enabled; rgbStartupPreferenceLoaded = true;
        }

        void AddRgbStartupPreferenceItem(ContextMenuStrip menu)
        {
            var option = new ToolStripMenuItem();
            Action refresh = delegate
            {
                LoadRgbStartupPreference();
                option.Text = Tr("Erkannte Tastenfarben beim Start automatisch bereinigen", "Automatically clean recognized key colors at startup");
                option.Checked = rgbStartupAutoRepair;
                option.ToolTipText = Tr("Nur passende Farbmuster mit eindeutig bestimmbaren Ersatzfarben. Ohne Häkchen wird vorher nachgefragt.",
                    "Only matching patterns with unambiguous replacement colors. When unchecked, ask first.");
            };
            refresh();
            option.Click += delegate { Attempt(delegate { SetRgbStartupAutomatic(!rgbStartupAutoRepair); }); refresh(); };
            menu.Items.Add(option); menu.Opening += delegate { refresh(); };
        }

        static bool AwaitRgbStartupApproval(RgbBackupWork work, Tk75RgbSnapshot observed, RgbStartupMarkerDecision proposal)
        {
            lock (work.Gate)
            {
                if (work.Abort || work.QuitAfterRestore) return false;
                if (work.StartupAutoRepair) return true;
                work.StartupObserved = observed; work.StartupProposal = proposal;
                work.StartupApprovalPending = true;
                try
                {
                    // No baseline is adopted and no write is queued while this
                    // waits. Shutdown/detach can cancel without involving UI.
                    while (work.StartupApproval == 0 && !work.Abort && !work.QuitAfterRestore)
                    {
                        Monitor.Wait(work.Gate, 250);
                        if (!work.Reader.IsReading || work.Reader.DeviceModelId != work.Model) return false;
                    }
                    return work.StartupApproval == 1 && !work.Abort && !work.QuitAfterRestore;
                }
                finally { work.StartupApprovalPending = false; }
            }
        }

        void CompleteRgbStartupApproval(RgbBackupWork work, bool approved, bool remember)
        {
            lock (work.Gate)
                if (!work.StartupApprovalPending || work.StartupApproval != 0 || work.Abort || work.QuitAfterRestore) return;
            Exception saveError = null;
            if (approved && remember)
                try { SetRgbStartupAutomatic(true); }
                catch (Exception error) { saveError = error; }
            lock (work.Gate)
            {
                work.StartupApproval = approved ? 1 : -1;
                Monitor.PulseAll(work.Gate);
            }
            // One-time approval remains valid if saving the future preference
            // failed. Report the disk error only after releasing the worker.
            if (saveError != null) throw saveError;
        }

        void RefreshRgbStartupConfirmation()
        {
            RgbBackupWork work = rgbBackupWork;
            if (rgbStartupDialog != null)
            {
                bool pending = false;
                if (work != null && Object.ReferenceEquals(work, rgbStartupDialogWork) && Object.ReferenceEquals(work.Reader, reader))
                    lock (work.Gate) pending = work.StartupApprovalPending && !work.Abort && !work.QuitAfterRestore;
                if (!pending) { rgbStartupDialog.DialogResult = DialogResult.Cancel; rgbStartupDialog.Close(); }
                return;
            }
            // Preview fixtures can inspect/respond to the pending decision but
            // never open a real prompt or silently grant consent.
            if (previewMode || work == null || !Object.ReferenceEquals(work.Reader, reader)) return;
            RgbStartupMarkerDecision proposal; Tk75RgbSnapshot observed;
            lock (work.Gate)
            {
                if (!work.StartupApprovalPending || work.StartupApprovalShown || work.Abort || work.QuitAfterRestore) return;
                work.StartupApprovalShown = true; proposal = work.StartupProposal; observed = work.StartupObserved;
            }
            var layout = work.Model == 3591 ? KeyboardLayout.Tk75Iso() : KeyboardLayout.Tk75Ansi();
            byte[] before = observed.Picture, after = proposal.Repaired.Picture;
            var lines = proposal.MarkerKeys.Select(delegate(int key)
            {
                KeyboardKeyDefinition definition = layout.FindByIndex(key);
                string name = definition == null ? key.ToString(CultureInfo.InvariantCulture) :
                    definition.GetLegend(UiText.Language == "en" ? KeyboardLegendStyle.Qwerty : KeyboardLegendStyle.Qwertz);
                return name + "   #" + RgbStartupColor(before, key) + " → #" + RgbStartupColor(after, key);
            });
            var dialog = new RgbStartupConfirmationDialog(String.Join(Environment.NewLine, lines));
            rgbStartupDialog = dialog; rgbStartupDialogWork = work;
            dialog.ShowInTaskbar = !Visible;
            if (!Visible) dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.FormClosed += delegate
            {
                rgbStartupDialog = null; rgbStartupDialogWork = null;
                try { Attempt(delegate { CompleteRgbStartupApproval(work, dialog.DialogResult == DialogResult.OK, dialog.AutoRepairChecked); }); }
                finally { dialog.Dispose(); }
            };
            // Modeless ownership keeps normal close and Windows shutdown free
            // to finish even when the user never responds to the question.
            dialog.Show(this);
        }

        static string RgbStartupColor(byte[] picture, int key)
        { return ((picture[key * 3] << 16) | (picture[key * 3 + 1] << 8) | picture[key * 3 + 2]).ToString("X6", CultureInfo.InvariantCulture); }

        Dictionary<int, HashSet<int>> BuildRgbStartupMarkerKeys(Profile profile, uint model)
        {
            var result = new Dictionary<int, HashSet<int>>();
            Action<int, int?> add = delegate(int color, int? key)
            {
                int matrix;
                if (!key.HasValue || !Tk75RgbProtocol.TryGetRgbMatrixIndex(model, key.Value, out matrix)) return;
                HashSet<int> keys;
                if (!result.TryGetValue(color, out keys)) result.Add(color, keys = new HashSet<int>());
                keys.Add(key.Value);
            };
            // These are recognition candidates, not the active output plan.
            // Disabled bindings, hotkeys and lighting options still describe
            // colors left behind by an earlier session.
            foreach (var controller in ControllerRouting.EffectiveControllers(profile))
                foreach (var binding in profile.Bindings)
                    if (String.Equals(binding.ControllerId, controller.Id, StringComparison.Ordinal))
                        add(RgbOverridePlan.GetControllerColor(profile, controller.Id), PhysicalInputKey(binding.KeyIndex));
            KeyboardLayout layout = model == 3591 ? KeyboardLayout.Tk75Iso() : KeyboardLayout.Tk75Ansi();
            add(profile.ModeSwitchRgbColor, HotkeyPhysicalKeyResolver.ResolveIndex(layout, profile.ModeSwitchHotkey.KeyCode));
            return result;
        }

        static object EncodeRgbStartupCandidates(Dictionary<int, HashSet<int>> candidates)
        {
            return candidates.OrderBy(pair => pair.Key).Select(pair => new Dictionary<string, object> {
                { "Color", pair.Key }, { "Keys", pair.Value.OrderBy(key => key).ToArray() }
            }).ToArray();
        }

        static Dictionary<int, HashSet<int>> DecodeRgbStartupCandidates(Dictionary<string, object> journal)
        {
            object raw;
            if (!journal.TryGetValue("StartupCandidates", out raw) || !(raw is object[]))
                throw new InvalidDataException("Startup lighting candidates are missing.");
            object[] entries = (object[])raw;
            if (entries.Length > 33) throw new InvalidDataException("Too many startup lighting colors.");
            var result = new Dictionary<int, HashSet<int>>();
            foreach (object entry in entries)
            {
                var record = entry as Dictionary<string, object>;
                if (record == null) throw new InvalidDataException("Invalid startup lighting candidates.");
                int color = RgbNumber(record, "Color");
                if (color < 0 || color > 0xffffff || result.ContainsKey(color) || !record.TryGetValue("Keys", out raw) || !(raw is object[]))
                    throw new InvalidDataException("Invalid startup lighting color or keys.");
                var keys = new HashSet<int>();
                foreach (object key in (object[])raw)
                    if (!(key is int) || (int)key < 0 || (int)key > 255 || !keys.Add((int)key))
                        throw new InvalidDataException("Invalid startup lighting key.");
                if (keys.Count == 0) throw new InvalidDataException("Empty startup lighting color group.");
                result.Add(color, keys);
            }
            return result;
        }

        // A reconciled baseline is explicitly derived, never labelled a literal
        // device read. Re-evaluate its complete proof whenever the journal loads.
        static void ReadRgbStartupProvenance(RgbRecoveryGroup group, Dictionary<string, object> backup, string directory)
        {
            group.InitialExpected = group.Original;
            object kind;
            if (!backup.TryGetValue("BaselineKind", out kind)) return;
            if (!(kind is string) || (string)kind != "StartupMarkerReconciliation")
                throw new InvalidDataException("Unknown lighting baseline provenance.");
            var observed = RgbJournalSnapshot(backup, "StartupObserved");
            Tk75RgbSnapshot reference = backup.ContainsKey("StartupReferenceBase64") ? RgbJournalSnapshot(backup, "StartupReference") : null;
            var decision = RgbStartupMarkers.Assess(observed, DecodeRgbStartupCandidates(backup), reference);
            if (!decision.CanRepair || !Tk75RgbExchange.Equivalent(decision.Repaired, group.Original))
                throw new InvalidDataException("Startup lighting baseline does not match its recorded recognition proof.");
            group.InitialExpected = observed;
            if (!backup.ContainsKey("SupersedesOriginalBackup")) return;
            string previous = RgbText(backup, "SupersedesOriginalBackup");
            string previousPath = RgbReferencedFile(directory, previous);
            if (String.Equals(previousPath, group.BackupFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A startup lighting baseline cannot supersede itself.");
            var previousBackup = ReadRgbJournal(previousPath);
            if (RgbNumber(previousBackup, "SchemaVersion") != 1 || RgbText(previousBackup, "Phase") != "Complete" ||
                RgbText(previousBackup, "IdentitySha256") != RgbText(backup, "IdentitySha256") || reference == null ||
                reference.ModelId != observed.ModelId || reference.Profile != observed.Profile || reference.Layer != observed.Layer ||
                !Tk75RgbExchange.Equivalent(RgbJournalSnapshot(previousBackup, "Snapshot"), reference))
                throw new InvalidDataException("Startup lighting reference does not match the preceding original backup.");
            group.SupersedesBackup = previous;
            group.SupersedesSequence = RgbNumber(backup, "SupersedesThroughSequence");
            if (group.SupersedesSequence < 1) throw new InvalidDataException("Invalid startup lighting supersession sequence.");
        }

        static HashSet<string> SupersededRgbBackups(Dictionary<string, RgbRecoveryGroup> groups)
        {
            var superseded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in groups)
            {
                RgbRecoveryGroup group = pair.Value;
                if (group.SupersedesBackup == null) continue;
                RgbRecoveryGroup previous;
                if (!groups.TryGetValue(group.SupersedesBackup, out previous) || previous.Sequence != group.SupersedesSequence ||
                    !superseded.Add(group.SupersedesBackup))
                    throw new InvalidDataException("The preceding lighting journal changed or has competing startup reconciliations.");
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pair.Key };
                while (group.SupersedesBackup != null)
                {
                    if (!visited.Add(group.SupersedesBackup)) throw new InvalidDataException("Cyclic startup lighting history.");
                    if (!groups.TryGetValue(group.SupersedesBackup, out group)) throw new InvalidDataException("Missing preceding startup lighting history.");
                }
            }
            return superseded;
        }
    }
}
