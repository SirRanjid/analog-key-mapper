using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static void ClickInputCalibration(MainForm form, string buttonName)
        {
            var button = Field<Button>(form, buttonName);
            Check(button.Enabled, "The scoped recording action is enabled for the injected reader: " + buttonName);
            button.PerformClick();
            Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") != null, "Clicking the real option button subscribes its measurement to the existing reader.");
        }
        static void AssertCalibrationUnchanged(MainForm form, CalibrationDocument original, string path, string json)
        {
            Check(Object.ReferenceEquals(original, Field<CalibrationDocument>(form, "calibration")) && File.ReadAllText(path) == json,
                "Option calibration leaves every saved pressure range, global scale and calibration file unchanged.");
            var range = Field<KeyboardPressureRange>(form, "sharedPressureRange");
            Check(range.ForKey(9).Rest == 20 && range.ForKey(9).Bottom == 420 && range.ForKey(14).Rest == 10 && range.ForKey(14).Bottom == 210 &&
                range.ForKey(21).Rest == 50 && range.ForKey(21).Bottom == 500 && range.ScaleMaximum == 600,
                "Selected and unselected pressure endpoints and the shared display scale remain intact.");
        }
        static void CheckCapturedOption(MainForm form, Profile before, InputActivationFields field, double amount)
        {
            var values = new KeyInputSettings { ActuationPoint = amount, ReleaseMovement = amount, PressMovement = amount };
            var expected = KeyInputEditing.ApplyActivation(before, new[] { 9, 14 }, values, field);
            Equal(Json(expected), Json(Current(form)), "Release commits only " + field + " for every selected key, retaining other mixed fields and unselected keys.");
            Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") == null, "Completed option capture unsubscribes before refreshing the editor.");
            Call(form, "Undo"); Pump(form);
            Equal(Json(before), Json(Current(form)), "One undo restores the whole completed " + field + " measurement.");
        }
        static void RunInputThresholdCapture(string artifacts)
        {
            int started = assertions;
            string data = Path.Combine(artifacts, "input-threshold-capture-data");
            using (var input = new DialogInput())
            using (var form = PressurePreview(data, input))
            {
                var store = Field<WorkspaceStore>(form, "store");
                var calibration = new CalibrationDocument { DeviceIdentity = new string('c', 64), ProtocolFingerprint = input.Reader.Fingerprint,
                    ScaleMaximum = 600, KeyRanges = new List<KeyPressureRangeEntry> {
                        new KeyPressureRangeEntry { KeyIndex = 9, Minimum = 20, Maximum = 420 },
                        new KeyPressureRangeEntry { KeyIndex = 14, Minimum = 10, Maximum = 210 },
                        new KeyPressureRangeEntry { KeyIndex = 21, Minimum = 50, Maximum = 500 }
                    } };
                store.SaveCalibration(calibration);
                typeof(MainForm).GetField("calibration", Private).SetValue(form, calibration); Call(form, "Configure");
                Profile fixture = Current(form);
                fixture.Inputs = new List<KeyInputSettings> {
                    new KeyInputSettings { KeyIndex = 9, RapidTriggerEnabled = true, ActuationPoint = .2, ReleaseMovement = .03, PressMovement = .04 },
                    new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .6, ReleaseMovement = .08, PressMovement = .1 },
                    new KeyInputSettings { KeyIndex = 21, ActuationPoint = .7 }
                };
                Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "input");
                string path = store.CalibrationPath(calibration.DeviceIdentity), saved = File.ReadAllText(path);
                Profile before = Current(form);
                Check(!input.Reader.HasReceivedSamples, "The first end-to-end recording begins with no prior selected-key snapshot.");

                // Existing Keys/card geometry stays fixed at both supported sizes.
                Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
                foreach (Size size in new[] { DefaultClientSize, new Size(SupportedMinimumSize.Width - chrome.Width, SupportedMinimumSize.Height - chrome.Height) })
                {
                    SetPreviewClientSize(form, size); DetailMode(form, "input");
                    var keyboard = Field<VisualKeyboard>(form, "keyboard"); Rectangle bounds = Relative(form, keyboard);
                    foreach (string name in new[] { "actuationSlider", "releaseSlider", "repressSlider", "actuationPoint", "releaseMovement", "pressMovement", "calibrateActuation", "calibrateRelease", "calibrateRepress", "applyKeyBehavior", "resetKeyBehavior" })
                        VisibleInside(form, Field<Control>(form, name), "recording-layout/" + size + "/" + name);
                    CapturePreview(form, artifacts, "input-thresholds-" + size.Width + "x" + size.Height);
                    DetailMode(form, "advanced"); Check(Relative(form, keyboard) == bounds, "Switching from the vertical Keys controls to Curve retains keyboard bounds.");
                    DetailMode(form, "input");
                }
                SetPreviewClientSize(form, DefaultClientSize);

                ClickInputCalibration(form, "calibrateActuation");
                Call(form, "UpdateLive");
                Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") != null, "Waiting without initial samples keeps recording armed.");
                PushDialog(input, 21, 300, 50); Call(form, "UpdateLive");
                Check(Field<int>(form, "inputThresholdCaptureSource") == -1, "An unselected key cannot become the option recording source.");
                PushDialog(input, 9, 60); Call(form, "UpdateLive");
                var status = Field<Label>(form, "keyBehaviorStatus"); Rectangle statusBounds = status.Bounds;
                var statusAncestors = new List<Control>();
                for (Control parent = status.Parent; parent != null; parent = parent.Parent) statusAncestors.Add(parent);
                int statusLayouts = 0;
                LayoutEventHandler statusLayout = delegate { statusLayouts++; };
                foreach (Control parent in statusAncestors) parent.Layout += statusLayout;
                try
                {
                    // Exercise only real input delivery and the regular live tick.
                    // Pump/PerformLayout would hide which readout triggered layout.
                    foreach (int peak in new[] { 90, 120, 150, 180 })
                    {
                        PushDialog(input, 9, peak); Call(form, "UpdateLive");
                        Check(status.Bounds == statusBounds, "A new calibration peak keeps the live status bounds fixed.");
                    }
                }
                finally { foreach (Control parent in statusAncestors) parent.Layout -= statusLayout; }
                Check(statusLayouts == 0, "Changing calibration peak text does not lay out any status ancestor or the surrounding editor.");
                Equal(Json(before), Json(Current(form)), "A held press only previews the measured threshold and does not write the profile.");
                Check(Field<int>(form, "inputThresholdCaptureSource") == 9 && Field<InputThresholdSlider>(form, "actuationSlider").MeasuredPercent == 40,
                    "The first selected press owns the recording and its peak appears on the vertical scale.");
                PushDialog(input, 14, 160, 10); PushDialog(input, 9, 20); Call(form, "UpdateLive");
                CheckCapturedOption(form, before, InputActivationFields.Actuation, .4); AssertCalibrationUnchanged(form, calibration, path, saved);

                // A held-at-start key must first release and then perform a new press.
                PushDialog(input, 9, 180); ClickInputCalibration(form, "calibrateRelease");
                PushDialog(input, 9, 240, 20); Call(form, "UpdateLive");
                Equal(Json(before), Json(Current(form)), "Releasing a key held at calibration start arms the capture without applying the existing hold.");
                Check(Field<int>(form, "inputThresholdCaptureSource") == -1 && Field<ReaderSession>(form, "inputThresholdCaptureReader") != null,
                    "A released initial hold waits for the next deliberate press.");
                PushDialog(input, 9, 60, 100, 20); Call(form, "UpdateLive");
                CheckCapturedOption(form, before, InputActivationFields.Release, .2); AssertCalibrationUnchanged(form, calibration, path, saved);

                PushDialog(input, 14, 10); ClickInputCalibration(form, "calibrateRepress");
                PushDialog(input, 14, 35, 60, 10); Call(form, "UpdateLive");
                CheckCapturedOption(form, before, InputActivationFields.Press, .25); AssertCalibrationUnchanged(form, calibration, path, saved);

                PushDialog(input, 9, 20); ClickInputCalibration(form, "calibrateActuation"); PushDialog(input, 9, 100); Call(form, "UpdateLive");
                SelectKeys(form, 21); PushDialog(input, 9, 20); Call(form, "UpdateLive");
                Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") == null, "Changing selected keys cancels the real reader subscription.");
                Equal(Json(before), Json(Current(form)), "A canceled selection cannot apply a late release to the new key.");

                SelectKeys(form, 9, 14); DetailMode(form, "input"); PushDialog(input, 9, 20); ClickInputCalibration(form, "calibrateActuation");
                using (var replacement = new DialogInput())
                {
                    typeof(MainForm).GetField("reader", Private).SetValue(form, replacement.Reader); Call(form, "UpdateLive");
                    Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") == null, "Changing the connected reader cancels the old source before a late report can apply.");
                    typeof(MainForm).GetField("reader", Private).SetValue(form, input.Reader);
                }
                Call(form, "RefreshInputEditor"); PushDialog(input, 9, 20); ClickInputCalibration(form, "calibrateActuation");
                PushDialog(input, 9, 160); Call(form, "UpdateLive");
                AwaitDialog(delegate { return input.Reader.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds).Any(s => s.KeyIndex == 9 && s.Stale); },
                    "The synthetic source becomes stale after the real configured input-age limit.");
                Call(form, "UpdateLive");
                Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") == null, "Stale pressure after a press cancels its pending calibration.");
                Equal(Json(before), Json(Current(form)), "Reader replacement and stale cancellation preserve all profile fields.");

                PushDialog(input, 9, 20); ClickInputCalibration(form, "calibrateActuation");
                input.Source.Push(new IOException("Synthetic option capture disconnect"));
                AwaitDialog(delegate { return !input.Reader.IsReading; }, "The injected source reports a deterministic disconnect."); Call(form, "UpdateLive");
                Check(Field<ReaderSession>(form, "inputThresholdCaptureReader") == null, "Reader disconnection cancels the real pending capture.");
                Equal(Json(before), Json(Current(form)), "Disconnect never applies an incomplete threshold."); AssertCalibrationUnchanged(form, calibration, path, saved);
                form.Close();
            }
            Console.WriteLine("INPUT CAPTURE UI PASS: " + (assertions - started) + " assertions; real synthetic reader, all three options, release/undo, source isolation and cancellation.");
        }
    }
}
