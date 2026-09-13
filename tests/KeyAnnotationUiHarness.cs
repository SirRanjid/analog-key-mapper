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
        static void RunKeyAnnotations(MainForm form, string artifacts)
        {
            Profile original = Current(form); string language = UiText.Language;
            CalibrationDocument originalCalibration = Field<CalibrationDocument>(form, "calibration");
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Profile fixture = ControllerRouting.Add(Current(form), "annotation-other", "Other", ControllerKind.Xbox360);
            fixture.Bindings = new List<Tk75.Mapping.Binding> {
                new Tk75.Mapping.Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive },
                new Tk75.Mapping.Binding { KeyIndex = 9, Target = OutputTarget.LeftXNegative },
                new Tk75.Mapping.Binding { KeyIndex = 20, Target = OutputTarget.A, Processing = new SignalSettings { ButtonThreshold = .35 } },
                new Tk75.Mapping.Binding { KeyIndex = 21, Target = OutputTarget.LeftTrigger, Processing = new SignalSettings { MinOutput = .1, MaxOutput = .8 } },
                new Tk75.Mapping.Binding { KeyIndex = 27, ControllerId = "annotation-other", Target = OutputTarget.B, Processing = new SignalSettings { ButtonThreshold = .65 } },
                new Tk75.Mapping.Binding { KeyIndex = 32, Target = OutputTarget.A, Processing = new SignalSettings { ButtonThreshold = .3 } },
                new Tk75.Mapping.Binding { KeyIndex = 32, Target = OutputTarget.B, Processing = new SignalSettings { ButtonThreshold = .7 } }
            };
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 14, ActuationPoint = .18, RapidTriggerEnabled = true, PressMovement = .04, ReleaseMovement = .03 },
                new KeyInputSettings { KeyIndex = 9, ActuationPoint = .25 }
            };
            try
            {
                typeof(MainForm).GetField("calibration", Private).SetValue(form, new CalibrationDocument {
                    GlobalMinimum = 0, GlobalMaximum = 385, ScaleMaximum = 385,
                    KeyRanges = new List<KeyPressureRangeEntry> { new KeyPressureRangeEntry { KeyIndex = 26, Minimum = 12, Maximum = 330 } }
                });
                Call(form, "Commit", fixture);
                foreach (string requestedLanguage in new[] { "en", "de" })
                {
                    Call(form, "SwitchLanguage", requestedLanguage);
                    var annotations = Field<Dictionary<int, KeyboardBehaviorAnnotation>>(keyboard, "behaviorAnnotations");
                    Check(annotations[14].Compact == "↓18↑3" && annotations[14].Full == "↓18↑3" && annotations[14].Narrow == "18/3", "RT cap metadata shows only the shared actuation value and release movement, including on narrow caps.");
                    Check(annotations[9].Compact == "↕25" && annotations[9].Full == "↕25", "Equal fixed press/release values use one compact, explicitly shared boundary.");
                    Check(annotations[15].Compact == "" && annotations[15].Description.Contains(requestedLanguage == "en" ? "Continuous pressure" : "Kontinuierlicher Druck"), "An untouched continuous key does not invent a 10% actuation gate.");
                    Check(annotations[20].Compact == "●35" && annotations[20].Description.Contains(requestedLanguage == "en" ? "calculated output" : "berechneten Ausgabe"), "A mapped digital key shows its actual output threshold with distinct semantics from physical actuation.");
                    Check(annotations[21].Compact == "→10–80" && annotations[21].Narrow == "→80", "A mapped analog key shows its actual output range or its maximum on a narrow cap.");
                    Check(annotations[27].Compact == "●65", "Configured keys on another controller also receive output-setting annotations.");
                    Check(annotations[32].Compact == "→…" && annotations[32].Description.Contains("●30") && annotations[32].Description.Contains("●70"), "Different outputs remain explicit instead of inventing one shared threshold.");
                    Check(annotations[26].Compact == "R12–330" && annotations[26].Description.Contains(requestedLanguage == "en" ? "Individual raw" : "Individueller Rohdruck"), "A key with only an explicit per-key pressure range is visibly configured without a fake actuation gate.");
                    string tip = (string)typeof(VisualKeyboard).GetMethod("TooltipFor", Private).Invoke(keyboard, new object[] { keyboard.LayoutModel.FindByIndex(14) });
                    Check(tip.Contains("18%") && tip.Contains("3%") && !tip.Contains("4%") && !tip.Contains("mm"), "The key tooltip reports effective thresholds and never presents obsolete legacy repress or unmeasured millimeters.");
                    object snapshot = annotations[14];
                    for (int i = 0; i < 5; i++) Call(form, "RefreshKeyBehaviorAnnotations");
                    Check(Object.ReferenceEquals(snapshot, annotations[14]), "Unchanged annotation refreshes keep their immutable presentation snapshot.");
                    RectangleF before = keyboard.GetKeyBounds(keyboard.LayoutModel.FindByIndex(14));
                    keyboard.UpdateKeyState(14, true, true, .4, false); Pump(form);
                    Check(before == keyboard.GetKeyBounds(keyboard.LayoutModel.FindByIndex(14)), "Live pressure does not resize annotated keys.");
                    CapturePreview(form, artifacts, "key-annotations-" + requestedLanguage);
                }
                using (var detached = new VisualKeyboard { LayoutModel = KeyboardLayout.Tk75Iso(), Size = new Size(1000, 440) })
                {
                    int invalidations = 0;
                    var value = new KeyboardBehaviorAnnotation { Compact = "↓18↑3", Full = "↓18↑3", Narrow = "18/3", Description = "test description" };
                    detached.CreateControl(); detached.SetBehaviorAnnotation(14, value);
                    value.Compact = "mutated";
                    Check(Field<Dictionary<int, KeyboardBehaviorAnnotation>>(detached, "behaviorAnnotations")[14].Compact == "↓18↑3", "Caller mutations cannot alter painted annotations.");
                    detached.Invalidated += delegate { invalidations++; };
                    detached.SetBehaviorAnnotation(14, new KeyboardBehaviorAnnotation { Compact = "↓18↑3", Full = "↓18↑3", Narrow = "18/3", Description = "test description" });
                    Check(invalidations == 0, "Identical annotations cause no repaint.");
                    using (var decorated = new Bitmap(detached.Width, detached.Height))
                    using (var plain = new Bitmap(detached.Width, detached.Height))
                    {
                        detached.DrawToBitmap(decorated, detached.ClientRectangle);
                        detached.SetBehaviorAnnotation(14, null); detached.DrawToBitmap(plain, detached.ClientRectangle);
                        Rectangle key = Rectangle.Round(detached.GetKeyBounds(detached.LayoutModel.FindByIndex(14)));
                        int changed = 0;
                        for (int y = key.Bottom - 14; y < key.Bottom - 2; y++)
                            for (int x = key.Left + 2; x < key.Right - 2; x++) if (decorated.GetPixel(x, y) != plain.GetPixel(x, y)) changed++;
                        Check(changed > 5, "Threshold information is visibly painted in the key corner.");
                        decorated.Save(Path.Combine(artifacts, "key-annotation-keyboard.png"));
                    }
                    detached.SetBehaviorAnnotation(14, new KeyboardBehaviorAnnotation { Compact = "↓18%" });
                    detached.LayoutModel = KeyboardLayout.Tk75Ansi();
                    Check(Field<Dictionary<int, KeyboardBehaviorAnnotation>>(detached, "behaviorAnnotations").Count == 0, "Changing the physical layout clears annotations for the previous keyboard.");
                }
                CheckCompactConfiguredKeyCaps(artifacts);
            }
            finally { typeof(MainForm).GetField("calibration", Private).SetValue(form, originalCalibration); Call(form, "Commit", original); Call(form, "SwitchLanguage", language); }
        }
        static void CheckCompactConfiguredKeyCaps(string artifacts)
        {
            int[] indices = { 9, 14, 20, 21, 26, 27, 32, 5, 71, 17, 59, 4, 76, 80 };
            foreach (int width in new[] { 650, 860, 1100 })
            using (var keyboard = new VisualKeyboard { LayoutModel = KeyboardLayout.Tk75Iso(), Size = new Size(width, (int)(width * .43)) })
            {
                var bounds = new Dictionary<int, RectangleF>(); var labels = new Dictionary<int, string>();
                foreach (int index in indices)
                {
                    var key = keyboard.LayoutModel.FindByIndex(index);
                    Check(key != null, "The compact annotation fixture references a physical key.");
                    bounds.Add(index, keyboard.GetKeyBounds(key)); labels.Add(index, keyboard.GetKeyLabel(key));
                    KeyboardBehaviorAnnotation annotation = index == 9 ? new KeyboardBehaviorAnnotation { Compact = "↕25" } :
                        index == 20 ? new KeyboardBehaviorAnnotation { Compact = "●35" } :
                        index == 21 ? new KeyboardBehaviorAnnotation { Compact = "→10–80", Narrow = "→80" } :
                        index == 26 ? new KeyboardBehaviorAnnotation { Compact = "R12–330", Narrow = "R" } :
                        index == 32 ? new KeyboardBehaviorAnnotation { Compact = "→…" } :
                        new KeyboardBehaviorAnnotation { Compact = "↓18↑3", Full = "↓18↑3", Narrow = "18/3" };
                    keyboard.SetBehaviorAnnotation(index, annotation);
                    keyboard.SetControllerAssignments(index, new[] { new KeyboardControllerBadge { Number = 1, Name = "Controller", Selected = true } });
                }
                keyboard.CreateControl();
                using (var decorated = new Bitmap(keyboard.Width, keyboard.Height))
                using (var plain = new Bitmap(keyboard.Width, keyboard.Height))
                {
                    keyboard.DrawToBitmap(decorated, keyboard.ClientRectangle);
                    foreach (int index in indices) keyboard.SetBehaviorAnnotation(index, null);
                    keyboard.DrawToBitmap(plain, keyboard.ClientRectangle);
                    foreach (int index in indices)
                    {
                        Rectangle key = Rectangle.Round(bounds[index]);
                        int changed = 0;
                        // Count only the reserved footer, not the main legend
                        // which can use its existing compact font at small sizes.
                        for (int y = key.Bottom - 10; y < key.Bottom - 2; y++)
                            for (int x = key.Left + 2; x < key.Right - 2; x++)
                                if (decorated.GetPixel(x, y) != plain.GetPixel(x, y)) changed++;
                        Check(changed >= 3, "Configured key " + index + " visibly retains compact settings with its controller badge at keyboard width " + width + ".");
                        Check(keyboard.GetKeyBounds(keyboard.LayoutModel.FindByIndex(index)) == bounds[index] && keyboard.GetKeyLabel(keyboard.LayoutModel.FindByIndex(index)) == labels[index],
                            "Annotations preserve key geometry and original modifier/key legends at width " + width + ".");
                    }
                    decorated.Save(Path.Combine(artifacts, "key-annotation-compact-" + width + ".png"));
                }
            }
        }
    }
}
