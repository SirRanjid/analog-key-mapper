using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    // Documentation only: real UI, fresh synthetic data, no device discovery or output.
    public static class DocumentationScreenshots
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(MainForm form, string name)
        { return (T)typeof(MainForm).GetField(name, Private).GetValue(form); }
        static void Call(MainForm form, string name, params object[] args)
        { typeof(MainForm).GetMethod(name, Private).Invoke(form, args); }
        static void Pump(MainForm form)
        { form.PerformLayout(); Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static void Passive(MainForm form)
        {
            Require(Field<object>(form, "reader") == null, "A documentation preview must not create a reader.");
            var runtime = Field<MultiControllerSession>(form, "runtime");
            Require(!runtime.AnyEnabled && runtime.ActiveControllerIds.Length == 0, "A documentation preview must not enable outputs.");
            Require(!Field<Timer>(form, "uiTimer").Enabled, "A documentation preview must not poll.");
            Require(!Field<bool>(form, "hotkey") && !Field<bool>(form, "modeHotkey"), "A documentation preview must not register global hotkeys.");
        }
        static int Index(KeyboardLayout layout, string code)
        { return layout.FindByCode(code).KeyIndex.Value; }
        static void Select(MainForm form, params int[] indices)
        {
            var keys = Field<DataGridView>(form, "keys"); keys.ClearSelection();
            foreach (int index in indices) keys.Rows[index].Selected = true;
            Pump(form);
        }
        static void Capture(MainForm form, string output, string name)
        {
            Pump(form); Passive(form);
            // Export the application's client area without unrelated Windows chrome.
            Point clientOrigin = form.PointToScreen(Point.Empty);
            Point offset = new Point(clientOrigin.X - form.Left, clientOrigin.Y - form.Top);
            using (var window = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(window, new Rectangle(Point.Empty, window.Size));
                using (var client = window.Clone(new Rectangle(offset, form.ClientSize), PixelFormat.Format32bppArgb))
                    client.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
            Console.WriteLine(name + ".png: " + form.ClientSize.Width + " x " + form.ClientSize.Height);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            MainForm form = null;
            try
            {
                if (args.Length != 2) throw new ArgumentException("Pass a new synthetic workspace and image output directory.");
                string data = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
                Require(!Directory.Exists(data), "The screenshot workspace must be new, never a user's existing data.");
                Directory.CreateDirectory(output);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                form = new MainForm(data, true);
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                form.Show(); Pump(form); Passive(form);
                Require(UiText.Language == "en", "Documentation must use the default English UI.");
                Field<ComboBox>(form, "layoutMode").SelectedIndex = 1; // Actual manual ANSI selection.
                var keyboard = Field<VisualKeyboard>(form, "keyboard");
                KeyboardLayout layout = keyboard.LayoutModel;
                var profile = new Profile { Name = "Demo - analog movement" };
                string[] codes = { "KeyW", "KeyA", "KeyS", "KeyD", "Space", "ControlLeft", "ShiftLeft", "KeyQ", "KeyE", "KeyR", "KeyF", "KeyZ", "KeyC" };
                OutputTarget[] targets = { OutputTarget.LeftYPositive, OutputTarget.LeftXNegative, OutputTarget.LeftYNegative, OutputTarget.LeftXPositive,
                    OutputTarget.A, OutputTarget.B, OutputTarget.LeftThumb, OutputTarget.LB, OutputTarget.RB, OutputTarget.X, OutputTarget.Y, OutputTarget.LeftTrigger, OutputTarget.RightTrigger };
                for (int i = 0; i < codes.Length; i++)
                {
                    var binding = new Binding { KeyIndex = Index(layout, codes[i]), Target = targets[i] };
                    if (i < 4) binding.Processing = new SignalSettings { Curve = CurveKind.Exponential, Exponent = 2, TopDeadzone = .04, BottomDeadzone = .02 };
                    profile.Bindings.Add(binding);
                }
                profile = ControllerRouting.Rename(profile, ControllerRouting.DefaultControllerId, "Player 1");
                profile = ControllerRouting.SetRgbColor(profile, ControllerRouting.DefaultControllerId, 0xAC98F0);
                Call(form, "Commit", profile); Call(form, "SaveProfile"); Call(form, "ReloadProfiles");
                form.ClientSize = new Size(1440, 880); Pump(form);
                Select(form, Index(layout, "KeyW"));
                Call(form, "SetDetailMode", null, true, false); Capture(form, output, "mapping");
                Select(form, codes.Take(4).Select(code => Index(layout, code)).ToArray());
                Call(form, "SetDetailMode", "controller", true, false); Capture(form, output, "controller");
                Select(form, Index(layout, "KeyW")); Call(form, "SetDetailMode", "advanced", true, false);
                Field<ComboBox>(form, "preset").SelectedIndex = 1; // Show the matching Gentle preset in its picker.
                Field<ComboBox>(form, "pasteMode").SelectedIndex = 3; // The native Curve copy/paste option.
                Capture(form, output, "response-curve");
                Console.WriteLine("PASS: three real English UI screenshots; synthetic profile, no reader, output, polling or global hotkeys.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (form != null) form.Dispose(); }
        }
    }
}
