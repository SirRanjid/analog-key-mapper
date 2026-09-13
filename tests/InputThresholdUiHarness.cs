using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static object ThresholdCall(InputThresholdSlider slider, string name, params object[] arguments)
        { return typeof(InputThresholdSlider).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(slider, arguments); }
        static void CheckInputThresholdSliders(MainForm form)
        {
            int started = assertions; Profile original = Current(form), fixture = Current(form);
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 9, RapidTriggerEnabled = true, ActuationPoint = .2, ReleaseMovement = .03, PressMovement = .04 },
                new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .4, ReleaseMovement = .08, PressMovement = .1 },
                new KeyInputSettings { KeyIndex = 21, ActuationPoint = .7 }
            };
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "advanced"); RevealCurveSetting(form, Field<Control>(form, "keyBehaviorPanel"));
            var slider = Field<InputThresholdSlider>(form, "actuationSlider");
            var release = Field<InputThresholdSlider>(form, "releaseSlider");
            var repress = Field<InputThresholdSlider>(form, "repressSlider");
            var number = Field<NumericUpDown>(form, "actuationPoint");
            Profile before = Current(form);
            Check(slider.Mixed && release.Mixed && repress.Mixed, "Vertical controls show mixed thresholds without inventing shared values.");
            Check(slider.Enabled && release.Enabled && repress.Enabled, "Rapid Trigger enables the two movement sliders as well as initial actuation.");
            foreach (string name in new[] { "actuationSlider", "releaseSlider", "repressSlider", "calibrateActuation", "calibrateRelease", "calibrateRepress" })
                VisibleInside(form, Field<Control>(form, name), "vertical-input/" + name);
            Check(number.Top < slider.Top || number.Parent == slider.Parent && number.Bounds.Bottom <= slider.Bounds.Top,
                "The numeric editor sits above its vertical threshold scale.");
            ThresholdCall(slider, "BeginEdit", false); ThresholdCall(slider, "Preview", 37.5);
            Check(number.Value == 37.5M && !Field<bool>(form, "inputDirty"), "Vertical slider previews update the number without dirtying or configuring the profile.");
            Equal(Json(before), Json(Current(form)), "Moving a vertical slider never writes to the runtime.");
            ThresholdCall(slider, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Check(number.Value == 20M && slider.Mixed && !Field<bool>(form, "inputDirty"), "Escape restores the original numeric and mixed state.");
            ThresholdCall(slider, "BeginEdit", false); ThresholdCall(slider, "Preview", 35.7); ThresholdCall(slider, "FinishEdit");
            Check(Field<bool>(form, "inputDirty") && Field<InputActivationFields>(form, "inputFieldsDirty") == InputActivationFields.Actuation,
                "Releasing a vertical gesture marks only its own field for the existing Apply flow.");
            Equal(Json(before), Json(Current(form)), "Release preserves the existing pending-edit semantics until Apply.");
            Call(form, "SaveKeyBehavior"); Pump(form);
            double storedActuation = Current(form).Inputs.Single(input => input.KeyIndex == 9).ActuationPoint;
            Check(Math.Abs(storedActuation - .357) < 1e-12, "35.7 percent normalizes to the intended actuation fraction.");
            var expected = KeyInputEditing.ApplyActivation(before, new[] { 9, 14 }, new KeyInputSettings { ActuationPoint = storedActuation }, InputActivationFields.Actuation);
            Equal(Json(expected), Json(Current(form)), "Apply shares only the chosen actuation setting and preserves mixed RT settings and unselected keys.");
            Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "One undo restores the vertical threshold edit.");

            var capture = typeof(MainForm).GetMethod("CapturedInputThreshold", BindingFlags.Static | BindingFlags.NonPublic);
            double amount = (double)capture.Invoke(null, new object[] { new Calibration(20, 420), 180.0 });
            Check(amount == .4, "Per-option calibration uses the source key's own saved rest/bottom range.");
            var apply = typeof(MainForm).GetMethod("ApplyCapturedInputThreshold", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (InputActivationFields field in new[] { InputActivationFields.Actuation, InputActivationFields.Release, InputActivationFields.Press })
            {
                Profile measured = (Profile)apply.Invoke(null, new object[] { before, new[] { 9, 14 }, 9, field, amount });
                var expectedInput = new KeyInputSettings { ActuationPoint = amount, ReleaseMovement = amount, PressMovement = amount };
                Profile wanted = KeyInputEditing.ApplyActivation(before, new[] { 9, 14 }, expectedInput, field);
                Equal(Json(wanted), Json(measured), "Calibrating " + field + " changes exactly that option for the selected keys.");
                Equal(Json(before), Json(Current(form)), "Pure calibration result construction has no hardware/profile side effects.");
            }
            Check(!(bool)Field<Button>(form, "calibrateActuation").Enabled && !(bool)Field<Button>(form, "calibrateRelease").Enabled,
                "Per-option recording remains unavailable without live keyboard pressure data.");
            Field<CheckBox>(form, "rapidTrigger").CheckState = CheckState.Unchecked;
            Check(slider.Enabled && !release.Enabled && !repress.Enabled, "Turning Rapid Trigger off keeps actuation available and disables movement sliders.");
            Call(form, "ResetKeyBehavior"); Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null);
            Console.WriteLine("INPUT THRESHOLDS PASS: " + (assertions - started) + " assertions; vertical drafts, mixed fields, undo and scoped per-option capture.");
        }
    }
}
