using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tk75.App
{
    // App-owned decoration only: no global hooks, screen capture or input.
    // WS_EX_TRANSPARENT + layered makes this window mouse-transparent even
    // over other applications; NOACTIVATE keeps focus on the drag source.
    public sealed class DragPreviewWindow : Form
    {
        // Borrowed for the lifetime of this window. The drag's outer using owns
        // the visual and disposes it only after the preview has been disposed.
        readonly MappingDragVisual dragVisual;
        readonly Point anchor;

        internal DragPreviewWindow(MappingDragVisual visual)
        {
            if (visual == null) throw new ArgumentNullException("visual");
            if (visual.Image == null) throw new ArgumentException("The drag visual has no image.", "visual");
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; ClientSize = visual.Image.Size;
            // Do not set Opacity or TransparencyKey: those use
            // SetLayeredWindowAttributes, which conflicts with per-pixel ULW.
            anchor = visual.Anchor; dragVisual = visual;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080 | 0x00080000;
                return value;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        public void FollowCursor(Point cursor)
        {
            if (IsDisposed) return;
            Point next = new Point(cursor.X - anchor.X, cursor.Y - anchor.Y);
            // A layered window retains its uploaded pixels when moved.
            // Keep the grabbed point fixed, even at a monitor edge; no
            // bitmap recreation/upload, screen clamp or hand offset here.
            if (Location != next) Location = next;
        }
        // The retained layered surface is the sole drawing path.
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (dragVisual != null) UploadDragVisual();
        }

        void UploadDragVisual()
        {
            Bitmap source = dragVisual.Image;
            if (source == null) throw new ObjectDisposedException("MappingDragVisual");
            // A single premultiplication is required by AC_SRC_ALPHA. The
            // source stays unchanged, and the conversion exists only during
            // this upload. Avoid the copy when the source already is PArgb.
            Bitmap converted = null;
            IntPtr memoryDc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
            bool selected = false;
            try
            {
                Bitmap pixels = source;
                if (source.PixelFormat != PixelFormat.Format32bppPArgb)
                {
                    converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImageUnscaled(source, 0, 0);
                    }
                    pixels = converted;
                }
                memoryDc = CreateCompatibleDC(IntPtr.Zero);
                if (memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new BitmapInfo { Size = 40, Width = pixels.Width, Height = -pixels.Height,
                    Planes = 1, BitCount = 32, SizeImage = checked((uint)(pixels.Width * pixels.Height * 4)) };
                IntPtr bits;
                dib = CreateDIBSection(memoryDc, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(memoryDc, dib);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                selected = true;
                BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int stride = checked(pixels.Width * 4);
                    var row = new byte[stride];
                    for (int y = 0; y < pixels.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(bits, y * stride), row.Length);
                    }
                }
                finally { pixels.UnlockBits(data); }
                var destination = new NativePoint { X = Left, Y = Top };
                var origin = new NativePoint();
                var size = new NativeSize { Width = pixels.Width, Height = pixels.Height };
                // Keep the drop target readable beneath either held image.
                var blend = new BlendFunction { SourceConstantAlpha = 128, AlphaFormat = 1 }; // AC_SRC_ALPHA, about 50% opacity
                if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, memoryDc,
                    ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error()); // ULW_ALPHA
            }
            finally
            {
                // Windows retains its own surface after UpdateLayeredWindow.
                // Never keep a DC or DIB alive across mouse movement, and never
                // delete a bitmap while it is still selected into a live DC.
                if (selected) SelectObject(memoryDc, previous);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (converted != null) converted.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeSize { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination,
            ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
    }
}
