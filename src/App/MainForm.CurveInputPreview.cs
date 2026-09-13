using System;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        bool curveInputEventsBuilt, curvePreviewFocusQueued;
        void BuildCurveInputPreviewEvents()
        {
            if (curveInputEventsBuilt) return;
            curveInputEventsBuilt = true;
            var fields = new[] { InputActivationFields.Actuation, InputActivationFields.Release };
            var groups = new Control[][] { new Control[] { actuationPoint, actuationSlider, calibrateActuation },
                new Control[] { releaseMovement, releaseSlider, calibrateRelease } };
            for (int i = 0; i < groups.Length; i++)
            {
                InputActivationFields field = fields[i];
                foreach (Control control in groups[i])
                {
                    control.Enter += delegate { if (!updatingInput) PreviewCurveInputOption(field, null); };
                    control.Leave += delegate { QueueCurvePreviewFocus(); };
                }
            }
            rapidTrigger.CheckedChanged += delegate { if (!updatingInput) RefreshCurveInputPreview(); };
            settings.CellEnter += delegate { if (settings.ContainsFocus) ShowFocusedCurveSetting(); };
            settings.Enter += delegate { ShowFocusedCurveSetting(); };
            settings.Leave += delegate { QueueCurvePreviewFocus(); };
        }
        void ShowFocusedCurveSetting()
        {
            if (IsDisposed || curve.IsDisposed || history == null) return;
            DataGridViewCell cell = settings.CurrentCell;
            curve.ActiveSetting = cell == null || cell.OwningRow == null ? null : cell.OwningRow.Tag as string;
        }
        void QueueCurvePreviewFocus()
        {
            if (curvePreviewFocusQueued || !IsHandleCreated || IsDisposed || Disposing) return;
            curvePreviewFocusQueued = true;
            BeginInvoke((MethodInvoker)delegate {
                curvePreviewFocusQueued = false;
                if (IsDisposed || Disposing || curve.IsDisposed || inputThresholdCaptureReader != null) return;
                if (actuationPoint.ContainsFocus || actuationSlider.ContainsFocus || calibrateActuation.ContainsFocus) PreviewCurveInputOption(InputActivationFields.Actuation, null);
                else if (releaseMovement.ContainsFocus || releaseSlider.ContainsFocus || calibrateRelease.ContainsFocus) PreviewCurveInputOption(InputActivationFields.Release, null);
                else { RefreshCurveInputPreview(); if (settings.ContainsFocus) ShowFocusedCurveSetting(); else curve.ActiveSetting = null; }
            });
        }
        void RefreshCurveInputPreview() { PreviewCurveInputOption(InputActivationFields.None, null); }
        void PreviewCurveInputOption(InputActivationFields field, double? percent)
        {
            if (IsDisposed || Disposing || curve.IsDisposed || history == null || updatingInput) return;
            if (editingInputKeys.Length == 0) { curve.UpdateInputPreview(null, false, false, InputActivationFields.None); return; }
            Profile profile = history.Current;
            InputActivationFields draftFields = inputFieldsDirty;
            KeyInputSettings first = null; bool firstConfigured = false, mixed = false;
            foreach (int key in editingInputKeys)
            {
                KeyInputSettings stored = profile.Inputs.FirstOrDefault(input => input.KeyIndex == key);
                var value = stored == null ? new KeyInputSettings { KeyIndex = key } : new KeyInputSettings { KeyIndex = key,
                    RapidTriggerEnabled = stored.RapidTriggerEnabled, ActuationPoint = stored.ActuationPoint,
                    ReleaseMovement = stored.ReleaseMovement, PressMovement = stored.ActuationPoint };
                if ((draftFields & InputActivationFields.RapidTrigger) != 0) value.RapidTriggerEnabled = rapidTrigger.Checked;
                if ((draftFields & InputActivationFields.Actuation) != 0) value.ActuationPoint = (double)actuationPoint.Value / 100;
                if ((draftFields & InputActivationFields.Release) != 0) value.ReleaseMovement = (double)releaseMovement.Value / 100;
                bool configured = stored != null || draftFields != InputActivationFields.None;
                if (percent.HasValue)
                {
                    double amount = Math.Max(.0001, Math.Min(1, percent.Value / 100)); configured = true;
                    if (field == InputActivationFields.Actuation) value.ActuationPoint = amount;
                    else if (field == InputActivationFields.Release) value.ReleaseMovement = amount;
                }
                value.PressMovement = value.ActuationPoint;
                if (first == null) { first = value; firstConfigured = configured; }
                else if (configured != firstConfigured || !CurveCanvas.SameInputPreview(first, value)) mixed = true;
            }
            curve.UpdateInputPreview(first, firstConfigured, mixed, field);
        }
    }
}
