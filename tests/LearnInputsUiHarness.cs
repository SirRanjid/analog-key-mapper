using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        sealed class LearningUiSource : ILearnedInputDeviceSource
        {
            readonly Dictionary<string, double> values = new Dictionary<string, double>();
            readonly object gate = new object();
            public string DeviceId { get; private set; }
            public string DisplayName { get { return "Synthetic controls"; } }
            public bool IsReading { get; set; }
            public InputControlDescriptor[] Controls { get; private set; }
            public string Status { get { return IsReading ? "Reading" : "Removed"; } }
            public volatile bool Disposed;
            public event Action<InputControlSample> Sample;
            public LearningUiSource(char identity, params InputControlDescriptor[] controls)
            {
                DeviceId = new string(identity, 64); Controls = controls; IsReading = true;
                foreach (var control in controls) values[control.ControlId] = control.Neutral ?? control.LogicalMinimum;
            }
            public bool TryGetValue(string id, out double value)
            { lock (gate) { value = 0; return IsReading && values.TryGetValue(id, out value); } }
            public void Send(string id, double value)
            {
                lock (gate) values[id] = value;
                var handler = Sample; if (handler != null) handler(new InputControlSample(id, value, HidLearningSource.TimestampMilliseconds));
            }
            public Action<InputControlSample> SnapshotHandlers() { return Sample; }
            public void Dispose() { Disposed = true; IsReading = false; }
        }
        static InputControlDescriptor LearningButton(string id, int? known)
        { return new InputControlDescriptor(id, id == "button-1" ? "Pedal button" : "Thumb button", InputControlKind.Button, 0, 1, 0, known); }
        static LearnInputDeviceChoice LearningChoice(LearningUiSource source, string backend)
        { return new LearnInputDeviceChoice { Backend = backend, Name = source.DisplayName, DeviceId = source.DeviceId, Open = delegate { return source; } }; }
        static void AwaitLearning(Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(2); }
            Check(condition(), message);
        }
        static void OpenLearningUi(LearnInputsDialog dialog)
        {
            ShowDialogOffline(dialog);
            AwaitLearning(delegate { return Field<ILearnedInputDeviceSource>(dialog, "source") != null; }, "The wizard asynchronously opens only its synthetic selected device.");
            Check(dialog.Left < -10000 && !dialog.ShowInTaskbar, "The learning fixture never owns a foreground or hardware window.");
        }
        static void AdvanceLearningUi(LearnInputsDialog dialog, long milliseconds)
        {
            var capture = Field<InputLearningCapture>(dialog, "capture");
            if (capture != null)
            {
                long time = Field<long>(dialog, "captureTimestamp") + milliseconds;
                typeof(LearnInputsDialog).GetField("captureTimestamp", Private).SetValue(dialog, time); capture.Advance(time);
            }
            DialogCall(dialog, "RefreshCapture"); Application.DoEvents();
        }
        static void LearningGesture(LearnInputsDialog dialog, LearningUiSource source, string id)
        {
            AdvanceLearningUi(dialog, InputLearningCapture.QuietMilliseconds + 1);
            source.Send(id, 1); DialogCall(dialog, "RefreshCapture");
            Check(Field<InputLearningCapture>(dialog, "capture").State == InputLearningCaptureState.Capturing, "A fresh press starts the current staged assignment.");
            source.Send(id, 0); DialogCall(dialog, "RefreshCapture");
            AdvanceLearningUi(dialog, InputLearningCapture.ReleaseSettleMilliseconds + 1);
        }
        static void CaptureLearningUi(LearnInputsDialog dialog, string artifacts, string name)
        {
            dialog.PerformLayout(); Application.DoEvents();
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(artifacts, name + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
        }
        static void RunLearningWizardSequence(string artifacts)
        {
            var source = new LearningUiSource('a', LearningButton("button-1", null), LearningButton("button-2", null));
            var choices = new[] { LearningChoice(source, "hid") };
            using (var dialog = new LearnInputsDialog(new[] { 14, 9 }, new[] { "W", "A" }, 3, delegate { return choices; }, delegate { return true; }))
            {
                OpenLearningUi(dialog);
                Check(!Field<Button>(dialog, "apply").Enabled && Field<int>(dialog, "current") == 0, "Learning begins at the first selected key with Apply unavailable.");
                Check(Field<DataGridView>(dialog, "queue").Rows.Count == 2, "Only unresolved selected keys enter the queue.");
                Check((bool)typeof(LearnInputsDialog).GetMethod("ProcessDialogKey", Private).Invoke(dialog, new object[] { Keys.Enter }), "A physical Enter cannot activate the focused wizard button.");
                Check((bool)typeof(LearnInputsDialog).GetMethod("ProcessDialogKey", Private).Invoke(dialog, new object[] { Keys.Escape }), "A physical Escape cannot cancel the staged transaction.");
                Check((bool)typeof(LearnInputsDialog).GetMethod("ProcessDialogKey", Private).Invoke(dialog, new object[] { Keys.Tab }), "A captured Tab cannot navigate while it is being recorded.");
                CaptureLearningUi(dialog, artifacts, "preview-input-learning-en");
                LearningGesture(dialog, source, "button-1");
                Check(dialog.Bindings.Length == 1 && dialog.Bindings[0].KeyIndex == 14 && Field<int>(dialog, "current") == 1, "One complete gesture advances and retains only a staged binding for W.");
                LearningGesture(dialog, source, "button-1");
                Check(Field<bool>(dialog, "duplicate") && dialog.Bindings.Length == 1 && Field<int>(dialog, "current") == 1, "A duplicate source cannot silently replace or reuse the earlier assignment.");
                Field<Button>(dialog, "retry").PerformClick(); LearningGesture(dialog, source, "button-2");
                Check(dialog.Bindings.Length == 2 && Field<Button>(dialog, "apply").Enabled, "Distinct inputs reach a single final review.");
                Field<Button>(dialog, "back").PerformClick();
                Check(dialog.Bindings.Length == 1 && Field<int>(dialog, "current") == 1, "Back retains the previous key and reopens only the last assignment.");
                Field<Button>(dialog, "skip").PerformClick();
                Check(dialog.Bindings.Length == 1 && (string)Field<DataGridView>(dialog, "queue").Rows[1].Cells[1].Value == "Skipped", "Skipping clears the reopened assignment and displays an explicit row state.");
                Field<Button>(dialog, "back").PerformClick(); LearningGesture(dialog, source, "button-2");
                Check(dialog.Bindings.Length == 2 && !((string)Field<DataGridView>(dialog, "queue").Rows[1].Cells[1].Value).Contains("Skipped"), "Relearning the skipped key restores its result.");
                CaptureLearningUi(dialog, artifacts, "preview-input-learning-review-en");
                Field<Button>(dialog, "apply").PerformClick();
                Check(dialog.DialogResult == DialogResult.OK && !source.Disposed, "Apply closes the review while preserving its source for atomic profile integration.");
                Check(object.ReferenceEquals(dialog.TakeSource(), source) && dialog.TakeSource() == null, "Source ownership transfers exactly once after OK.");
            }
            Check(!source.Disposed, "Disposing an applied dialog does not close the transferred source."); source.Dispose();
        }
        static void RunLearningWizardGuards(string artifacts)
        {
            var automatic = new LearningUiSource('b', LearningButton("button-1", 14), LearningButton("button-2", null));
            using (var dialog = new LearnInputsDialog(new[] { 14, 9 }, new[] { "W", "A" }, 0,
                delegate { return new[] { LearningChoice(automatic, "keyboard") }; }, delegate { return true; }))
            {
                OpenLearningUi(dialog);
                Check(Field<int>(dialog, "current") == 1 && dialog.Bindings.Length == 1 && dialog.Bindings[0].KeyIndex == 14, "A standard keyboard usage identifies and skips its known key automatically.");
                Check(((string)Field<DataGridView>(dialog, "queue").Rows[0].Cells[1].Value).Contains("automatic"), "Automatically identified keys remain explicit in the review queue.");
                automatic.IsReading = false; DialogCall(dialog, "RefreshCapture");
                Check(!Field<Button>(dialog, "apply").Enabled && !Field<Button>(dialog, "skip").Enabled && Field<Button>(dialog, "retry").Enabled, "Device removal pauses the transaction and exposes retry without allowing partial application.");
                Field<Button>(dialog, "cancel").PerformClick();
                Check(dialog.DialogResult == DialogResult.Cancel && dialog.TakeSource() == null, "Cancel never transfers a device or implies successful application.");
            }
            AwaitLearning(delegate { return automatic.Disposed; }, "Canceled device cleanup completes off the UI thread.");
            bool valid = true;
            var guarded = new LearningUiSource('c', LearningButton("button-1", null));
            using (var dialog = new LearnInputsDialog(new[] { 14 }, new[] { "W" }, 0,
                delegate { return new[] { LearningChoice(guarded, "travel") }; }, delegate { return valid; }))
            {
                OpenLearningUi(dialog); valid = false; DialogCall(dialog, "RefreshCapture");
                Check(!Field<Button>(dialog, "retry").Enabled && !Field<Button>(dialog, "apply").Enabled, "Changing the profile context cannot be bypassed through retry.");
                Field<Button>(dialog, "cancel").PerformClick();
            }
            AwaitLearning(delegate { return guarded.Disposed; }, "A stale profile context still releases its selected device.");
            var late = new LearningUiSource('d', LearningButton("button-1", null));
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var dialog = new LearnInputsDialog(new[] { 14 }, new[] { "W" }, 0, delegate {
                return new[] { new LearnInputDeviceChoice { Backend = "hid", Name = "Delayed input", DeviceId = late.DeviceId,
                    Open = delegate { entered.Set(); release.WaitOne(2000); return late; } } };
            }, delegate { return true; }))
            {
                ShowDialogOffline(dialog); AwaitLearning(delegate { return entered.WaitOne(0); }, "The synthetic connection is pending independently of the UI.");
                Field<Button>(dialog, "cancel").PerformClick(); release.Set();
                AwaitLearning(delegate { return late.Disposed; }, "A connection completing after Cancel cannot orphan a source.");
            }
            string language = UiText.Language; UiText.SetLanguage("de");
            try
            {
                var german = new LearningUiSource('e', LearningButton("button-1", null));
                using (var dialog = new LearnInputsDialog(new[] { 14 }, new[] { "W" }, 1,
                    delegate { return new[] { LearningChoice(german, "hid") }; }, delegate { return true; }))
                {
                    OpenLearningUi(dialog); Equal("Unbekannte Eingaben lernen", dialog.Text, "The wizard consistently follows German UI selection.");
                    CaptureLearningUi(dialog, artifacts, "preview-input-learning-de");
                    dialog.Size = dialog.MinimumSize; dialog.PerformLayout(); Application.DoEvents();
                    foreach (string field in new[] { "back", "skip", "retry", "finish", "apply", "cancel" })
                    { var button = Field<Button>(dialog, field); if (button.Visible) Check(button.Right <= button.Parent.ClientSize.Width, "Wizard actions fit the minimum supported dialog width."); }
                    Field<Button>(dialog, "cancel").PerformClick();
                }
            }
            finally { UiText.SetLanguage(language); }
        }
        static LearnedKeyBinding LearningIntegrationBinding(int key, LearningUiSource source, string control)
        {
            var descriptor = source.Controls.Single(item => item.ControlId == control);
            return new LearnedKeyBinding { KeyIndex = key, Backend = "hid", SourceDeviceId = source.DeviceId,
                SourceName = source.DisplayName, ControlId = control, Kind = (int)descriptor.Kind,
                Minimum = descriptor.LogicalMinimum, Maximum = descriptor.LogicalMaximum, Rest = descriptor.Neutral ?? descriptor.LogicalMinimum,
                Active = descriptor.LogicalMaximum, Direction = 1 };
        }
        static void RefreshLearningMenu(MainForm form)
        {
            var menu = Field<DataGridView>(form, "keys").ContextMenuStrip;
            typeof(ToolStripDropDown).GetMethod("OnOpening", Private).Invoke(menu, new object[] { new CancelEventArgs() });
        }
        static Dictionary<int, int> LearningRgbPlan(MainForm form, Profile profile, string artifacts, int? shortcut)
        {
            Type workType = typeof(MainForm).GetNestedType("RgbBackupWork", BindingFlags.NonPublic);
            object work = Activator.CreateInstance(workType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { null, (uint)3591, artifacts }, null);
            object plan = Call(form, "GetRgbLightingPlan", work, profile, new[] { "main" }, false, shortcut.HasValue, shortcut,
                Field<VisualKeyboard>(form, "keyboard").LayoutModel);
            return Field<Dictionary<int, int>>(plan, "Colors");
        }
        static void RunLearningMainFormIntegration(MainForm form, string artifacts)
        {
            AssertPassive(form);
            var source = new LearningUiSource('f', new InputControlDescriptor("pedal", "Pedal travel", InputControlKind.Absolute, 0, 1000, 0, null),
                LearningButton("button-1", null));
            var owned = Field<Dictionary<string, ILearnedInputDeviceSource>>(form, "learnedSources");
            owned.Add(source.DeviceId, source);
            Profile configured = Current(form);
            configured.LearnedInputs = new List<LearnedKeyBinding> { LearningIntegrationBinding(14, source, "pedal"), LearningIntegrationBinding(255, source, "button-1") };
            Call(form, "Commit", configured); Pump(form);
            Check(Field<object>(form, "reader") == null && (bool)typeof(MainForm).GetProperty("LiveInputReading", Private).GetValue(form, null),
                "A connected learned input becomes live without opening a keyboard reader.");
            Check(Field<DataGridView>(form, "keys").Rows[255].Visible,
                "A learned destination outside the drawn keyboard remains available in the key list without output bindings.");
            source.Send("pedal", 500); source.Send("button-1", 1);
            var view = (LearnedInputRouting)typeof(MainForm).GetProperty("InputView", Private).GetValue(form, null);
            double scale = Field<KeyboardPressureRange>(form, "sharedPressureRange").ScaleMaximum;
            var raw = new Dictionary<int, double>(); view.CopyRawSnapshot(500, raw);
            Check(raw[14] == Math.Round(scale * .5) && raw[255] == scale,
                "MainForm projects analog and binary external inputs onto its shared pressure scale.");
            var ui = (KeyStateSnapshot[])Call(form, "InputUiSnapshot");
            Check(ui.Single(item => item.KeyIndex == 14).RawValue == raw[14] && ui.Single(item => item.KeyIndex == 255).RawValue == raw[255],
                "MainForm live previews read the same routed values as controller processing.");
            foreach (System.Collections.DictionaryEntry slot in Field<System.Collections.IDictionary>(Field<MultiControllerSession>(form, "runtime"), "slots"))
            {
                object session = slot.Value.GetType().GetField("Session").GetValue(slot.Value);
                var mapping = Field<MappingSession>(session, "inner");
                var copy = Field<Action<double, Dictionary<int, double>>>(mapping, "copyRawSnapshot");
                var mapped = new Dictionary<int, double>(); copy(500, mapped);
                Check(mapped[14] == raw[14] && mapped[255] == raw[255], "Every real controller session is wired to the learned routing view.");
            }
            SelectKeys(form, 9, 14); RefreshLearningMenu(form);
            var unknown = (int[])Call(form, "UnknownSelectedInputs");
            Check(unknown.SequenceEqual(new[] { 9 }) && Field<ToolStripMenuItem>(form, "learnUnknownMenu").Enabled,
                "The selection menu queues only unresolved inputs even when both keys have controller outputs.");
            SelectKeys(form, 14); RefreshLearningMenu(form);
            Check(!Field<ToolStripMenuItem>(form, "learnUnknownMenu").Enabled,
                "An already learned input cannot be overwritten through Learn unknown inputs.");
            string beforeForget = Json(Current(form));
            var forget = Field<DataGridView>(form, "keys").ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Forget learned inputs");
            Check(forget.Available, "The menu exposes forgetting only for selected learned inputs.");
            forget.PerformClick(); Pump(form);
            Check(Current(form).LearnedInputs.Count == 1 && Current(form).LearnedInputs[0].KeyIndex == 255,
                "Forgetting changes only the selected logical key while preserving the other source and settings.");
            Call(form, "Undo"); Pump(form);
            Equal(beforeForget, Json(Current(form)), "One Undo restores the whole forgotten assignment without additional edits.");
            Check((int?)Call(form, "PhysicalInputKey", 14) == null && (int?)Call(form, "PhysicalInputKey", 9) == 9,
                "An external pedal has no keyboard lighting index, while an ordinary physical key retains its index.");
            Profile lighting = Current(form); lighting.RgbOverrideEnabled = true;
            lighting.ModeSwitchLightingEnabled = true; lighting.ModeSwitchHotkey.Enabled = true; lighting.ModeSwitchRgbColor = 0x123456;
            Call(form, "Commit", lighting);
            var colors = LearningRgbPlan(form, Current(form), artifacts, null);
            Check(!colors.ContainsKey(14), "The RGB planner excludes a pedal-driven logical key from physical keyboard overrides.");
            colors = LearningRgbPlan(form, Current(form), artifacts, 14);
            Check(colors.ContainsKey(14) && colors[14] == 0x123456, "A real mode shortcut stays physical even when the same logical key has an external route.");
            Profile externalProfile = Current(form);
            using (var input = new DialogInput())
            {
                typeof(MainForm).GetField("reader", Private).SetValue(form, input.Reader);
                var remapped = Current(form);
                remapped.LearnedInputs.RemoveAll(binding => binding.KeyIndex == 14);
                remapped.LearnedInputs.Add(new LearnedKeyBinding { KeyIndex = 14, Backend = "travel", SourceDeviceId = LearnedInputRouting.TravelDeviceId(input.Reader),
                    SourceName = "Synthetic keyboard", ControlId = "key:9", SourceKeyIndex = 9, Kind = (int)InputControlKind.Absolute,
                    Minimum = 0, Maximum = 65535, Rest = 0, Active = 385, Direction = 1 });
                try
                {
                    Call(form, "Commit", remapped);
                    Check((int?)Call(form, "PhysicalInputKey", 14) == 9, "A learned travel route resolves its actual hardware key for lighting and suppression.");
                    colors = LearningRgbPlan(form, Current(form), artifacts, null);
                    Check(!colors.ContainsKey(14) && colors.ContainsKey(9), "Travel-driven RGB follows the source key rather than the displayed logical target.");
                    colors = LearningRgbPlan(form, Current(form), artifacts, 14);
                    Check(colors.ContainsKey(9) && colors[14] == 0x123456, "Controller lighting follows travel routing while the independent mode marker keeps its physical key.");
                }
                finally { typeof(MainForm).GetField("reader", Private).SetValue(form, null); Call(form, "Commit", externalProfile); }
            }
            SelectKeys(form, 14); Call(form, "SavePressureRange", 50d, 350d, 600d); Call(form, "SaveProfile");
            var calibrationA = Field<CalibrationDocument>(form, "calibration");
            string pathA = Field<string>(form, "profilePath");
            var store = Field<WorkspaceStore>(form, "store");
            string pathB = store.NewProfilePath(); var second = Current(form); second.Name = "Second learned-input profile";
            WorkspaceStore.WriteAtomic(pathB, ProfileJson.Serialize(second)); Call(form, "LoadProfile", pathB);
            var calibrationB = Field<CalibrationDocument>(form, "calibration");
            Check(calibrationA.DeviceIdentity != calibrationB.DeviceIdentity && calibrationB.KeyRanges.Count == 0 && Field<KeyboardPressureRange>(form, "sharedPressureRange").ScaleMaximum == DefaultCalibration.Bottom,
                "Loading a different generic-input profile starts its own pressure ranges and global scale.");
            SelectKeys(form, 14); Call(form, "SavePressureRange", 10d, 250d, 500d);
            string plainPath = store.NewProfilePath(); WorkspaceStore.WriteAtomic(plainPath, ProfileJson.Serialize(new Profile { Name = "No learned input" }));
            Call(form, "LoadProfile", plainPath);
            Check(Field<KeyboardPressureRange>(form, "sharedPressureRange").ScaleMaximum == DefaultCalibration.Bottom &&
                Field<KeyboardPressureRange>(form, "sharedPressureRange").ForKey(14).Rest == DefaultCalibration.Rest,
                "A profile without learned inputs cannot inherit the previous generic device pressure configuration.");
            Check(source.Disposed, "Leaving all learned inputs releases the previously owned external source.");
            Call(form, "LoadProfile", pathA);
            var restored = Field<KeyboardPressureRange>(form, "sharedPressureRange");
            Check(restored.ScaleMaximum == 600 && restored.ForKey(14).Rest == 50 && restored.ForKey(14).Bottom == 350,
                "Returning to the first profile restores only its own persisted per-key range and scale.");
            Check(!view.IsReading, "Superseded routing views are invalidated as profiles change.");
            AssertPassive(form);
        }
        static void RunLearnInputsUi(MainForm preview, string artifacts)
        { RunLearningWizardSequence(artifacts); RunLearningWizardGuards(artifacts); RunLearningMainFormIntegration(preview, artifacts); AssertPassive(preview); }
    }
}
