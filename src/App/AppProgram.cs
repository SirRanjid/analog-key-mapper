using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Tk75.App
{
    internal static class AppProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--output-host") return Tk75.Output.OutputHost.Run(args);
            if (args.Length == 2 && args[0] == "--check-controller")
            { try { return Tk75.Output.ControllerProbe.Run(Path.GetFullPath(args[1])); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; } }
            if (args.Length == 2 && args[0] == "--check-controller-viiper")
            { try { return Tk75.Output.ControllerProbe.RunViiper(Path.GetFullPath(args[1])); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; } }
            if (args.Length == 2 && args[0] == "--check-controller-xbox-runtime")
            { try { return Tk75.Output.ControllerProbe.RunXboxRuntime(Path.GetFullPath(args[1])); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; } }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                UiText.SetLanguage(UiPreferences.LoadLanguage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")));
                if (args.Length == 2 && args[0] == "--render-preview")
                {
                    string output = Path.GetFullPath(args[1]); string previewData = Path.Combine(Path.GetDirectoryName(output), "preview-data");
                    using (var form = new MainForm(previewData, true))
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                        form.Show(); form.PerformLayout(); Application.DoEvents();
                        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png); }
                    }
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException(UiText.Get("Unbekannte Startoption.", "Unknown startup option."));
                using (var instance = SingleInstanceWindow.Acquire(AppDomain.CurrentDomain.BaseDirectory))
                {
                    if (!instance.IsOwner) { instance.BringExistingToFront(); return 0; }
                    using (var navigation = new MouseNavigationFilter())
                    using (var form = new MainForm(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"), false)) Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0 && args[0] == "--render-preview") Console.Error.WriteLine(ex.ToString());
                else MessageBox.Show(UiText.Get(ex.Message), UiText.Get("Analog Key Mapper konnte nicht starten", "Analog Key Mapper could not start"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
