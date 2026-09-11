using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    static class VisualKeyboardHarness
    {
        static int assertions;
        static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
        static void Throws(Action action, string message) { assertions++; try { action(); } catch (ArgumentException) { return; } throw new Exception(message); }
        static int Number(IDictionary<string, object> item, string key) { return Convert.ToInt32(item[key]); }
        static string Text(IDictionary<string, object> item, string key) { return Convert.ToString(item[key]); }
        static Dictionary<string, object> Json(string path) { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)); }
        static void VerifySource(string root, bool iso)
        {
            KeyboardLayout layout = iso ? KeyboardLayout.Tk75Iso() : KeyboardLayout.Tk75Ansi();
            string variant = iso ? "iso" : "ansi";
            Dictionary<string, object> evidence = Json(Path.Combine(root, "tests", "fixtures", "tk75-layout-" + variant + ".json"));
            Dictionary<string, object> matrix = Json(Path.Combine(root, "tests", "fixtures", "tk75-matrix-" + variant + ".json"));
            object[] bytes = (object[])matrix["bytes"];
            Check(bytes.Length == 512, "Manufacturer matrix length");
            object[] expected = (object[])evidence["keys"];
            Check(layout.Keys.Count == expected.Length, "Every manufacturer key visible before input");
            Check(layout.Keys.Count == (iso ? 85 : 84), "81/82 keys plus three knob segments");
            Check(layout.Size.Width == Number(evidence, "width") && layout.Size.Height == Number(evidence, "height"), "Manufacturer dimensions");
            HashSet<int> indices = new HashSet<int>(); int verified = 0;
            foreach (object item in expected)
            {
                IDictionary<string, object> source = (IDictionary<string, object>)item;
                string code = Text(source, "code"); int index = Number(source, "index"), usage = Number(source, "usage");
                KeyboardKeyDefinition key = layout.FindByCode(code);
                Check(key != null && key.KeyIndex == index, "Matrix slot " + code);
                Check(ReferenceEquals(key, layout.FindByIndex(index)), "Index lookup " + code);
                Check(indices.Add(index), "Unique matrix identity " + code);
                Check(key.Bounds == new RectangleF(Number(source, "x"), Number(source, "y"), Number(source, "width"), Number(source, "height")), "Manufacturer geometry " + code);
                Check(key.IsKnob == (Text(source, "kind") == "knob"), "Knob/key kind " + code);
                int b0 = Convert.ToInt32(bytes[index * 4]), b1 = Convert.ToInt32(bytes[index * 4 + 1]), b2 = Convert.ToInt32(bytes[index * 4 + 2]), b3 = Convert.ToInt32(bytes[index * 4 + 3]);
                if (usage >= 0) Check(b0 == 0 && b1 == 0 && b2 == usage && b3 == 0, "Raw manufacturer usage " + code);
                else if (code == "Fn") Check(b0 == 10 && b1 == 1 && b2 == 0 && b3 == 0, "Raw Fn special command");
                else Check(b0 == 3 && b1 == 0 && b2 == (code == "AudioVolumeDown" ? 234 : code == "AudioVolumeUp" ? 233 : 205) && b3 == 0, "Raw knob command " + code);
                if (key.VerifiedOnHardware) { verified++; Check(code == "KeyW" || code == "KeyA" || code == "KeyS" || code == "KeyD", "No unsupported physical verification claim"); }
            }
            Check(verified == 4, "Only WASD physically confirmed");
            Check(layout.FindByCode("KeyW").KeyIndex == 14 && layout.FindByCode("KeyA").KeyIndex == 9 && layout.FindByCode("KeyS").KeyIndex == 15 && layout.FindByCode("KeyD").KeyIndex == 21, "Physical WASD anchor");
            Check(layout.FindByCode("Enter").KeyIndex == (iso ? 80 : 81), "ISO and ANSI Enter differ");
            Check(iso ? layout.FindByCode("IntlRo").KeyIndex == 81 && layout.FindByCode("IntlBackslash").KeyIndex == 10 : layout.FindByCode("Backslash").KeyIndex == 80 && layout.FindByIndex(10) == null, "Distinct ISO/ANSI extra positions");
            Check(layout.FindByCode("KeyY").GetLegend(KeyboardLegendStyle.Qwertz) == "Z" && layout.FindByCode("KeyZ").GetLegend(KeyboardLegendStyle.Qwertz) == "Y", "German letter legends");
            Check(layout.FindByCode("KeyY").GetLegend(KeyboardLegendStyle.Qwerty) == "Y" && layout.FindByCode("KeyZ").GetLegend(KeyboardLegendStyle.Qwerty) == "Z", "English letter legends");
        }
        sealed class TestKeyboard : VisualKeyboard
        {
            public void ClickAt(Point point) { OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); }
            public void Key(Keys key) { OnKeyDown(new KeyEventArgs(key)); }
        }
        static Point Center(RectangleF bounds) { return new Point((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2)); }
        static void InteractionAndRender(string output)
        {
            using (Form form = new Form())
            using (TestKeyboard keyboard = new TestKeyboard())
            {
                form.AutoScaleMode = AutoScaleMode.None; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false; form.Controls.Add(keyboard);
                keyboard.Dock = DockStyle.Fill; keyboard.LayoutModel = KeyboardLayout.Tk75Iso(); keyboard.LegendStyle = KeyboardLegendStyle.Qwertz;
                form.ClientSize = new Size(940, 380); form.Show(); Application.DoEvents();
                int changes = 0; keyboard.SelectionChanged += delegate { changes++; };
                KeyboardKeyDefinition w = keyboard.LayoutModel.FindByIndex(14), a = keyboard.LayoutModel.FindByIndex(9);
                keyboard.ClickAt(Center(keyboard.GetKeyBounds(w)));
                Check(keyboard.SelectedKeyIndices.Length == 1 && keyboard.SelectedKeyIndices[0] == 14, "Mouse click selects W without reports/connection");
                keyboard.SelectKey(a, true); Check(keyboard.SelectedKeyIndices.Length == 2, "Ctrl toggle adds A");
                keyboard.SelectKey(w, true); Check(keyboard.SelectedKeyIndices.Length == 1 && keyboard.SelectedKeyIndices[0] == 9, "Ctrl toggle removes W");
                int before = changes; keyboard.SetSelectedKeys(new[] { 9, 9 }); Check(changes == before, "Same selection not republished");
                int[] external = keyboard.SelectedKeyIndices; external[0] = 255; Check(keyboard.SelectedKeyIndices[0] == 9, "Selection snapshot detached");
                Throws(delegate { keyboard.SetSelectedKeys(new[] { 14, 256 }); }, "Out-of-range selection rejected");
                Check(keyboard.SelectedKeyIndices[0] == 9, "Invalid selection is atomic");
                keyboard.SetSelectedKeys(new[] { 255 }); Check(keyboard.SelectedKeyIndices.Length == 0, "Unknown index is not assigned a physical key");
                Check(keyboard.HitTest(Point.Empty) == null, "Background is not a key");
                KeyboardKeyDefinition enter = keyboard.LayoutModel.FindByCode("Enter"); RectangleF er = keyboard.GetKeyBounds(enter);
                Check(keyboard.HitTest(new Point((int)(er.Left + er.Width * .1), (int)(er.Top + er.Height * .8))) == null, "ISO return notch is not clickable");
                keyboard.SelectKey(w, false); keyboard.Key(Keys.Left); Check(keyboard.SelectedKeyIndices.Length == 1 && keyboard.SelectedKeyIndices[0] == 8, "Arrow navigation W to Q");
                keyboard.Key(Keys.Control | Keys.Right); Check(keyboard.SelectedKeyIndices[0] == 8, "Ctrl arrow moves focus without removing selection");
                keyboard.Key(Keys.Control | Keys.Space); Check(keyboard.SelectedKeyIndices.Length == 2, "Ctrl space adds focused key");
                KeyboardLayout original = keyboard.LayoutModel;
                keyboard.LegendStyle = KeyboardLegendStyle.Qwerty;
                Check(ReferenceEquals(original, keyboard.LayoutModel) && keyboard.LayoutModel.FindByCode("KeyY").KeyIndex == 38, "Legend switch cannot remap hardware");
                keyboard.LegendStyle = KeyboardLegendStyle.Qwertz;
                keyboard.SetKeyLabel(14, "Vorwärts"); Check(keyboard.GetKeyLabel(w) == "Vorwärts", "Learned/custom label supported");
                keyboard.SetKeyLabel(14, null); Check(keyboard.GetKeyLabel(w) == "W", "Custom label can be cleared");
                Throws(delegate { keyboard.UpdateKeyState(14, false, false, Double.NaN); }, "Nonfinite depth not rendered");
                Throws(delegate { keyboard.UpdateKeyState(14, false, false, 1.01); }, "Unnormalized depth not silently treated as calibrated");
                keyboard.SetSelectedKeys(new[] { 14 }); keyboard.SetMappedKeys(new[] { 14, 9, 15, 21 });
                keyboard.UpdateKeyState(14, true, true, .65); keyboard.UpdateKeyState(9, true, false, null);
                keyboard.UpdateKeyState(15, true, false, 0); keyboard.UpdateKeyState(21, true, true, .2);
                Check(keyboard.AccessibilityObject.GetChildCount() == 85, "Every physical key has accessible child");
                AccessibleObject child = null;
                for (int i = 0; i < keyboard.AccessibilityObject.GetChildCount(); i++)
                { AccessibleObject candidate = keyboard.AccessibilityObject.GetChild(i); if (candidate.Name == "A") child = candidate; }
                Check(child != null && child.Role == AccessibleRole.PushButton && child.DefaultAction == "Select", "Accessible physical key identity/action");
                child.DoDefaultAction(); Check(keyboard.SelectedKeyIndices.Length == 1 && keyboard.SelectedKeyIndices[0] == 9, "Accessibility activates same physical key");
                keyboard.SetSelectedKeys(new[] { 14 });
                foreach (Size size in new[] { new Size(940, 380), new Size(680, 300) })
                {
                    form.ClientSize = size; Application.DoEvents();
                    foreach (KeyboardKeyDefinition key in keyboard.LayoutModel.Keys)
                    {
                        RectangleF bounds = keyboard.GetKeyBounds(key);
                        Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= keyboard.Width && bounds.Bottom <= keyboard.Height, "Responsive key bounds " + key.Code);
                        Check(ReferenceEquals(keyboard.HitTest(Center(bounds)), key), "Responsive hit identity " + key.Code);
                    }
                    using (Bitmap image = new Bitmap(keyboard.Width, keyboard.Height))
                    { keyboard.DrawToBitmap(image, new Rectangle(Point.Empty, keyboard.Size)); image.Save(Path.Combine(output, "keyboard-iso-" + size.Width + ".png")); }
                }
                keyboard.LayoutModel = original.WithKeyIndex("KeyW", null, false);
                int requests = 0; keyboard.UnassignedKeySelected += delegate(KeyboardKeyDefinition key) { if (key.Code == "KeyW") requests++; };
                keyboard.ClickAt(Center(keyboard.GetKeyBounds(keyboard.LayoutModel.FindByCode("KeyW"))));
                Check(requests == 1 && keyboard.SelectedKeyIndices.Length == 0, "Unassigned physical key requests learning without invented index");
                Check(original.FindByCode("KeyW").KeyIndex == 14, "Learning layout copy leaves source layout immutable");
                Throws(delegate { original.WithKeyIndex("KeyW", 9, true); }, "Duplicate learned index rejected");
                keyboard.LayoutModel = null; Check(keyboard.AccessibilityObject.GetChildCount() == 0 && keyboard.HitTest(new Point(50, 50)) == null, "Unknown layout does not show TK75 identity");
                form.Close();
            }
        }
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("Expected workspace and artifact directory.");
                UiText.SetLanguage("en");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                VerifySource(args[0], false); VerifySource(args[0], true); InteractionAndRender(args[1]);
                Console.WriteLine("PASS VisualKeyboard: " + assertions + " assertions; manufacturer matrices, geometry, selection, accessibility and offline rendering."); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
