using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static SignalSettings PresentationCurve()
        {
            return new SignalSettings { Curve = CurveKind.Bezier, TopDeadzone = .03, BottomDeadzone = .02,
                CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0, .15), new CurvePoint(.35, .12, .75),
                    new CurvePoint(.7, .6, 1.4), new CurvePoint(1, 1, .9) } };
        }
        static void SavePresentation(Form form, string artifacts, string name)
        {
            form.PerformLayout(); Application.DoEvents(); form.PerformLayout();
            Check(form.Visible && form.Left < -10000 && !form.ShowInTaskbar, "Presentation captures only the real offscreen synthetic application window.");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(artifacts, "presentation-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
        }
        static void RunPresentationUi(MainForm form, string artifacts)
        {
            int before = assertions;
            Profile original = Current(form);
            Call(form, "SwitchLanguage", "en"); Call(form, "SetLegendOverride", (int?)0);
            SetPreviewClientSize(form, new Size(1600, 960));
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            int shift = keyboard.LayoutModel.Keys.Single(key => key.Code == "ShiftLeft").KeyIndex.Value;
            int space = keyboard.LayoutModel.Keys.Single(key => key.Code == "Space").KeyIndex.Value;
            var demo = ControllerRouting.Rename(new Profile { Name = "Movement and acceleration · example" }, "main", "Player 1");
            demo = ControllerRouting.Add(demo, "presentation-player-2", "Player 2", ControllerKind.DualSense);
            demo = ControllerRouting.SetRgbColor(demo, "main", 0xB6A4FF);
            demo = ControllerRouting.SetRgbColor(demo, "presentation-player-2", 0x73D0BD);
            demo.Bindings = new List<Binding> {
                new Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive, Processing = PresentationCurve() },
                new Binding { KeyIndex = 15, Target = OutputTarget.LeftYNegative, Processing = PresentationCurve() },
                new Binding { KeyIndex = 9, Target = OutputTarget.LeftXNegative, Processing = PresentationCurve() },
                new Binding { KeyIndex = 21, Target = OutputTarget.LeftXPositive, Processing = PresentationCurve() },
                new Binding { KeyIndex = 14, Target = OutputTarget.RightTrigger, Processing = new SignalSettings { TopDeadzone = .02, Scale = .9 } },
                new Binding { KeyIndex = shift, Target = OutputTarget.LeftTrigger },
                new Binding { KeyIndex = space, Target = OutputTarget.A }
            };
            demo.Inputs = new[] { 9, 14, 15, 21 }.Select(key => new KeyInputSettings {
                KeyIndex = key, RapidTriggerEnabled = true, ActuationPoint = .18, PressMovement = .18, ReleaseMovement = .03 }).ToList();
            demo = KeyInputEditing.SetPairing(demo, new[] { 14 }, 15, InputOpposedPolicy.LastPressed);
            demo = KeyInputEditing.SetPairing(demo, new[] { 9 }, 21, InputOpposedPolicy.LastPressed);
            MappingValidation.RequireValid(demo);
            string movement = demo.Bindings.Single(binding => binding.KeyIndex == 14 && binding.Target == OutputTarget.LeftYPositive).BindingId;
            using (var input = new DialogInput())
            {
                typeof(MainForm).GetField("reader", Private).SetValue(form, input.Reader);
                typeof(MainForm).GetField("calibration", Private).SetValue(form, new CalibrationDocument {
                    DeviceIdentity = new string('8', 64), ProtocolFingerprint = input.Reader.Fingerprint, ScaleMaximum = 385,
                    KeyRanges = new[] { 9, 14, 15, 21 }.Select(key => new KeyPressureRangeEntry { KeyIndex = key, Minimum = 8, Maximum = 360 }).ToList() });
                try
                {
                    Call(form, "Commit", demo); Call(form, "SaveProfile"); Call(form, "ReloadProfiles");
                    foreach (int key in new[] { 9, 14, 15, 21, shift, space }) PushDialog(input, key, 0);
                    Call(form, "UpdateLive");
                    SelectKeys(form, 9, 14, 15, 21); DetailMode(form, "controller");
                    Check(Field<Control>(form, "controllerPreview").Visible, "The presentation hero shows the actual controller and assigned key labels.");
                    SavePresentation(form, artifacts, "controller");

                    SelectKeys(form, 14); SelectBindings(form, movement); DetailMode(form, null);
                    Check(Field<Button>(form, "calibrateRange").Enabled, "The pressure presentation offers real calibration through a synthetic reader.");
                    SavePresentation(form, artifacts, "mapping");

                    DetailMode(form, "advanced"); SelectBindings(form, movement);
                    var canvas = Field<CurveCanvas>(form, "curve"); canvas.ShapeEditing = false;
                    Field<Button>(canvas, "viewButton").PerformClick(); Pump(form);
                    Point knot = CurveLocation(canvas, .35, .12);
                    CurveCanvasMouse(canvas, "OnMouseDown", knot, MouseButtons.Left); CurveCanvasMouse(canvas, "OnMouseUp", knot, MouseButtons.Left); Pump(form);
                    Check(canvas.ShapeEditing && canvas.Settings.Curve == CurveKind.Bezier && canvas.Width == canvas.Height,
                        "The curve presentation shows real editable Bezier points on the square canvas.");
                    SavePresentation(form, artifacts, "response-curve");

                    Field<Button>(canvas, "viewButton").PerformClick(); Pump(form);
                    Field<InputThresholdSlider>(form, "actuationSlider").Focus(); Call(form, "PreviewCurveInputOption", InputActivationFields.Actuation, null); Pump(form);
                    Check(Field<Button>(form, "calibrateActuation").Enabled && Field<Button>(form, "calibrateRelease").Enabled,
                        "The behavior presentation includes both functional calibration controls.");
                    SavePresentation(form, artifacts, "key-behavior");

                    DetailMode(form, "input"); RevealKeySetting(form, Field<Control>(form, "keySocdPanel")); ArmSocdCapture(form); Pump(form);
                    SavePresentation(form, artifacts, "socd-opposite-drop"); Call(form, "CancelSocdCapture", false);
                    Check(!Field<MultiControllerSession>(form, "runtime").AnyEnabled, "Presentation data never activates a controller output.");
                }
                finally
                {
                    Call(form, "CancelSocdCapture", false);
                    typeof(MainForm).GetField("reader", Private).SetValue(form, null);
                    typeof(MainForm).GetField("calibration", Private).SetValue(form, null);
                    Call(form, "Commit", original); Call(form, "Configure");
                }
            }
            var source = new LearningUiSource('9', LearningButton("button-1", null), LearningButton("button-2", null));
            using (var dialog = new LearnInputsDialog(new[] { 14, 9 }, new[] { "W", "A" }, 2,
                delegate { return new[] { LearningChoice(source, "hid") }; }, delegate { return true; }))
            {
                DialogResult result = RunLearningModal(dialog, delegate {
                    LearningGesture(dialog, source, "button-1"); LearningGesture(dialog, source, "button-2");
                    Check(dialog.Bindings.Length == 2 && Field<Button>(dialog, "apply").Enabled, "The learning presentation shows an actual completed, staged review.");
                    SavePresentation(dialog, artifacts, "input-learning"); Field<Button>(dialog, "apply").PerformClick();
                });
                Check(result == DialogResult.OK && object.ReferenceEquals(dialog.TakeSource(), source), "The learning presentation completes its ordinary modal transaction.");
            }
            source.Dispose();
            foreach (string name in new[] { "controller", "mapping", "response-curve", "key-behavior", "socd-opposite-drop", "input-learning" })
                Check(new FileInfo(Path.Combine(artifacts, "presentation-" + name + ".png")).Length > 1000, "The current UI rendered the complete presentation image: " + name);
            AssertPassive(form);
            Console.WriteLine("PRESENTATION UI PASS: " + (assertions - before) + " assertions; six coherent English captures, synthetic input, no hardware or controller output.");
        }
    }
}
