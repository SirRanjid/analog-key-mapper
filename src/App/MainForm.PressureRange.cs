using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly PressureRangeSlider pressureRange = new PressureRangeSlider();
        readonly NumericUpDown pressureScaleMaximum = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 385, DecimalPlaces = 0, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
        readonly Button calibrateRange = new SleekButton { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AccessibleRole = AccessibleRole.PushButton };
        readonly Label pressureScaleLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        KeyboardPressureRange sharedPressureRange = new KeyboardPressureRange(null);
        CalibrationDocument reportedLegacyPressureRange;
        CalibrationDocument displayedPressureRange;
        bool updatingPressureRange;
        readonly object pressureCaptureGate = new object();
        PressureRangeCapture pressureCapture;
        ReaderSession pressureCaptureReader;
        int pressureCaptureKey = -1;

        Control BuildPressureRangeEditor()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 2), ColumnCount = 1, RowCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            UiText.PreserveText(pressureScaleLabel); UiText.PreserveText(calibrateRange);
            header.Controls.Add(pressureScaleLabel, 0, 0); header.Controls.Add(pressureScaleMaximum, 1, 0); header.Controls.Add(calibrateRange, 2, 0);
            panel.Controls.Add(header, 0, 0); pressureRange.Dock = DockStyle.Fill; pressureRange.Margin = Padding.Empty; panel.Controls.Add(pressureRange, 0, 1);
            pressureRange.ValueCommitted += delegate { if (!updatingPressureRange) Attempt(delegate { SavePressureRange(pressureRange.SelectedMinimum, pressureRange.SelectedMaximum, pressureRange.RangeMaximum); }); };
            pressureScaleMaximum.ValueChanged += delegate {
                if (updatingPressureRange) return;
                CancelPressureCapture();
                double maximum = (double)pressureScaleMaximum.Value;
                // Adjust the visual scale immediately; persistence happens once
                // on leaving the editor or pressing Enter, not on each digit.
                pressureRange.SetRange(0, maximum, Math.Min(pressureRange.SelectedMinimum, Math.Max(0, maximum - 1)), Math.Min(pressureRange.SelectedMaximum, maximum));
            };
            pressureScaleMaximum.Leave += delegate { CommitPressureScale(); };
            pressureScaleMaximum.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { CommitPressureScale(); e.Handled = e.SuppressKeyPress = true; } };
            calibrateRange.Click += delegate { Attempt(delegate { if (pressureCaptureReader == null) BeginPressureCapture(); else { CancelPressureCapture(); RefreshPressureRangeEditor(); } }); };
            VisibleChanged += delegate { if (!Visible && pressureCaptureReader != null) { CancelPressureCapture(); RefreshPressureRangeEditor(); } };
            Disposed += delegate { CancelPressureCapture(); };
            return panel;
        }
        void CommitPressureScale()
        {
            if (updatingPressureRange || calibration == null) return;
            Attempt(delegate {
                double requested = (double)pressureScaleMaximum.Value;
                pressureRange.SetRange(0, requested, Math.Min(pressureRange.SelectedMinimum, Math.Max(0, requested - 1)), Math.Min(pressureRange.SelectedMaximum, requested));
                SavePressureRange(pressureRange.SelectedMinimum, pressureRange.SelectedMaximum, requested);
            });
        }
        void SavePressureRange(double minimum, double maximum, double scaleMaximum)
        {
            if (calibration == null) return;
            bool measured = pressureCaptureReader != null;
            CancelPressureCapture();
            if (minimum == sharedPressureRange.Minimum && maximum == sharedPressureRange.Maximum && scaleMaximum == sharedPressureRange.ScaleMaximum && (!measured || calibration.GlobalMinimum.HasValue))
            { RefreshPressureRangeEditor(); return; }
            var next = sharedPressureRange.Apply(calibration, minimum, maximum, scaleMaximum);
            try { store.SaveCalibration(next); }
            catch { RefreshPressureRangeEditor(); throw; }
            calibration = next; Configure(); UpdateKeyCard();
            store.Event("Keyboard pressure range saved for all keys: " + minimum + ".." + maximum);
        }
        void RefreshPressureRangeEditor()
        {
            int[] selected = SelectedKeys();
            if (pressureCaptureReader != null && (selected.Length != 1 || selected[0] != pressureCaptureKey || !Object.ReferenceEquals(reader, pressureCaptureReader))) CancelPressureCapture();
            updatingPressureRange = true;
            try
            {
                // Keep the themed button inside the scale editor's 24px header.
                // The theme's standard 32px minimum and margins belong to larger rows.
                if (calibrateRange.MinimumSize != Size.Empty) calibrateRange.MinimumSize = Size.Empty;
                Padding buttonMargin = new Padding(3, 0, 0, 0), buttonPadding = new Padding(4, 0, 4, 0);
                if (calibrateRange.Margin != buttonMargin) calibrateRange.Margin = buttonMargin;
                if (calibrateRange.Padding != buttonPadding) calibrateRange.Padding = buttonPadding;
                if (pressureCaptureReader == null && (!Object.ReferenceEquals(displayedPressureRange, calibration) || !pressureRange.IsDragging && !pressureScaleMaximum.Focused))
                {
                    pressureRange.SetRange(0, sharedPressureRange.ScaleMaximum, sharedPressureRange.Minimum, sharedPressureRange.Maximum);
                    pressureScaleMaximum.Value = (decimal)sharedPressureRange.ScaleMaximum;
                    displayedPressureRange = calibration;
                }
                pressureScaleLabel.Text = Tr("Skala bis", "Scale max");
                pressureRange.AccessibleName = Tr("Gemeinsamer Druckbereich", "Shared pressure range");
                pressureRange.Enabled = pressureScaleMaximum.Enabled = calibration != null && pressureCaptureReader == null;
                calibrateRange.Enabled = selected.Length == 1 && reader != null && reader.IsReading && calibration != null;
                calibrateRange.Text = pressureCaptureReader != null ? Tr("Abbrechen", "Cancel") : Tr("Kalibrieren", "Calibrate");
                calibrateRange.AccessibleName = calibrateRange.Text;
                if (pressureCaptureReader == null && selected.Length != 0)
                    keyHint.Text = Tr("Druckbereich · alle Tasten", "Pressure range · all keys");
                keyCardTips.SetToolTip(pressureRange, Tr("Min und Max gelten für jede Taste dieser Tastatur.", "Min and max apply to every key on this keyboard."));
                keyCardTips.SetToolTip(pressureScaleMaximum, Tr("Gemeinsame Rohwert-Skala. Eine Kalibrierung erweitert sie bei Bedarf automatisch.", "Shared raw-value scale. Calibration expands it automatically when needed."));
                string calibrationHint = pressureCaptureReader != null
                    ? Tr("Kalibrierung abbrechen. Der gespeicherte Druckbereich bleibt unverändert.", "Cancel calibration. The saved pressure range stays unchanged.")
                    : Tr("Einmal bis zum Anschlag drücken und loslassen. Der gemessene Bereich gilt anschließend für alle Tasten.", "Press all the way down once, then release. The measured range is then used for all keys.");
                calibrateRange.AccessibleDescription = calibrationHint;
                keyCardTips.SetToolTip(calibrateRange, calibrationHint);
            }
            finally { updatingPressureRange = false; }
        }
        void BeginPressureCapture()
        {
            RequireReader(); int[] selected = SelectedKeys();
            if (selected.Length != 1 || calibration == null) return;
            CancelPressureCapture();
            ReaderSession input = reader;
            var initial = input.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds).Where(s => s.KeyIndex == selected[0] && s.Known && !s.Stale).Select(s => (double?)s.RawValue).FirstOrDefault();
            lock (pressureCaptureGate)
            {
                pressureCapture = new PressureRangeCapture(selected[0], sharedPressureRange.Minimum, sharedPressureRange.Maximum, initial);
                pressureCaptureReader = input; pressureCaptureKey = selected[0];
            }
            input.Sample += FeedPressureCapture;
            RefreshPressureRangeEditor(); UpdatePressureCapture();
        }
        void FeedPressureCapture(TravelSample sample, double elapsed)
        { lock (pressureCaptureGate) if (pressureCapture != null) pressureCapture.Feed(sample.KeyIndex, sample.RawValue); }
        void CancelPressureCapture()
        {
            ReaderSession previous;
            lock (pressureCaptureGate) { previous = pressureCaptureReader; pressureCaptureReader = null; pressureCapture = null; pressureCaptureKey = -1; }
            if (previous != null) previous.Sample -= FeedPressureCapture;
            pressureRange.MeasuredValue = null;
        }
        void UpdatePressureCapture()
        {
            ReaderSession input = pressureCaptureReader;
            if (input == null) return;
            if (!Object.ReferenceEquals(input, reader) || !input.IsReading || closing || deviceDetachInProgress)
            { CancelPressureCapture(); RefreshPressureRangeEditor(); return; }
            double minimum, maximum; double? latest; bool completed, pressing;
            lock (pressureCaptureGate)
            {
                if (pressureCapture == null) return;
                minimum = pressureCapture.Minimum; maximum = pressureCapture.Maximum; latest = pressureCapture.Latest;
                completed = pressureCapture.Completed; pressing = pressureCapture.Pressing;
            }
            double scale = Math.Max(sharedPressureRange.ScaleMaximum, maximum);
            if (maximum > minimum) pressureRange.SetRange(0, scale, minimum, maximum);
            pressureRange.MeasuredValue = latest;
            keyHint.Text = string.Format(pressing ? Tr("{0} loslassen · gilt für alle Tasten", "Release {0} · applies to all keys") : Tr("{0} einmal ganz drücken und loslassen", "Press {0} fully once, then release"), Label(pressureCaptureKey));
            if (completed) Attempt(delegate { SavePressureRange(minimum, maximum, scale); });
        }
    }
}
