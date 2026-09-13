using System;
using System.Collections.Generic;
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
        readonly NumericUpDown pressureScaleMaximum = new SleekNumericUpDown { Minimum = 1, Maximum = 65535, Value = 385, DecimalPlaces = 0, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
        readonly Button calibrateRange = new SleekButton { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AccessibleRole = AccessibleRole.PushButton };
        readonly Label pressureScaleLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        KeyboardPressureRange sharedPressureRange = new KeyboardPressureRange(null);
        CalibrationDocument reportedLegacyPressureRange;
        CalibrationDocument displayedPressureRange;
        int[] displayedPressureKeys = new int[0];
        bool updatingPressureRange;
        readonly object pressureCaptureGate = new object();
        PressureRangeCapture pressureCapture;
        Dictionary<int, PressureRangeCapture> pressureCaptureCandidates;
        int[] pressureCaptureKeys = new int[0];
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
                // The scale is a visual limit. Lowering it never changes a key's
                // endpoints, including endpoints on currently unselected keys.
                double maximum = Math.Max(sharedPressureRange.MaximumEndpoint, (double)pressureScaleMaximum.Value);
                pressureRange.SetRange(0, maximum, pressureRange.SelectedMinimum, pressureRange.SelectedMaximum);
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
                double effective = Math.Max(sharedPressureRange.MaximumEndpoint, requested);
                CancelPressureCapture();
                if (effective != sharedPressureRange.ScaleMaximum)
                    PersistPressureRange(sharedPressureRange.ApplyScale(calibration, effective), "Keyboard pressure scale saved: " + effective);
                else { displayedPressureRange = null; RefreshPressureRangeEditor(); }
                if (requested < effective)
                    keyHint.Text = string.Format(Tr("Skala mindestens {0} · gespeicherte Max-Werte bleiben erhalten", "Scale minimum {0} · saved max values are preserved"), effective);
            });
        }
        void SavePressureRange(double minimum, double maximum, double scaleMaximum)
        {
            if (calibration == null) return;
            bool measured = pressureCaptureReader != null;
            int[] selected = measured ? pressureCaptureKeys : SelectedKeys();
            CancelPressureCapture();
            if (selected.Length == 0) { RefreshPressureRangeEditor(); return; }
            if (selected.All(key => sharedPressureRange.ForKey(key).Rest == minimum && sharedPressureRange.ForKey(key).Bottom == maximum) &&
                scaleMaximum == sharedPressureRange.ScaleMaximum && (!measured || selected.All(key => calibration.KeyRanges.Any(entry => entry.KeyIndex == key))))
            { RefreshPressureRangeEditor(); return; }
            PersistPressureRange(sharedPressureRange.Apply(calibration, selected, minimum, maximum, scaleMaximum),
                "Pressure range saved for selected keys [" + string.Join(",", selected) + "]: " + minimum + ".." + maximum);
        }
        void PersistPressureRange(CalibrationDocument next, string message)
        {
            try { store.SaveCalibration(next); }
            catch { displayedPressureRange = null; RefreshPressureRangeEditor(); throw; }
            calibration = next; Configure(); RefreshKeyBehaviorAnnotations(); UpdateKeyCard();
            store.Event(message);
        }
        void RefreshPressureRangeEditor()
        {
            int[] selected = SelectedKeys();
            if (pressureCaptureReader != null && (!selected.SequenceEqual(pressureCaptureKeys) || !Object.ReferenceEquals(reader, pressureCaptureReader))) CancelPressureCapture();
            updatingPressureRange = true;
            try
            {
                // Keep the themed button inside the scale editor's 24px header.
                if (calibrateRange.MinimumSize != Size.Empty) calibrateRange.MinimumSize = Size.Empty;
                Padding buttonMargin = new Padding(3, 0, 0, 0), buttonPadding = new Padding(4, 0, 4, 0);
                if (calibrateRange.Margin != buttonMargin) calibrateRange.Margin = buttonMargin;
                if (calibrateRange.Padding != buttonPadding) calibrateRange.Padding = buttonPadding;
                Calibration range = sharedPressureRange.ForKey(selected.Length == 0 ? 0 : selected[0]);
                bool mixed = selected.Any(key => sharedPressureRange.ForKey(key).Rest != range.Rest || sharedPressureRange.ForKey(key).Bottom != range.Bottom);
                if (pressureCaptureReader == null && (!Object.ReferenceEquals(displayedPressureRange, calibration) || !selected.SequenceEqual(displayedPressureKeys) || !pressureRange.IsDragging && !pressureScaleMaximum.Focused))
                {
                    pressureRange.SetRange(0, sharedPressureRange.ScaleMaximum, range.Rest, range.Bottom);
                    pressureScaleMaximum.Value = (decimal)sharedPressureRange.ScaleMaximum;
                    displayedPressureRange = calibration; displayedPressureKeys = selected;
                }
                pressureScaleLabel.Text = Tr("Skala bis", "Scale max");
                pressureRange.AccessibleName = Tr("Druckbereich der ausgewählten Tasten", "Pressure range of selected keys");
                pressureRange.Enabled = selected.Length != 0 && calibration != null && pressureCaptureReader == null;
                pressureScaleMaximum.Enabled = calibration != null && pressureCaptureReader == null;
                calibrateRange.Enabled = selected.Length != 0 && reader != null && reader.IsReading && calibration != null;
                calibrateRange.Text = pressureCaptureReader != null ? Tr("Abbrechen", "Cancel") : Tr("Kalibrieren", "Calibrate");
                calibrateRange.AccessibleName = calibrateRange.Text;
                if (pressureCaptureReader == null && selected.Length != 0)
                    keyHint.Text = selected.Length == 1 ? Tr("Druckbereich · ausgewählte Taste", "Pressure range · selected key") :
                        string.Format(mixed ? Tr("Verschiedene Bereiche · Änderungen für {0} Tasten", "Mixed ranges · edits apply to {0} keys") :
                        Tr("Druckbereich · {0} ausgewählte Tasten", "Pressure range · {0} selected keys"), selected.Length);
                keyCardTips.SetToolTip(pressureRange, mixed ?
                    Tr("Die Griffe zeigen den Bereich der ersten ausgewählten Taste. Eine Änderung setzt Min und Max für alle ausgewählten Tasten.", "The handles show the first selected key's range. An edit sets min and max for all selected keys.") :
                    Tr("Min und Max gelten nur für die ausgewählten Tasten.", "Min and max apply only to the selected keys."));
                keyCardTips.SetToolTip(pressureScaleMaximum, string.Format(Tr("Gemeinsame Rohwert-Skala dieser Tastatur. Mindestens {0}, damit alle gespeicherten Max-Werte erhalten bleiben. Kalibrierung erweitert sie bei Bedarf.",
                    "Shared raw-value scale for this keyboard. At least {0} to preserve every saved max value. Calibration expands it when needed."), sharedPressureRange.MaximumEndpoint));
                string calibrationHint = pressureCaptureReader != null
                    ? Tr("Kalibrierung abbrechen. Die gespeicherten Druckbereiche bleiben unverändert.", "Cancel calibration. Saved pressure ranges stay unchanged.")
                    : Tr("Eine ausgewählte Taste einmal bis zum Anschlag drücken und loslassen. Ihr gemessener Bereich gilt anschließend für alle ausgewählten Tasten.", "Press any selected key all the way down once, then release. Its measured range is applied to all selected keys.");
                calibrateRange.AccessibleDescription = calibrationHint;
                keyCardTips.SetToolTip(calibrateRange, calibrationHint);
            }
            finally { updatingPressureRange = false; }
        }
        void BeginPressureCapture()
        {
            RequireReader(); int[] selected = SelectedKeys();
            if (selected.Length == 0 || calibration == null) return;
            CancelPressureCapture();
            ReaderSession input = reader;
            var initial = input.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds).Where(s => s.Known && !s.Stale).ToDictionary(s => s.KeyIndex, s => (double)s.RawValue);
            var candidates = new Dictionary<int, PressureRangeCapture>();
            foreach (int key in selected)
            {
                double raw; Calibration range = sharedPressureRange.ForKey(key);
                candidates.Add(key, new PressureRangeCapture(key, range.Rest, range.Bottom, initial.TryGetValue(key, out raw) ? (double?)raw : null));
            }
            lock (pressureCaptureGate)
            {
                pressureCaptureCandidates = candidates; pressureCaptureKeys = selected;
                pressureCapture = selected.Length == 1 ? candidates[selected[0]] : null;
                pressureCaptureReader = input; pressureCaptureKey = selected.Length == 1 ? selected[0] : -1;
            }
            input.Sample += FeedPressureCapture;
            RefreshPressureRangeEditor(); UpdatePressureCapture();
        }
        void FeedPressureCapture(TravelSample sample, double elapsed)
        {
            lock (pressureCaptureGate)
            {
                if (pressureCapture != null) { pressureCapture.Feed(sample.KeyIndex, sample.RawValue); return; }
                PressureRangeCapture candidate;
                if (pressureCaptureCandidates == null || !pressureCaptureCandidates.TryGetValue(sample.KeyIndex, out candidate)) return;
                candidate.Feed(sample.KeyIndex, sample.RawValue);
                // Lock onto the first selected key that starts a press. Other
                // keys can no longer contaminate this one press/release cycle.
                if (candidate.Pressing || candidate.Completed) { pressureCapture = candidate; pressureCaptureKey = sample.KeyIndex; }
            }
        }
        void CancelPressureCapture()
        {
            ReaderSession previous;
            lock (pressureCaptureGate)
            {
                previous = pressureCaptureReader; pressureCaptureReader = null; pressureCapture = null; pressureCaptureCandidates = null;
                pressureCaptureKeys = new int[0]; pressureCaptureKey = -1;
            }
            if (previous != null) previous.Sample -= FeedPressureCapture;
            pressureRange.MeasuredValue = null;
        }
        void UpdatePressureCapture()
        {
            ReaderSession input = pressureCaptureReader;
            if (input == null) return;
            if (!Object.ReferenceEquals(input, reader) || !input.IsReading || closing || deviceDetachInProgress || !SelectedKeys().SequenceEqual(pressureCaptureKeys))
            { CancelPressureCapture(); RefreshPressureRangeEditor(); return; }
            double minimum, maximum; double? latest; bool completed, pressing; int sourceKey, selectedCount;
            lock (pressureCaptureGate)
            {
                if (pressureCapture == null)
                {
                    keyHint.Text = Tr("Eine ausgewählte Taste einmal ganz drücken und loslassen", "Press any selected key fully once, then release");
                    return;
                }
                minimum = pressureCapture.Minimum; maximum = pressureCapture.Maximum; latest = pressureCapture.Latest;
                completed = pressureCapture.Completed; pressing = pressureCapture.Pressing; sourceKey = pressureCaptureKey; selectedCount = pressureCaptureKeys.Length;
            }
            double scale = Math.Max(sharedPressureRange.ScaleMaximum, maximum);
            if (maximum > minimum) pressureRange.SetRange(0, scale, minimum, maximum);
            pressureRange.MeasuredValue = latest;
            keyHint.Text = string.Format(pressing ? Tr("{0} loslassen · gilt für {1} ausgewählte Tasten", "Release {0} · applies to {1} selected keys") :
                Tr("{0} einmal ganz drücken und loslassen", "Press {0} fully once, then release"), Label(sourceKey), selectedCount);
            if (completed) Attempt(delegate { SavePressureRange(minimum, maximum, scale); });
        }
    }
}
