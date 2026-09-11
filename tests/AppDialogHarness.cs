using System;
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
        static void FullCycles(DialogInput input)
        { PushDialog(input, 14, 0, 40, 100, 180, 340, 0, 40, 100, 180, 340, 0); }
        static void RestartCalibration(CalibrationDialog dialog)
        { DialogControls(dialog).OfType<Button>().Single(b => b.Text == "Neu aufnehmen").PerformClick(); TickDialog(dialog); }
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

        static void RunDialogs(string artifacts)
        {
            int started = assertions;
            using (var input = new DialogInput())
            using (var dialog = new CalibrationDialog(input.Reader, 14, "W", null))
            {
                ShowDialogOffline(dialog); TickDialog(dialog);
                Check(dialog.ClientSize == new Size(640, 390), "Calibration opens at the designed 640x390 client size.");
                Check(Field<bool>(dialog, "recording"), "Calibration starts recording automatically on Shown.");
                Check(Field<Label>(dialog, "values").Text.Contains("Noch keine Messwerte"), "Empty input is described without fabricated pressure.");
                Check(!Field<Button>(dialog, "save").Enabled, "Empty calibration cannot be saved.");
                PushDialog(input, 14, 0, 100, 250, 500, 650, 0); TickDialog(dialog);
                Check(Field<ProgressBar>(dialog, "progress").Value == 1 && Field<Label>(dialog, "status").Text.Contains("Noch einmal"), "First release advances the two-cycle instruction.");
                PushDialog(input, 9, 60); TickDialog(dialog);
                Check(Field<Label>(dialog, "status").Text.Contains("andere Taste") && !Field<bool>(dialog, "recording"), "A second physical key invalidates the calibration capture.");
                Field<CheckBox>(dialog, "full").Checked = true;
                RestartCalibration(dialog);
                Check(Field<bool>(dialog, "recording") && Field<int>(dialog, "samples") == 0 && !Field<CheckBox>(dialog, "full").Checked, "Restart clears samples, errors and the previous bottom-out confirmation.");
                Check(Field<ProgressBar>(dialog, "progress").Value == 0, "Restart resets the visible cycle count.");
                FullCycles(input); TickDialog(dialog);
                Check((bool)DialogCall(dialog, "CanSave"), "Two released cycles with 11 samples and five distinct values meet the measured requirements.");
                Check(!Field<Button>(dialog, "save").Enabled, "Completed measurements still require explicit bottom-out confirmation.");
                Field<CheckBox>(dialog, "full").Checked = true;
                Check(Field<Button>(dialog, "save").Enabled, "Bottom-out checkbox enables a valid completed calibration.");
                CaptureDialog(dialog, Path.Combine(artifacts, "calibration-completed-640x390.png"));
                Field<Button>(dialog, "save").PerformClick();
                Check(dialog.DialogResult == DialogResult.OK && dialog.Result != null, "The real Save click returns the completed calibration.");
                Check(dialog.Result.Rest == 0 && dialog.Result.Bottom == 340 && !dialog.Result.MeasuredTravel.HasValue, "Restart discarded earlier extrema and does not invent a millimeter travel.");
            }

            foreach (int[] sparse in new[] { new[] { 0, 10, 10, 20, 30, 0, 10, 10, 20, 30, 0 }, new[] { 20, 80, 160, 320, 0, 40, 200, 340, 0 } })
            using (var input = new DialogInput())
            using (var dialog = new CalibrationDialog(input.Reader, 14, "W", null))
            {
                ShowDialogOffline(dialog); PushDialog(input, 14, sparse); TickDialog(dialog); Field<CheckBox>(dialog, "full").Checked = true;
                Check(Field<ProgressBar>(dialog, "progress").Value == 2, "Sparse input contains two complete physical cycles.");
                Check(!(bool)DialogCall(dialog, "CanSave") && !Field<Button>(dialog, "save").Enabled, sparse.Length >= 10 ? "Four distinct depths do not pass the five-value minimum." : "Nine samples do not pass the ten-sample minimum.");
                Check(Field<Label>(dialog, "status").Text.Contains("neu aufnehmen"), "Insufficient precision has an actionable restart instruction."); dialog.Close();
            }

            using (var input = new DialogInput())
            using (var dialog = new CalibrationDialog(input.Reader, 14, "W", null))
            {
                ShowDialogOffline(dialog); FullCycles(input); TickDialog(dialog); Field<CheckBox>(dialog, "full").Checked = true;
                Check(Field<Button>(dialog, "save").Enabled, "Disconnect case first has a valid completed capture.");
                input.Reader.Stop(); TickDialog(dialog);
                Check(!(bool)DialogCall(dialog, "CanSave") && !Field<Button>(dialog, "save").Enabled, "Disconnect after completion prevents saving, including before another UI action.");
                Field<Button>(dialog, "save").PerformClick(); Check(dialog.Result == null && dialog.DialogResult != DialogResult.OK, "Disabled Save cannot publish a disconnected calibration.");
                Equal(input.Reader.Status, Field<Label>(dialog, "status").Text, "Disconnection reason replaces the completed instruction."); dialog.Close();
            }

            using (var input = new DialogInput())
            using (var dialog = new CalibrationDialog(input.Reader, 14, "W", null))
            {
                ShowDialogOffline(dialog); input.Source.Push(new IOException("Synthetic cable removed before first pressure value."));
                AwaitDialog(delegate { return !input.Reader.IsReading; }, "Synthetic input failure reaches the reader."); TickDialog(dialog);
                Equal(input.Reader.Status, Field<Label>(dialog, "status").Text, "Empty-input fault reason is visible in the calibration dialog.");
                RestartCalibration(dialog); Check(!Field<bool>(dialog, "recording") && !Field<Button>(dialog, "save").Enabled, "Restart cannot silently reconnect or record through an input fault.");
                CaptureDialog(dialog, Path.Combine(artifacts, "calibration-input-fault-640x390.png")); dialog.Close();
            }

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
            Console.WriteLine("DIALOG PASS: " + (assertions - started) + " assertions; real Learn/Calibration forms with synthetic ReaderSession only.");
        }
    }
}
