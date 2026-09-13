using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly InputThresholdSlider actuationSlider = new InputThresholdSlider(), releaseSlider = new InputThresholdSlider(), repressSlider = new InputThresholdSlider();
        readonly Button calibrateActuation = new SleekButton(), calibrateRelease = new SleekButton(), calibrateRepress = new SleekButton();
        InputThresholdSlider inputThresholdDraftSlider;
        NumericUpDown inputThresholdDraftNumber;
        decimal inputThresholdOriginalNumber;

        Control BuildInputThresholdField(string label, NumericUpDown number, InputThresholdSlider slider, Button calibrate, InputActivationFields field)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(3, 0, 5, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var caption = Caption(label, 24); caption.Margin = Padding.Empty; panel.Controls.Add(caption, 0, 0);
            number.Margin = new Padding(0, 0, 0, 2); panel.Controls.Add(number, 0, 1);
            calibrate.Dock = DockStyle.Fill; calibrate.AutoSize = false; calibrate.MinimumSize = Size.Empty; calibrate.Margin = new Padding(0, 0, 0, 2); calibrate.Padding = new Padding(2, 0, 2, 0);
            panel.Controls.Add(calibrate, 0, 2); slider.Dock = DockStyle.Fill; slider.Margin = Padding.Empty; panel.Controls.Add(slider, 0, 3);
            number.ValueChanged += delegate { if (!updatingInput) slider.SetValue((double)number.Value, false); };
            slider.Previewed += delegate(double value) {
                if (updatingInput) return;
                if (inputThresholdDraftSlider != slider)
                { if (inputThresholdDraftSlider != null) inputThresholdDraftSlider.CancelEdit(); inputThresholdDraftSlider = slider; inputThresholdDraftNumber = number; inputThresholdOriginalNumber = number.Value; }
                bool previous = updatingInput; updatingInput = true;
                try { number.Value = (decimal)value; } finally { updatingInput = previous; }
            };
            slider.Committed += delegate(double value) {
                inputThresholdDraftSlider = null; inputThresholdDraftNumber = null;
                bool previous = updatingInput; updatingInput = true;
                try { number.Value = (decimal)value; } finally { updatingInput = previous; }
                MarkInputDirty(field);
            };
            slider.Canceled += delegate { RestoreInputThresholdNumber(slider); };
            calibrate.Click += delegate { Attempt(delegate { if (inputThresholdCaptureReader != null) CancelInputThresholdCapture(); else BeginInputThresholdCapture(field); }); };
            return panel;
        }
        void RestoreInputThresholdNumber(InputThresholdSlider slider)
        {
            if (inputThresholdDraftSlider != slider) return;
            var number = inputThresholdDraftNumber; decimal original = inputThresholdOriginalNumber;
            inputThresholdDraftSlider = null; inputThresholdDraftNumber = null;
            bool previous = updatingInput; updatingInput = true;
            try { number.Value = original; } finally { updatingInput = previous; }
        }
        void CancelInputThresholdGestures()
        { actuationSlider.CancelEdit(); releaseSlider.CancelEdit(); repressSlider.CancelEdit(); }
        void RefreshInputThresholdSliders(KeyInputSettings[] values)
        {
            CancelInputThresholdGestures();
            actuationSlider.SetValue((double)actuationPoint.Value, values.Skip(1).Any(v => v.ActuationPoint != values[0].ActuationPoint));
            releaseSlider.SetValue((double)releaseMovement.Value, values.Skip(1).Any(v => v.ReleaseMovement != values[0].ReleaseMovement));
            repressSlider.SetValue((double)pressMovement.Value, values.Skip(1).Any(v => v.PressMovement != values[0].PressMovement));
            RefreshInputThresholdTooltips();
        }
        void RefreshInputThresholdTooltips()
        {
            string instructions = Tr("\nVertikale Skala: 0 % oben, 100 % unten. Ziehen oder ↑/↓ für 0,1 %, Umschalt für 1 %. Esc verwirft die Bewegung. Gemischt: der Regler wartet auf deine gemeinsame Vorgabe.",
                "\nVertical scale: 0% at the top, 100% at the bottom. Drag or use ↑/↓ for 0.1%; Shift uses 1%. Esc discards the gesture. Mixed: the slider waits for your shared value.");
            foreach (var pair in new[] { new { Slider = actuationSlider, Number = actuationPoint }, new { Slider = releaseSlider, Number = releaseMovement }, new { Slider = repressSlider, Number = pressMovement } })
            {
                string text = keyCardTips.GetToolTip(pair.Number) + instructions;
                pair.Slider.AccessibleName = pair.Number == actuationPoint ? Tr("Auslösepunkt in Prozent", "Actuation point in percent") : pair.Number == releaseMovement ? Tr("Loslassweg in Prozent", "Release movement in percent") : Tr("Erneuter Druckweg in Prozent", "Repress movement in percent");
                pair.Slider.AccessibleDescription = text; keyCardTips.SetToolTip(pair.Slider, text);
            }
            RefreshInputThresholdCaptureButtons();
        }
        void RefreshInputThresholdAvailability()
        {
            bool idle = inputThresholdCaptureReader == null, selected = editingInputKeys.Length != 0;
            rapidTrigger.Enabled = actuationPoint.Enabled = idle && selected;
            releaseMovement.Enabled = pressMovement.Enabled = idle && selected && rapidTrigger.CheckState != CheckState.Unchecked;
            actuationSlider.Enabled = actuationPoint.Enabled; releaseSlider.Enabled = releaseMovement.Enabled; repressSlider.Enabled = pressMovement.Enabled;
            applyKeyBehavior.Enabled = idle && selected;
            if (!idle) resetKeyBehavior.Enabled = false;
            else resetKeyBehavior.Enabled = history.Current.Inputs.Any(i => editingInputKeys.Contains(i.KeyIndex));
            RefreshInputThresholdCaptureButtons();
        }
    }
}
