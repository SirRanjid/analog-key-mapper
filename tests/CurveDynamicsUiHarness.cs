using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static void CheckDynamicsRails(CurveCanvas canvas, bool visible, string label)
        {
            Check(Field<Control>(canvas, "inputRangeRail").Visible == visible, label + ": input range visibility matches the graph axes.");
            Check(Field<Control>(canvas, "outputRangeRail").Visible == visible, label + ": output range visibility matches the graph axes.");
        }
        static double[] DynamicsRailValues(CurveCanvas canvas)
        {
            var input = Field<CurveRangeRail>(canvas, "inputRangeRail"); var output = Field<CurveRangeRail>(canvas, "outputRangeRail");
            return new[] { input.ValueAt(0), input.ValueAt(1), output.ValueAt(0), output.ValueAt(1) };
        }
        static void RunCurveDynamicsUi(MainForm form, string artifacts)
        {
            int started = assertions; Profile original = Current(form), fixture = Current(form);
            Size originalSize = form.ClientSize; string language = UiText.Language;
            fixture.Bindings.RemoveAll(binding => binding.KeyIndex == 21);
            fixture.Inputs.RemoveAll(input => input.KeyIndex == 21);
            Call(form, "Commit", fixture); SelectKeys(form, 21); DetailMode(form, "advanced");
            var canvas = Field<CurveCanvas>(form, "curve");
            Check(canvas.Settings == null && !canvas.InputPreviewConfigured && canvas.ActiveInputField == InputActivationFields.None,
                "An unmapped legacy key does not fabricate an existing 10-percent actuation setting.");
            string before = Json(Current(form));
            Call(form, "PreviewCurveInputOption", InputActivationFields.Actuation, (double?)42);
            Check(canvas.ActiveInputField == InputActivationFields.Actuation && Math.Abs(canvas.InputPreview.ActuationPoint - .42) < 1e-10,
                "Actuation can be visualized independently of any controller mapping.");
            Check((bool)typeof(CurveCanvas).GetProperty("DynamicPreviewVisible", Private).GetValue(canvas, null),
                "Unmapped actuation is rendered before the no-mapping placeholder.");
            CheckDynamicsRails(canvas, false, "Unmapped actuation illustration");
            using (var image = new Bitmap(canvas.Width, canvas.Height)) canvas.DrawToBitmap(image, canvas.ClientRectangle);
            Equal(before, Json(Current(form)), "Independent actuation preview never creates a mapping or writes settings.");
            Call(form, "RefreshCurveInputPreview");
            Check(!canvas.InputPreviewConfigured, "Canceling unmapped preview restores continuous legacy behavior.");
            CheckDynamicsRails(canvas, true, "Returning from unmapped actuation");

            fixture.Inputs.Add(new KeyInputSettings { KeyIndex = 21, RapidTriggerEnabled = true, ActuationPoint = .3, ReleaseMovement = .12, PressMovement = .18 });
            Call(form, "Commit", fixture); SelectKeys(form, 21); DetailMode(form, "advanced");
            before = Json(Current(form));
            Field<NumericUpDown>(form, "actuationPoint").Value = 37.5M;
            Check(Math.Abs(canvas.InputPreview.ActuationPoint - .375) < 1e-10 && canvas.ActiveInputField == InputActivationFields.Actuation,
                "A numeric threshold draft updates its input guide without pressing Apply.");
            var release = Field<InputThresholdSlider>(form, "releaseSlider");
            ThresholdCall(release, "BeginEdit", false); ThresholdCall(release, "Preview", 28.0);
            Check(Math.Abs(canvas.InputPreview.ReleaseMovement - .28) < 1e-10 && canvas.ActiveInputField == InputActivationFields.Release,
                "A live vertical movement slider previews its relative travel in the graph.");
            release.CancelEdit();
            Check(Math.Abs(canvas.InputPreview.ActuationPoint - .375) < 1e-10 && Math.Abs(canvas.InputPreview.ReleaseMovement - .12) < 1e-10,
                "Canceling a movement gesture retains earlier pending numeric changes.");
            Equal(before, Json(Current(form)), "Input visualizations preserve the existing draft/apply contract.");
            Call(form, "ResetKeyBehavior"); Call(form, "Commit", fixture); SelectKeys(form, 21); DetailMode(form, "advanced");
            Call(form, "SwitchLanguage", "en");
            foreach (Size size in new[] { DefaultClientSize, new Size(SupportedMinimumSize.Width - (form.Width - form.ClientSize.Width), SupportedMinimumSize.Height - (form.Height - form.ClientSize.Height)) })
            {
                SetPreviewClientSize(form, size); Pump(form);
                foreach (InputActivationFields field in new[] { InputActivationFields.Actuation, InputActivationFields.Release, InputActivationFields.Press })
                {
                    string settingsBeforeView = Json(Current(form)); double[] rangesBeforeView = DynamicsRailValues(canvas);
                    Call(form, "PreviewCurveInputOption", field, null);
                    Check(canvas.ActiveInputField == field, "Active input illustration follows its selected option at " + size);
                    CheckDynamicsRails(canvas, false, field + " illustration at " + size);
                    CapturePreview(form, artifacts, "curve-input-" + field.ToString().ToLowerInvariant() + "-" + (size == DefaultClientSize ? "normal" : "minimum"));
                    Field<Button>(canvas, "viewButton").PerformClick();
                    CheckDynamicsRails(canvas, true, "Response after " + field + " at " + size);
                    Check(rangesBeforeView.SequenceEqual(DynamicsRailValues(canvas)), "An input illustration round trip preserves every range value.");
                    Equal(settingsBeforeView, Json(Current(form)), "Showing and closing an input illustration never edits settings.");
                }
            }
            fixture.Bindings.Add(new Binding { KeyIndex = 21, Target = OutputTarget.LeftTrigger, Processing = new SignalSettings { Scale = .8, MaxOutput = .9, SmoothingTimeConstant = .15 } });
            Call(form, "Commit", fixture); SelectKeys(form, 21); DetailMode(form, "advanced");
            before = Json(Current(form)); double[] rangesBeforeTime = DynamicsRailValues(canvas);
            canvas.ActiveSetting = "SmoothingTimeConstant";
            CheckDynamicsRails(canvas, false, "Smoothing time view");
            using (var image = new Bitmap(canvas.Width, canvas.Height)) canvas.DrawToBitmap(image, canvas.ClientRectangle);
            var step = Field<SmoothingStepPreview>(canvas, "stepPreview");
            Check(step != null && step.Output[100] > 0 && step.Output[100] < step.SettledOutput,
                "Focused smoothing renders a real delayed step response instead of only a text note.");
            CapturePreview(form, artifacts, "curve-smoothing-time-minimum");
            SetPreviewClientSize(form, DefaultClientSize); Pump(form); canvas.ActiveSetting = "SmoothingTimeConstant";
            CheckDynamicsRails(canvas, false, "Smoothing after resizing");
            CapturePreview(form, artifacts, "curve-smoothing-time-normal");
            Field<Button>(canvas, "viewButton").PerformClick();
            Check(canvas.ActiveSetting == null && !canvas.ShapeEditing && canvas.ActiveInputField == InputActivationFields.None,
                "The Response button returns from an illustrative time view to the signal curve.");
            CheckDynamicsRails(canvas, true, "Response after smoothing");
            Field<Button>(canvas, "viewButton").PerformClick();
            Check(canvas.ShapeEditing, "The shape editor opens after a time-view round trip.");
            CheckDynamicsRails(canvas, true, "Shape editor");
            Field<Button>(canvas, "viewButton").PerformClick();
            CheckDynamicsRails(canvas, true, "Response after shape editing");
            Check(rangesBeforeTime.SequenceEqual(DynamicsRailValues(canvas)), "Time/shape view round trips preserve every range value.");
            Equal(before, Json(Current(form)), "Time/shape view changes never write profile settings.");
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null); SetPreviewClientSize(form, originalSize);
            Call(form, "SwitchLanguage", language); Pump(form);
            Console.WriteLine("CURVE DYNAMICS PASS: " + (assertions - started) + " assertions; unmapped key previews, pending edits, relative movement and true time response.");
        }
    }
}
