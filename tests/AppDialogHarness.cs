using System;
using System.Collections;
using System.Collections.Generic;
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
        sealed class DialogSource : IReportSource
        {
            readonly object gate = new object();
            readonly Queue<object> pending = new Queue<object>();
            readonly AutoResetEvent available = new AutoResetEvent(false);
            public void Push(object value) { lock (gate) pending.Enqueue(value); available.Set(); }
            public byte[] Read(int timeoutMs)
            {
                object value = null;
                lock (gate) if (pending.Count > 0) value = pending.Dequeue();
                if (value == null)
                {
                    available.WaitOne(timeoutMs);
                    lock (gate) if (pending.Count > 0) value = pending.Dequeue();
                }
                var failure = value as Exception; if (failure != null) throw failure;
                return (byte[])value;
            }
            public void Dispose() { available.Dispose(); }
        }
        sealed class DialogInput : IDisposable
        {
            public readonly DialogSource Source = new DialogSource();
            public readonly ReaderSession Reader;
            public DialogInput()
            {
                var device = new CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "Synthetic dialog keyboard", version = 0x0403, usagePage = 65535, usage = 1, inputReportLength = 32 };
                device.reportCapabilities.Add(new Dictionary<string, object> { { "reportType", "input" }, { "kind", "value" }, { "reportId", 5 }, { "usagePage", 65535 }, { "bitSize", 8 }, { "reportCount", 31 } });
                Reader = new ReaderSession(device, new RongYuanTravel32(), delegate { return Source; });
                Reader.Start(); AwaitDialog(delegate { return Reader.IsReading; }, "Fake reader starts without a hardware source.");
            }
            public void Dispose() { Reader.Dispose(); }
        }

        static void AwaitDialog(Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            Check(condition(), message);
        }
        static object DialogCall(object dialog, string method)
        { return dialog.GetType().GetMethod(method, Private).Invoke(dialog, null); }
        static IEnumerable<Control> DialogControls(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (Control item in DialogControls(child)) yield return item; } }
        static void ShowDialogOffline(Form dialog)
        {
            dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-30000, -30000);
            dialog.Show(); Application.DoEvents();
            Check(Field<System.Windows.Forms.Timer>(dialog, "timer").Enabled, "Shown starts the dialog UI timer.");
            // Shown/real subscription is exercised; later ticks are deterministic.
            Field<System.Windows.Forms.Timer>(dialog, "timer").Stop();
            Check(dialog.Left < -10000 && !dialog.ShowInTaskbar, "Dialog stays offscreen and owns no foreign window.");
        }
        static void TickDialog(Form dialog)
        {
            typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Private).Invoke(Field<System.Windows.Forms.Timer>(dialog, "timer"), new object[] { EventArgs.Empty });
            if (!dialog.IsDisposed) { dialog.PerformLayout(); Application.DoEvents(); }
        }
        static void PushDialog(DialogInput input, int index, params int[] raw)
        {
            int completed = 0;
            Action<TravelSample, double> delivered = delegate { Interlocked.Increment(ref completed); };
            // Registered after the dialog's subscription: observing delivery means
            // its real Feed handler has finished processing every queued report.
            input.Reader.Sample += delivered;
            try
            {
                foreach (int value in raw)
                {
                    byte[] report = new byte[32]; report[0] = 5; report[1] = 27; report[2] = (byte)value; report[3] = (byte)(value >> 8); report[4] = (byte)index;
                    input.Source.Push(report);
                }
                AwaitDialog(delegate { return Volatile.Read(ref completed) == raw.Length; }, "Synthetic reports reach the actual dialog subscription.");
            }
            finally { input.Reader.Sample -= delivered; }
        }
        static void CaptureDialog(Form dialog, string path)
        {
            dialog.PerformLayout(); Application.DoEvents();
            foreach (Control control in DialogControls(dialog).Where(c => c.Visible && (c is Button || c is CheckBox || c is Label || c is ProgressBar)))
            {
                Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                {
                    Rectangle clipped = Rectangle.Intersect(bounds, parent.RectangleToScreen(parent.ClientRectangle));
                    Check(clipped.Width >= bounds.Width - 1 && clipped.Height >= bounds.Height - 1, "Dialog control fits its parent: " + control.Text);
                    if (parent == dialog) break;
                }
            }
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }

        static MainForm PressurePreview(string data, DialogInput input)
        {
            var form = new MainForm(data, true);
            try
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                form.Show(); Pump(form); form.MaximumSize = new Size(2048, 2048); SetPreviewClientSize(form, DefaultClientSize);
                var store = Field<WorkspaceStore>(form, "store");
                CalibrationDocument saved = store.LoadCalibration(new string('c', 64), input.Reader.Fingerprint);
                typeof(MainForm).GetField("reader", Private).SetValue(form, input.Reader);
                typeof(MainForm).GetField("calibration", Private).SetValue(form, saved);
                Call(form, "Configure"); Field<MultiControllerSession>(form, "runtime").SetReader(input.Reader);
                SelectKeys(form, 14); DetailMode(form, null); Call(form, "RefreshPressureRangeEditor");
                Check(!Field<System.Windows.Forms.Timer>(form, "uiTimer").Enabled && !Field<bool>(form, "discoveryEnabled"),
                    "Inline calibration uses only the injected source; hardware discovery and automatic UI ticks remain off.");
                return form;
            }
            catch { form.Dispose(); throw; }
        }
        static void ClickPressureCalibration(MainForm form)
        {
            var button = Field<Button>(form, "calibrateRange");
            Check(button.Enabled, "The inline calibration action is available for the selected synthetic key.");
            button.PerformClick();
        }
        static void CheckPressureRange(MainForm form, double minimum, double maximum, double scale)
        {
            var slider = Field<PressureRangeSlider>(form, "pressureRange");
            var shared = Field<KeyboardPressureRange>(form, "sharedPressureRange");
            Check(shared.Minimum == minimum && shared.Maximum == maximum && shared.ScaleMaximum == scale,
                "The keyboard-wide range matches the completed edit: " + minimum + ".." + maximum + " on " + scale + ".");
            Check(slider.SelectedMinimum == minimum && slider.SelectedMaximum == maximum && slider.RangeMaximum == scale,
                "The slider and global pressure range display the same values.");
            var runtime = Field<MultiControllerSession>(form, "runtime");
            foreach (DictionaryEntry entry in Field<IDictionary>(runtime, "slots"))
            {
                object session = entry.Value.GetType().GetField("Session").GetValue(entry.Value);
                var mapping = Field<MappingSession>(session, "inner");
                var values = Field<Dictionary<int, Calibration>>(mapping, "calibrations");
                Check(values.Count == 256 && Enumerable.Range(0, 256).All(key => values[key].Rest == minimum && values[key].Bottom == maximum),
                    "The real controller session applies exactly the same range to all 256 physical key IDs.");
                Check(Field<object>(mapping, "output") == null, "Calibration never creates a controller output in this synthetic test.");
            }
        }
        static void AwaitPressurePreview(MainForm form, int raw)
        {
            var runtime = Field<MultiControllerSession>(form, "runtime");
            string binding = Current(form).Bindings.First(item => item.KeyIndex == 14).BindingId;
            double expected = Field<KeyboardPressureRange>(form, "sharedPressureRange").Depth(raw);
            AwaitDialog(delegate {
                SignalResult result;
                return runtime.Preview.BindingResults.TryGetValue(binding, out result) && result.IsValid &&
                    Math.Abs(result.Normalized - expected) < 0.000001;
            }, "The synthetic pressure reaches the real mapping preview before its UI tick.");
        }
        static void CheckLivePressurePainting(MainForm form, DialogInput input, string path)
        {
            var slider = Field<PressureRangeSlider>(form, "pressureRange");
            var label = Field<Label>(form, "pressureText");
            var bindings = Field<DataGridView>(form, "bindings");
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            PushDialog(input, 14, 0); AwaitPressurePreview(form, 0);
            Call(form, "UpdateLive"); Application.DoEvents(); form.Update();
            // Prime initial sample/status changes before measuring. Pump() is
            // deliberately excluded here because it forces full-form layout.
            var bounds = DialogControls(form).Where(control => control.Visible).ToDictionary(control => control, control => control.Bounds);
            int[] selectedKeys = (int[])Call(form, "SelectedKeys"), keyboardKeys = keyboard.SelectedKeyIndices;
            string[] selectedBindings = (string[])Call(form, "SelectedBindings");
            int rows = bindings.Rows.Count;
            var calibration = Field<CalibrationDocument>(form, "calibration");
            var shared = Field<KeyboardPressureRange>(form, "sharedPressureRange");
            string profile = Json(Current(form)), persisted = File.Exists(path) ? File.ReadAllText(path) : null;
            var ancestors = new List<Control>();
            for (Control parent = slider.Parent; parent != null; parent = parent.Parent) ancestors.Add(parent);
            var layouts = new List<string>(); var parentRedraws = new List<string>(); var markerAreas = new List<Rectangle>();
            int labelRedraws = 0, gridRedraws = 0;
            var layoutStacks = new HashSet<string>(StringComparer.Ordinal);
            Func<Control, string> hierarchy = delegate(Control control) {
                if (control == null) return "<null>";
                var parts = new List<string>();
                for (Control current = control; current != null; current = current.Parent)
                {
                    Control item = current;
                    FieldInfo field = typeof(MainForm).GetFields(Private).FirstOrDefault(candidate => Object.ReferenceEquals(candidate.GetValue(form), item));
                    parts.Add(current.GetType().Name + "[" + (current.Parent == null ? 0 : current.Parent.Controls.GetChildIndex(current)) + "]" +
                        (field == null ? "" : "#" + field.Name));
                }
                parts.Reverse(); return String.Join("/", parts);
            };
            LayoutEventHandler layout = delegate(object sender, LayoutEventArgs e) {
                layouts.Add(sender.GetType().Name + "/" + e.AffectedProperty);
                if (layoutStacks.Count < 2)
                {
                    string stack = new StackTrace(1, true).ToString();
                    if (layoutStacks.Add(stack))
                        Console.WriteLine("PRESSURE LAYOUT DIAGNOSTIC " + layoutStacks.Count + ": sender=" + hierarchy((Control)sender) +
                            "; affected=" + hierarchy(e.AffectedControl) + "; property=" + e.AffectedProperty +
                            "; affectedBounds=" + (e.AffectedControl == null ? "<null>" : e.AffectedControl.Bounds.ToString()) +
                            "; marker=" + slider.MeasuredValue + Environment.NewLine + stack);
                }
            };
            InvalidateEventHandler parentRedraw = delegate(object sender, InvalidateEventArgs e) { parentRedraws.Add(sender.GetType().Name + "/" + e.InvalidRect); };
            InvalidateEventHandler marker = delegate(object sender, InvalidateEventArgs e) { markerAreas.Add(e.InvalidRect); };
            InvalidateEventHandler text = delegate { labelRedraws++; }, grid = delegate { gridRedraws++; };
            foreach (Control parent in ancestors) { parent.Layout += layout; parent.Invalidated += parentRedraw; }
            slider.Invalidated += marker; label.Invalidated += text; bindings.Invalidated += grid;
            var displayedValues = new HashSet<string>();
            try
            {
                for (int cycle = 0; cycle < 2; cycle++)
                    foreach (int raw in new[] { 16, 96, 192, 300, 385, 300, 96, 0 })
                    {
                        PushDialog(input, 14, raw); AwaitPressurePreview(form, raw); Call(form, "UpdateLive");
                        Application.DoEvents(); form.Update();
                        Check(slider.MeasuredValue == raw, "Normal pressing and releasing moves the inline live marker.");
                        Check(bounds.All(item => item.Key.Bounds == item.Value), "Normal pressure cannot resize or move any visible control.");
                        Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(selectedKeys) && keyboard.SelectedKeyIndices.SequenceEqual(keyboardKeys) &&
                            ((string[])Call(form, "SelectedBindings")).SequenceEqual(selectedBindings) && bindings.Rows.Count == rows,
                            "Normal pressure preserves key selection, binding selection and the existing mapping rows.");
                        displayedValues.Add(Convert.ToString(bindings.Rows[0].Cells[3].Value));
                    }
                Check(markerAreas.Count > 0 && labelRedraws > 0 && gridRedraws > 0 && displayedValues.Count > 1,
                    "The regression exercises changing marker, raw-value text and calculated binding values.");
                Check(markerAreas.All(area => !area.IsEmpty && slider.ClientRectangle.Contains(area) && area.Top >= slider.Font.Height + 4 &&
                    area.Width <= Math.Max(12, slider.Font.Height) && area.Height <= Math.Max(12, slider.Height / 3)),
                    "Each live marker invalidation is a small area below the slider labels, never the full range editor.");
                int stableMarkers = markerAreas.Count, stableText = labelRedraws, stableGrid = gridRedraws;
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    PushDialog(input, 14, 0); Call(form, "UpdateLive"); Application.DoEvents(); form.Update();
                }
                Check(markerAreas.Count == stableMarkers && labelRedraws == stableText && gridRedraws == stableGrid,
                    "Repeated unchanged samples invalidate neither marker, pressure text nor binding values.");
                Check(layouts.Count == 0, "Live readings cause no ancestor layout: " + String.Join(", ", layouts.Take(6)));
                Check(parentRedraws.Count == 0, "Live readings cause no ancestor invalidation: " + String.Join(", ", parentRedraws.Take(6)));
                Check(Object.ReferenceEquals(calibration, Field<CalibrationDocument>(form, "calibration")) &&
                    Object.ReferenceEquals(shared, Field<KeyboardPressureRange>(form, "sharedPressureRange")) && Json(Current(form)) == profile &&
                    (File.Exists(path) ? File.ReadAllText(path) : null) == persisted,
                    "Normal pressure never edits, persists or reconfigures the global calibration or mapping profile.");
            }
            finally
            {
                foreach (Control parent in ancestors) { parent.Layout -= layout; parent.Invalidated -= parentRedraw; }
                slider.Invalidated -= marker; label.Invalidated -= text; bindings.Invalidated -= grid;
            }
        }
        static void CheckPressureCalibrationButton(MainForm form, DialogInput input, string artifacts)
        {
            string language = UiText.Language; Size size = form.ClientSize;
            var button = Field<Button>(form, "calibrateRange");
            try
            {
                Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
                SetPreviewClientSize(form, new Size(SupportedMinimumSize.Width - chrome.Width, SupportedMinimumSize.Height - chrome.Height));
                foreach (string locale in new[] { "de", "en" })
                {
                    Call(form, "SwitchLanguage", locale); Call(form, "RefreshPressureRangeEditor"); Pump(form);
                    for (int capture = 0; capture < 2; capture++)
                    {
                        if (capture != 0) { PushDialog(input, 14, 0); ClickPressureCalibration(form); Pump(form); }
                        string expected = locale == "de" ? capture == 0 ? "Kalibrieren" : "Abbrechen" : capture == 0 ? "Calibrate" : "Cancel";
                        Equal(expected, button.Text, "The inline button displays its localized ready/cancel action.");
                        Check(button.AccessibleRole == AccessibleRole.PushButton && button.AccessibleName == expected && button.TabStop,
                            "Calibration remains an accessible, keyboard-reachable push button in both states.");
                        Size caption = TextRenderer.MeasureText(button.Text, button.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine);
                        Check(caption.Width <= button.ClientSize.Width - button.Padding.Horizontal && caption.Height <= button.ClientSize.Height - button.Padding.Vertical,
                            "The full calibration caption fits the compact button without wrapping or ellipsis: " + locale + "/" + expected);
                        VisibleInside(form, button, "compact calibration " + locale + "/" + expected);
                        CapturePreview(form, artifacts, "pressure-button-compact-" + locale + (capture == 0 ? "-ready" : "-cancel"));
                        if (capture != 0) ClickPressureCalibration(form);
                    }
                }
            }
            finally { Call(form, "CancelPressureCapture"); Call(form, "SwitchLanguage", language); SetPreviewClientSize(form, size); }
        }
        static void RunInlinePressureRange(string artifacts)
        {
            string language = UiText.Language;
            string data = Path.Combine(artifacts, "inline-pressure-data");
            try
            {
                using (var input = new DialogInput())
                using (var form = PressurePreview(data, input))
                {
                    var slider = Field<PressureRangeSlider>(form, "pressureRange");
                    var store = Field<WorkspaceStore>(form, "store");
                    string path = store.CalibrationPath(new string('c', 64));
                    CheckPressureRange(form, 0, 385, 385);
                    Check(slider.Enabled && Field<NumericUpDown>(form, "pressureScaleMaximum").Enabled, "Both handles and the shared scale are editable inline.");
                    Check(!File.Exists(path), "Opening the range editor does not invent or persist a calibration.");
                    CheckPressureMarkerPainting(slider);
                    CheckLivePressurePainting(form, input, path);
                    CheckPressureCalibrationButton(form, input, artifacts);
                    PushDialog(input, 14, 0); ClickPressureCalibration(form);
                    Check(Object.ReferenceEquals(Field<ReaderSession>(form, "pressureCaptureReader"), input.Reader), "The inline action subscribes to the already-connected reader.");
                    Check(!slider.Enabled && !Field<NumericUpDown>(form, "pressureScaleMaximum").Enabled, "Manual edits are paused during a live measurement.");
                    Check(form.OwnedForms.Length == 0 && !DialogControls(form).OfType<CheckBox>().Any(box => box.Text.Contains("Anschlag") || box.Text.Contains("full press")),
                        "Calibration opens no dialog and asks for no bottom-out confirmation checkbox.");

                    PushDialog(input, 14, 0, 80, 250, 550); Call(form, "UpdatePressureCapture");
                    Check(slider.SelectedMaximum == 550 && slider.RangeMaximum == 550 && slider.MeasuredValue == 550,
                        "Pressing beyond the old limit expands the live slider and marks the measured pressure.");
                    Check(!File.Exists(path) && Field<KeyboardPressureRange>(form, "sharedPressureRange").Maximum == 385,
                        "An unfinished press remains a preview until the selected key is released.");
                    PushDialog(input, 9, 900, 0); Call(form, "UpdatePressureCapture");
                    Check(slider.SelectedMaximum == 550 && Field<ReaderSession>(form, "pressureCaptureReader") != null,
                        "An unrelated key does not cancel the capture or widen the selected key's measurement.");
                    PushDialog(input, 14, 0); Call(form, "UpdatePressureCapture");
                    CheckPressureRange(form, 0, 550, 550);
                    Check(Field<ReaderSession>(form, "pressureCaptureReader") == null && slider.Enabled && File.Exists(path),
                        "One release automatically commits the global calibration without another click or second press.");
                    CalibrationDocument saved = store.LoadCalibration(new string('c', 64), input.Reader.Fingerprint);
                    Check(saved.GlobalMinimum == 0 && saved.GlobalMaximum == 550 && saved.ScaleMaximum == 550 && saved.Entries.Count == 0,
                        "The saved document records one shared range without manufacturing per-key measurements.");
                    string committed = File.ReadAllText(path);
                    PushDialog(input, 14, 700, 0); Call(form, "UpdatePressureCapture");
                    Check(File.ReadAllText(path) == committed, "Later normal presses cannot recalibrate after capture has completed.");

                    PushDialog(input, 14, 0); ClickPressureCalibration(form);
                    PushDialog(input, 14, 80, 610); Call(form, "UpdatePressureCapture");
                    ClickPressureCalibration(form);
                    Check(Field<ReaderSession>(form, "pressureCaptureReader") == null && File.ReadAllText(path) == committed,
                        "Cancel unsubscribes the unfinished capture and leaves the saved range unchanged.");
                    CheckPressureRange(form, 0, 550, 550);
                    PushDialog(input, 14, 0); Call(form, "UpdatePressureCapture");
                    Check(File.ReadAllText(path) == committed, "Releasing a cancelled press cannot save later.");

                    ClickPressureCalibration(form); PushDialog(input, 14, 80, 620); Call(form, "UpdatePressureCapture");
                    SelectKeys(form, 9); PushDialog(input, 14, 0); Call(form, "UpdatePressureCapture");
                    Check(Field<ReaderSession>(form, "pressureCaptureReader") == null && File.ReadAllText(path) == committed,
                        "Changing the selected key cancels its pending capture instead of applying it to a different key.");
                    SelectKeys(form, 14);
                    var scale = Field<NumericUpDown>(form, "pressureScaleMaximum"); scale.Value = 700;
                    Check(slider.RangeMaximum == 700 && File.ReadAllText(path) == committed,
                        "Typing a scale limit previews it without writing on every digit.");
                    var enter = new KeyEventArgs(Keys.Enter);
                    typeof(NumericUpDown).GetMethod("OnKeyDown", Private).Invoke(scale, new object[] { enter });
                    Check(enter.Handled, "Enter commits the shared scale directly in the editor.");
                    CheckPressureRange(form, 0, 550, 700);
                    committed = File.ReadAllText(path);
                    scale.Text = "800";
                    Check(File.ReadAllText(path) == committed, "Pending numeric text does not save before the user commits it.");
                    var typedEnter = new KeyEventArgs(Keys.Enter);
                    typeof(NumericUpDown).GetMethod("OnKeyDown", Private).Invoke(scale, new object[] { typedEnter });
                    Check(typedEnter.Handled, "Enter validates pending numeric text instead of retaining the previous numeric value.");
                    CheckPressureRange(form, 0, 550, 800);
                    CalibrationDocument unchangedCalibration = Field<CalibrationDocument>(form, "calibration");
                    KeyboardPressureRange unchangedRange = Field<KeyboardPressureRange>(form, "sharedPressureRange");
                    committed = File.ReadAllText(path);
                    typeof(NumericUpDown).GetMethod("OnKeyDown", Private).Invoke(scale, new object[] { new KeyEventArgs(Keys.Enter) });
                    Check(Object.ReferenceEquals(unchangedCalibration, Field<CalibrationDocument>(form, "calibration")) &&
                        Object.ReferenceEquals(unchangedRange, Field<KeyboardPressureRange>(form, "sharedPressureRange")) && File.ReadAllText(path) == committed,
                        "Committing an unchanged scale preserves the calibration snapshot and avoids writing or reconfiguring output.");
                    scale.Text = "700";
                    typeof(NumericUpDown).GetMethod("OnKeyDown", Private).Invoke(scale, new object[] { new KeyEventArgs(Keys.Enter) });
                    CheckPressureRange(form, 0, 550, 700);
                    slider.AccessibilityObject.GetChild(0).Value = "10";
                    slider.AccessibilityObject.GetChild(1).Value = "600";
                    CheckPressureRange(form, 10, 600, 700);
                    saved = store.LoadCalibration(new string('c', 64), input.Reader.Fingerprint);
                    Check(saved.GlobalMinimum == 10 && saved.GlobalMaximum == 600 && saved.ScaleMaximum == 700,
                        "Editing either real slider handle persists the range for every key.");
                    SelectKeys(form, 9, 14);
                    Check(slider.Enabled && !Field<Button>(form, "calibrateRange").Enabled,
                        "The shared range stays editable for multiple keys; measurement names one selected source key.");
                    CheckPressureRange(form, 10, 600, 700);
                    SelectKeys(form, 14);
                    CapturePreview(form, artifacts, "global-pressure-range");

                    committed = File.ReadAllText(path); PushDialog(input, 14, 0); ClickPressureCalibration(form);
                    PushDialog(input, 14, 80, 740, 0); input.Reader.Stop(); Call(form, "UpdatePressureCapture");
                    Check(Field<ReaderSession>(form, "pressureCaptureReader") == null && File.ReadAllText(path) == committed,
                        "Disconnect before the UI commits a completed measurement cannot change the saved range.");
                    Check(!Field<Button>(form, "calibrateRange").Enabled, "A stopped reader disables calibration without silently reconnecting it.");
                    CheckPressureRange(form, 10, 600, 700);
                }
                using (var input = new DialogInput())
                using (var form = PressurePreview(data, input))
                {
                    CheckPressureRange(form, 10, 600, 700);
                    Check(Field<ReaderSession>(form, "pressureCaptureReader") == null,
                        "Reopening the editor restores the range without starting another calibration.");
                }
            }
            finally { UiText.SetLanguage(language); }
        }

        static void RunDialogs(string artifacts)
        {
            int started = assertions;
            RunInlinePressureRange(artifacts);

            using (var input = new DialogInput())
            using (var dialog = new LearnDialog(input.Reader, "W"))
            {
                ShowDialogOffline(dialog); TickDialog(dialog);
                Check(Field<Label>(dialog, "status").Text.Contains(input.Reader.Status), "Learning without samples explains the actual input status.");
                PushDialog(input, 14, 45); TickDialog(dialog);
                Check(Field<Label>(dialog, "status").Text.Contains("45") && Field<Label>(dialog, "status").Text.Contains("loslassen"), "Learning displays the incoming pressure and release instruction.");
                PushDialog(input, 14, 0); TickDialog(dialog);
                Check(Field<ProgressBar>(dialog, "progress").Value == 1 && Field<Label>(dialog, "status").Text.Contains("Erster Durchgang"), "Learning advances after the first complete cycle.");
                CaptureDialog(dialog, Path.Combine(artifacts, "learning-first-cycle.png"));
                PushDialog(input, 14, 85, 0); TickDialog(dialog);
                Check(dialog.DialogResult == DialogResult.OK && dialog.KeyIndex == 14, "Two complete learning cycles return the measured physical key index.");
            }

            using (var input = new DialogInput())
            using (var dialog = new LearnDialog(input.Reader, "W"))
            {
                ShowDialogOffline(dialog); PushDialog(input, 14, 40); PushDialog(input, 9, 80); TickDialog(dialog);
                Check(dialog.DialogResult != DialogResult.OK && Field<Label>(dialog, "status").Text.Contains("Mehrere Tasten"), "Ambiguous learning cannot silently choose one key."); dialog.Close();
            }
            using (var input = new DialogInput())
            using (var dialog = new LearnDialog(input.Reader, "W"))
            {
                ShowDialogOffline(dialog); PushDialog(input, 14, 40, 0, 80, 0); input.Reader.Stop(); TickDialog(dialog);
                Check(dialog.DialogResult != DialogResult.OK, "A disconnect before learning confirmation prevents success.");
                Equal(input.Reader.Status, Field<Label>(dialog, "status").Text, "Learning displays disconnection instead of a success state."); dialog.Close();
            }
            Console.WriteLine("DIALOG PASS: " + (assertions - started) + " assertions; real Learn form and inline pressure range with synthetic ReaderSession only.");
        }
    }
}
