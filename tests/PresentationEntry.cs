using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        [STAThread]
        public static int ExportPresentation(string[] args)
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("A fresh synthetic data directory and image directory are required.");
                string data = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
                if (Directory.Exists(data)) throw new InvalidOperationException("Synthetic data directory must not exist.");
                Directory.CreateDirectory(output);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var version = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                Console.WriteLine("Application source version: " + (version == null ? "unspecified" : version.InformationalVersion));
                DocumentationScreenshots.ExportBrand(output);
                using (var preview = new MainForm(data, true))
                {
                    preview.ShowInTaskbar = false; preview.StartPosition = FormStartPosition.Manual;
                    preview.Location = new Point(-30000, -30000); preview.MaximumSize = new Size(2048, 2048);
                    preview.Show(); Pump(preview); AssertPassive(preview);
                    RunPresentationUi(preview, output);
                    AssertPassive(preview);
                }
                foreach (string name in new[] { "controller", "mapping", "response-curve", "key-behavior", "socd-opposite-drop", "input-learning" })
                    File.Copy(Path.Combine(output, "presentation-" + name + ".png"), Path.Combine(output, name + ".png"), true);
                UiText.SetLanguage("en");
                using (var prompt = new RgbStartupConfirmationDialog("F9   #FFC65C → #223344\r\nW   #79C8AF → #223344\r\nA   #79C8AF → #223344\r\nS   #79C8AF → #223344\r\nD   #79C8AF → #223344"))
                {
                    prompt.ShowInTaskbar = false; prompt.StartPosition = FormStartPosition.Manual;
                    prompt.Location = new Point(-30000, -30000); prompt.Show();
                    SavePresentation(prompt, output, "startup-lighting-confirmation");
                    File.Copy(Path.Combine(output, "presentation-startup-lighting-confirmation.png"), Path.Combine(output, "startup-lighting-confirmation.png"), true);
                    File.Copy(Path.Combine(output, "presentation-startup-lighting-confirmation.png"), Path.Combine(output, "startup-cleanup.png"), true);
                }
                Console.WriteLine("PASS: current application presentation, seven synthetic UI captures and official code-native logo exports; no hardware or controller output.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
    internal static class PresentationEntry
    {
        [STAThread]
        public static int Main(string[] args) { return AppUiHarness.ExportPresentation(args); }
    }
}
