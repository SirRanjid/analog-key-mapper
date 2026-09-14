using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Tk75.App
{
    // The same keycap/pressure-curve artwork supplies the executable, window and
    // tray. Icons are created on state changes and owned/disposed by the caller.
    internal static class AppStatusIcon
    {
        internal static Icon CreateApplication() { return CreateIcon(null, new Size(32, 32)); }
        internal static Icon Create(TrayStatus status) { return CreateIcon(status, SystemInformation.SmallIconSize); }

        static Icon CreateIcon(TrayStatus? status, Size size)
        {
            using (var stream = new MemoryStream(CreateIcoData(status)))
            using (var icon = new Icon(stream, size)) return (Icon)icon.Clone();
        }

        internal static byte[] CreateIcoData(TrayStatus? status)
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (Bitmap bitmap = Render(sizes[i], status))
                using (var png = new MemoryStream()) { bitmap.Save(png, ImageFormat.Png); images[i] = png.ToArray(); }
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
                }
                foreach (byte[] png in images) writer.Write(png);
                writer.Flush(); return buffer.ToArray();
            }
        }

        static GraphicsPath Round(float x, float y, float w, float h, float radius)
        {
            var path = new GraphicsPath(); float d = 2 * radius;
            path.AddArc(x, y, d, d, 180, 90); path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90); path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }

        internal static Bitmap Render(int size, TrayStatus? status)
        {
            if (size < 16 || size > 256) throw new ArgumentOutOfRangeException("size");
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 16f, size / 16f);
                bool muted = status == TrayStatus.Disconnected;
                Color edge = muted ? Color.FromArgb(180, 188, 204) : Color.FromArgb(207, 196, 255);
                using (var body = Round(1, 2.5f, 13.5f, 12, 2.5f))
                using (var fill = new SolidBrush(Color.FromArgb(70, 61, 107))) g.FillPath(fill, body);
                using (var face = Round(1, 1, 13.5f, 12, 2.5f))
                using (var fill = new SolidBrush(muted ? Color.FromArgb(44, 49, 60) : Color.FromArgb(44, 36, 72)))
                using (var outline = new Pen(edge, 1.15f)) { g.FillPath(fill, face); g.DrawPath(outline, face); }
                // A continuous rising response curve is recognizable even at
                // 16px and distinguishes this keycap from a letter-key icon.
                using (var curve = new GraphicsPath())
                using (var stroke = new Pen(edge, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    curve.AddBezier(4, 9.6f, 7.6f, 9.6f, 6.8f, 4.6f, 10.7f, 4.6f);
                    g.DrawPath(stroke, curve);
                }
                if (status.HasValue) DrawBadge(g, status.Value);
            }
            return bitmap;
        }

        static void DrawBadge(Graphics g, TrayStatus status)
        {
            Color color;
            switch (status)
            {
                case TrayStatus.Starting: color = Color.FromArgb(120, 185, 255); break;
                case TrayStatus.Disconnected: color = Color.FromArgb(174, 184, 203); break;
                case TrayStatus.KeyboardMode: color = Color.FromArgb(248, 198, 94); break;
                case TrayStatus.ControllerActive: color = Color.FromArgb(107, 231, 161); break;
                default: color = Color.FromArgb(255, 138, 151); break;
            }
            using (var fill = new SolidBrush(color))
            {
                if (status == TrayStatus.Attention)
                {
                    // Fill over the inside half of the stroke, leaving the same
                    // 0.75-unit outside border as the circular badges. Rounded
                    // joins keep the triangle's corners inside the icon bounds.
                    using (var triangle = new GraphicsPath())
                    using (var border = new Pen(Color.FromArgb(20, 22, 28), 1.5f) { LineJoin = LineJoin.Round })
                    {
                        triangle.AddPolygon(new[] { new PointF(12.3f, 9.2f), new PointF(15.2f, 14.95f), new PointF(9.4f, 14.95f) });
                        g.DrawPath(border, triangle);
                        g.FillPath(fill, triangle);
                    }
                }
                else
                {
                    using (var border = new SolidBrush(Color.FromArgb(20, 22, 28))) g.FillEllipse(border, 8.7f, 8.7f, 7.3f, 7.3f);
                    g.FillEllipse(fill, 9.45f, 9.45f, 5.8f, 5.8f);
                }
            }
            using (var mark = new Pen(Color.FromArgb(29, 34, 42), 1.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (status)
                {
                    case TrayStatus.ControllerActive:
                        g.DrawLines(mark, new[] { new PointF(10.6f, 12.3f), new PointF(11.7f, 13.4f), new PointF(14, 11.2f) }); break;
                    case TrayStatus.KeyboardMode:
                        g.DrawLine(mark, 11.3f, 11.1f, 11.3f, 13.5f); g.DrawLine(mark, 13.3f, 11.1f, 13.3f, 13.5f); break;
                    case TrayStatus.Disconnected:
                        g.DrawLine(mark, 10.8f, 12.3f, 13.8f, 12.3f); break;
                    case TrayStatus.Starting:
                        g.DrawArc(mark, 10.7f, 10.7f, 3.2f, 3.2f, 20, 265); break;
                    default:
                        g.DrawLine(mark, 12.3f, 11.35f, 12.3f, 12.75f);
                        g.DrawLine(mark, 12.3f, 14.05f, 12.3f, 14.1f); break;
                }
            }
        }
    }
}
