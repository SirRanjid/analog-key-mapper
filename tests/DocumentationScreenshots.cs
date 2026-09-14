using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Tk75.App;

namespace Tk75.Tests
{
    // Official assets come directly from the application's shared icon renderer.
    public static class DocumentationScreenshots
    {
        internal static void ExportBrand(string output)
        {
            foreach (int size in new[] { 32, 64, 128, 256 })
                using (Bitmap logo = AppStatusIcon.Render(size, null))
                    logo.Save(Path.Combine(output, "analog-key-mapper-logo-" + size + ".png"), ImageFormat.Png);
            File.Copy(Path.Combine(output, "analog-key-mapper-logo-256.png"), Path.Combine(output, "logo.png"), true);
            File.WriteAllBytes(Path.Combine(output, "AnalogKeyMapper.ico"), AppStatusIcon.CreateIcoData(null));
            TrayStatus[] states = { TrayStatus.Starting, TrayStatus.Disconnected, TrayStatus.KeyboardMode, TrayStatus.ControllerActive, TrayStatus.Attention };
            string[] names = { "Connecting", "Controllers off", "Keyboard mode", "Controller active", "Action needed" };
            using (var sheet = new Bitmap(900, 168, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(sheet))
            using (var text = new SolidBrush(Color.FromArgb(228, 231, 240)))
            using (var font = new Font("Segoe UI", 11))
            using (var centered = new StringFormat { Alignment = StringAlignment.Center })
            {
                graphics.Clear(Color.FromArgb(20, 22, 28));
                for (int i = 0; i < states.Length; i++)
                {
                    using (Bitmap icon = AppStatusIcon.Render(64, states[i]))
                    {
                        icon.Save(Path.Combine(output, "tray-" + states[i].ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
                        graphics.DrawImageUnscaled(icon, i * 180 + 58, 25);
                    }
                    graphics.DrawString(names[i], font, text, new RectangleF(i * 180, 111, 180, 35), centered);
                }
                sheet.Save(Path.Combine(output, "tray-statuses.png"), ImageFormat.Png);
                sheet.Save(Path.Combine(output, "tray-status.png"), ImageFormat.Png);
            }
        }
    }
}
