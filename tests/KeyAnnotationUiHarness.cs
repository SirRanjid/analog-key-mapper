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
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Profile fixture = Current(form);
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 14, ActuationPoint = .18, RapidTriggerEnabled = true, PressMovement = .04, ReleaseMovement = .03 },
                new KeyInputSettings { KeyIndex = 9, ActuationPoint = .25 }
            };
            try
            {
                Call(form, "Commit", fixture);
                foreach (string requestedLanguage in new[] { "en", "de" })
                {
                    Call(form, "SwitchLanguage", requestedLanguage);
                    var annotations = Field<Dictionary<int, KeyboardBehaviorAnnotation>>(keyboard, "behaviorAnnotations");
                    Check(annotations[14].Full.Contains("↓18%") && annotations[14].Full.Contains("↑3%") && annotations[14].Full.Contains("↧4%"), "RT cap metadata distinguishes initial actuation, release movement and repress movement.");
                    Check(annotations[9].Full.Contains("↓25%") && annotations[9].Full.Contains("↑25%"), "Fixed actuation displays its actual common press/release boundary.");
                    Check(annotations[15].Compact == "" && annotations[15].Description.Contains(requestedLanguage == "en" ? "Continuous pressure" : "Kontinuierlicher Druck"), "An untouched continuous key does not invent a 10% actuation gate.");
                    string tip = (string)typeof(VisualKeyboard).GetMethod("TooltipFor", Private).Invoke(keyboard, new object[] { keyboard.LayoutModel.FindByIndex(14) });
                    Check(tip.Contains("18%") && tip.Contains("3%") && tip.Contains("4%") && !tip.Contains("mm"), "The key tooltip exposes all thresholds without claiming measured millimeters.");
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
                    var value = new KeyboardBehaviorAnnotation { Compact = "↓18%", Full = "↓18% ↑3% ↧4%", Description = "test description" };
                    detached.CreateControl(); detached.SetBehaviorAnnotation(14, value);
                    value.Compact = "mutated";
                    Check(Field<Dictionary<int, KeyboardBehaviorAnnotation>>(detached, "behaviorAnnotations")[14].Compact == "↓18%", "Caller mutations cannot alter painted annotations.");
                    detached.Invalidated += delegate { invalidations++; };
                    detached.SetBehaviorAnnotation(14, new KeyboardBehaviorAnnotation { Compact = "↓18%", Full = "↓18% ↑3% ↧4%", Description = "test description" });
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
            }
            finally { Call(form, "Commit", original); Call(form, "SwitchLanguage", language); }
        }
    }
}
