using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tk75.App
{
    // NumericUpDown still owns its edit field, validation, acceleration, wheel,
    // keyboard and accessible up/down buttons. Only their paint is replaced.
    public class SleekNumericUpDown : NumericUpDown
    {
        public SleekNumericUpDown() { NativeSurfaceTheme.AttachNumber(this); }
    }

    internal static class NativeControlPaint
    {
        internal const int Paint = 0x000F, EraseBackground = 0x0014, Print = 0x0317, PrintClient = 0x0318;
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PaintInfo
        {
            public IntPtr Dc;
            public int Erase;
            public Rect Area;
            public int Restore, Update;
            public int Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ComboInfo
        {
            public int Size;
            public Rect Item, Button;
            public int ButtonState;
            public IntPtr Combo, Edit, List;
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr BeginPaint(IntPtr window, out PaintInfo paint);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool EndPaint(IntPtr window, ref PaintInfo paint);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool IsWindowVisible(IntPtr window);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern bool RestoreDC(IntPtr dc, int state);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern bool OffsetViewportOrgEx(IntPtr dc, int x, int y, IntPtr previous);

        internal static bool TryClientPaint(ref Message message, Size size, Action<Graphics> paint)
        {
            if (message.Msg == EraseBackground) { message.Result = (IntPtr)1; return true; }
            if (message.Msg != Paint && message.Msg != PrintClient && message.Msg != Print) return false;
            if (size.Width < 1 || size.Height < 1) return false;
            PaintInfo info = new PaintInfo();
            bool begin = message.Msg == Paint;
            IntPtr dc = begin ? BeginPaint(message.HWnd, out info) : message.WParam;
            try
            {
                if (dc != IntPtr.Zero)
                    using (Graphics target = Graphics.FromHdc(dc))
                    using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(target, new Rectangle(Point.Empty, size)))
                    { paint(buffer.Graphics); buffer.Render(target); }
            }
            finally { if (begin) EndPaint(message.HWnd, ref info); }
            if (message.Msg == Print && (message.LParam.ToInt64() & 16) != 0 && dc != IntPtr.Zero)
                PrintChildren(message.HWnd, dc);
            message.Result = IntPtr.Zero; return true;
        }

        static void PrintChildren(IntPtr parent, IntPtr dc)
        {
            // WM_PRINT is also used by DrawToBitmap. Include the existing native
            // edit child; it must not disappear just because the parent owns paint.
            Point origin = Point.Empty; ClientToScreen(parent, ref origin);
            for (IntPtr child = GetWindow(parent, 5); child != IntPtr.Zero; child = GetWindow(child, 2))
            {
                Rect bounds;
                if (!IsWindowVisible(child) || !GetWindowRect(child, out bounds)) continue;
                int state = SaveDC(dc);
                try
                {
                    OffsetViewportOrgEx(dc, bounds.Left - origin.X, bounds.Top - origin.Y, IntPtr.Zero);
                    SendMessage(child, Print, dc, (IntPtr)30);
                }
                finally { RestoreDC(dc, state); }
            }
        }

        internal static void Chevron(Graphics graphics, Rectangle area, bool vertical, bool forward, Color color)
        {
            if (area.Width < 3 || area.Height < 3) return;
            float x = area.Left + area.Width / 2f, y = area.Top + area.Height / 2f;
            float length = Math.Max(2, Math.Min(area.Width, area.Height) * .18f);
            float direction = forward ? 1 : -1;
            PointF[] points = vertical
                ? new[] { new PointF(x - length, y - direction * length / 2), new PointF(x, y + direction * length / 2), new PointF(x + length, y - direction * length / 2) }
                : new[] { new PointF(x - direction * length / 2, y - length), new PointF(x + direction * length / 2, y), new PointF(x - direction * length / 2, y + length) };
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, Math.Max(1, length * .4f)))
            { pen.StartCap = pen.EndCap = LineCap.Round; graphics.DrawLines(pen, points); }
        }
    }

    internal static class NativeSurfaceTheme
    {
        static readonly ConditionalWeakTable<Control, NumberSurface> Numbers = new ConditionalWeakTable<Control, NumberSurface>();
        static readonly ConditionalWeakTable<Control, ScrollSurface> Scrolls = new ConditionalWeakTable<Control, ScrollSurface>();

        internal static void AttachNumber(NumericUpDown control)
        { Numbers.GetValue(control, delegate(Control owner) { return new NumberSurface((NumericUpDown)owner); }); }

        internal static void AttachScrollbars(Control control)
        {
            ScrollableControl panel = control as ScrollableControl;
            TextBoxBase text = control as TextBoxBase;
            if (!(control is ScrollBar) && !(control is ListBox) && !(text != null && text.Multiline) && !(panel != null && panel.AutoScroll)) return;
            Scrolls.GetValue(control, delegate(Control owner) { return new ScrollSurface(owner); });
        }

        // Each attachment follows one app-owned control through handle recreation.
        // There are no global hooks, changed system colors, timers or hidden input
        // controls. Every non-paint message reaches the original native procedure.
        abstract class ControlSurface : NativeWindow, IDisposable
        {
            protected readonly Control Owner;
            protected ControlSurface(Control owner)
            {
                Owner = owner;
                owner.HandleCreated += Created; owner.HandleDestroyed += Destroyed; owner.Disposed += Disposed;
                if (owner.IsHandleCreated) AssignHandle(owner.Handle);
            }
            void Created(object sender, EventArgs e) { AssignHandle(Owner.Handle); }
            void Destroyed(object sender, EventArgs e) { ReleaseHandle(); }
            void Disposed(object sender, EventArgs e) { Dispose(); }
            public virtual void Dispose()
            {
                Owner.HandleCreated -= Created; Owner.HandleDestroyed -= Destroyed; Owner.Disposed -= Disposed;
                ReleaseHandle();
            }
        }

        sealed class NumberSurface : ControlSurface
        {
            readonly NumericUpDown number;
            ButtonSurface buttons;
            public NumberSurface(NumericUpDown owner) : base(owner)
            {
                number = owner;
                owner.ControlAdded += ChildAdded;
                owner.Enter += FocusChanged; owner.Leave += FocusChanged;
                foreach (Control child in owner.Controls) AttachChild(child);
            }
            void FocusChanged(object sender, EventArgs e) { Owner.Invalidate(); }
            void ChildAdded(object sender, ControlEventArgs e) { AttachChild(e.Control); }
            void AttachChild(Control child)
            {
                if (child is TextBoxBase || buttons != null) return;
                buttons = new ButtonSurface(child);
            }
            protected override void WndProc(ref Message message)
            {
                if (NativeControlPaint.TryClientPaint(ref message, Owner.ClientSize, PaintNumber)) return;
                base.WndProc(ref message);
            }
            void PaintNumber(Graphics graphics)
            {
                graphics.Clear(number.BackColor);
                if (number.BorderStyle == BorderStyle.None || number.Width < 3 || number.Height < 3) return;
                using (Pen pen = new Pen(number.ContainsFocus && number.Enabled ? ModernTheme.Accent : ModernTheme.Border))
                    graphics.DrawRectangle(pen, 0, 0, number.ClientSize.Width - 1, number.ClientSize.Height - 1);
            }
            public override void Dispose()
            {
                number.ControlAdded -= ChildAdded; number.Enter -= FocusChanged; number.Leave -= FocusChanged;
                if (buttons != null) { buttons.Dispose(); buttons = null; }
                base.Dispose();
            }
        }

        sealed class ButtonSurface : ControlSurface
        {
            Point mouse = new Point(-1, -1);
            bool pressed;
            public ButtonSurface(Control owner) : base(owner) { }
            protected override void WndProc(ref Message message)
            {
                if (NativeControlPaint.TryClientPaint(ref message, Owner.ClientSize, PaintButtons)) return;
                int kind = message.Msg;
                if (kind == 0x0200 || kind == 0x0201)
                { long point = message.LParam.ToInt64(); mouse = new Point((short)(point & 65535), (short)((point >> 16) & 65535)); }
                if (kind == 0x0201) pressed = true;
                if (kind == 0x0202 || kind == 0x0215) pressed = false;
                if (kind == 0x02A3) { mouse = new Point(-1, -1); pressed = false; }
                base.WndProc(ref message);
                if (kind == 0x0200 || kind == 0x0201 || kind == 0x0202 || kind == 0x0215 || kind == 0x02A3 || kind == 0x000A) Owner.Invalidate();
            }
            void PaintButtons(Graphics graphics)
            {
                graphics.Clear(ModernTheme.SurfaceAlt);
                int half = Owner.ClientSize.Height / 2;
                Rectangle up = new Rectangle(0, 0, Owner.ClientSize.Width, half);
                Rectangle down = new Rectangle(0, half, Owner.ClientSize.Width, Owner.ClientSize.Height - half);
                Rectangle hot = up.Contains(mouse) ? up : down.Contains(mouse) ? down : Rectangle.Empty;
                if (Owner.Enabled && !hot.IsEmpty)
                    using (Brush brush = new SolidBrush(pressed ? ModernTheme.AccentSoft : SurfaceDrawing.Blend(ModernTheme.SurfaceAlt, ModernTheme.Foreground, .06f))) graphics.FillRectangle(brush, hot);
                using (Pen border = new Pen(ModernTheme.Border)) graphics.DrawLine(border, 2, half, Owner.Width - 3, half);
                Color color = Owner.Enabled ? ModernTheme.Accent : ModernTheme.Muted;
                NativeControlPaint.Chevron(graphics, up, true, false, color);
                NativeControlPaint.Chevron(graphics, down, true, true, color);
            }
        }

        sealed class ScrollSurface : ControlSurface
        {
            readonly bool standalone, vertical;
            bool framePending;
            public ScrollSurface(Control owner) : base(owner)
            { standalone = owner is ScrollBar; vertical = owner is VScrollBar; }
            protected override void WndProc(ref Message message)
            {
                if (standalone && NativeControlPaint.TryClientPaint(ref message, Owner.ClientSize, PaintStandalone)) return;
                if (!standalone && message.Msg == 0x0085)
                { framePending = false; ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
                if (!standalone && (message.Msg == NativeControlPaint.Print || message.Msg == NativeControlPaint.PrintClient))
                { if (message.Msg == NativeControlPaint.Print) ScrollbarDrawing.PaintFrame(Handle, message.WParam); return; }
                if (ScrollbarDrawing.ChangesScrollAppearance(message.Msg))
                {
                    if (standalone) Owner.Invalidate();
                    else if (!framePending) framePending = ScrollbarDrawing.InvalidateFrame(Handle);
                }
            }
            void PaintStandalone(Graphics graphics)
            {
                // WinForms can consume WM_SIZE before the system SCROLLBAR
                // procedure sees it. Let its original paint calculate the real
                // arrow/thumb geometry inside our existing offscreen buffer.
                // The dark pass then reads that geometry; native white pixels
                // are never presented and native input remains untouched.
                IntPtr dc = graphics.GetHdc();
                try
                {
                    Message nativePaint = Message.Create(Handle, NativeControlPaint.PrintClient, dc, (IntPtr)12);
                    base.WndProc(ref nativePaint);
                }
                finally { graphics.ReleaseHdc(dc); }
                ScrollbarDrawing.Paint(graphics, Handle, 0xFFFFFFFC, vertical, Point.Empty);
            }
        }

        // The drop-down list belongs to its ComboBox, but is a separate HWND and
        // is not in Control.Controls. Attach only the handle GetComboBoxInfo gave
        // us; destroying/recreating the ComboBox detaches this object explicitly.
        internal sealed class ComboListSurface : NativeWindow, IDisposable
        {
            bool framePending;
            public void Attach(IntPtr handle)
            {
                if (Handle == handle) return;
                ReleaseHandle(); framePending = false; if (handle != IntPtr.Zero) AssignHandle(handle);
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0085)
                { framePending = false; ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
                if (message.Msg == NativeControlPaint.Print) ScrollbarDrawing.PaintFrame(Handle, message.WParam);
                else if (!framePending && ScrollbarDrawing.ChangesScrollAppearance(message.Msg)) framePending = ScrollbarDrawing.InvalidateFrame(Handle);
            }
            public void Dispose() { ReleaseHandle(); }
        }
    }

    internal static class ScrollbarDrawing
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ScrollInfo
        {
            public int Size;
            public NativeControlPaint.Rect Area;
            public int ArrowSize, ThumbStart, ThumbEnd, Reserved;
            public uint State, FirstArrow, FirstPage, Thumb, LastPage, LastArrow;
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool GetScrollBarInfo(IntPtr window, uint objectId, ref ScrollInfo info);

        internal static bool ChangesScrollAppearance(int message)
        {
            return message == 0x0005 || message == 0x000A || message == 0x0047 || message == 0x00A0 ||
                message == 0x00A1 || message == 0x00A2 || message == 0x0114 || message == 0x0115 ||
                message == 0x0200 || message == 0x0201 || message == 0x0202 || message == 0x020A ||
                message == 0x020E || message == 0x0215 || message == 0x02A2 || message == 0x02A3 ||
                message == 0x00E0 || message == 0x00E2 || message == 0x00E9;
        }
        internal static bool InvalidateFrame(IntPtr window)
        {
            // Queue at most one frame-only paint after native scroll processing.
            // Invalidating the client would repaint lists/live values on hover.
            return window != IntPtr.Zero && NativeControlPaint.PostMessage(window, 0x0085, (IntPtr)1, IntPtr.Zero);
        }

        internal static bool Read(IntPtr window, uint objectId, out ScrollInfo info)
        {
            info = new ScrollInfo(); info.Size = Marshal.SizeOf(typeof(ScrollInfo));
            return GetScrollBarInfo(window, objectId, ref info) && (info.State & 0x18000) == 0 && info.Area.Right > info.Area.Left && info.Area.Bottom > info.Area.Top;
        }

        internal static void PaintFrame(IntPtr window, IntPtr suppliedDc)
        {
            NativeControlPaint.Rect bounds, client;
            if (window == IntPtr.Zero || !NativeControlPaint.GetWindowRect(window, out bounds) || !NativeControlPaint.GetClientRect(window, out client)) return;
            Point origin = Point.Empty; NativeControlPaint.ClientToScreen(window, ref origin);
            Rectangle clientBounds = client.Bounds; clientBounds.Offset(origin.X - bounds.Left, origin.Y - bounds.Top);
            IntPtr dc = suppliedDc == IntPtr.Zero ? NativeControlPaint.GetWindowDC(window) : suppliedDc;
            if (dc == IntPtr.Zero) return;
            try
            {
                using (Graphics graphics = Graphics.FromHdc(dc))
                {
                    GraphicsState state = graphics.Save();
                    graphics.ExcludeClip(clientBounds);
                    using (Brush surface = new SolidBrush(ModernTheme.Border)) graphics.FillRectangle(surface, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                    Point offset = new Point(-bounds.Left, -bounds.Top);
                    Paint(graphics, window, 0xFFFFFFFB, true, offset);
                    Paint(graphics, window, 0xFFFFFFFA, false, offset);
                    graphics.Restore(state);
                }
            }
            finally { if (suppliedDc == IntPtr.Zero) NativeControlPaint.ReleaseDC(window, dc); }
        }

        // GetScrollBarInfo is the documented native geometry/state contract. The
        // thumb drawn here is the actual native thumb, including DPI and RTL;
        // no replacement range, scroll calculation or hit-test rectangle exists.
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-scrollbarinfo
        internal static void Paint(Graphics graphics, IntPtr window, uint objectId, bool vertical, Point offset)
        {
            ScrollInfo info;
            if (!Read(window, objectId, out info))
            { if (objectId == 0xFFFFFFFC) graphics.Clear(ModernTheme.SurfaceAlt); return; }
            if (objectId == 0xFFFFFFFC) offset = new Point(-info.Area.Left, -info.Area.Top);
            Rectangle area = info.Area.Bounds; area.Offset(offset);
            using (Brush track = new SolidBrush(ModernTheme.SurfaceAlt)) graphics.FillRectangle(track, area);
            int extent = vertical ? area.Height : area.Width;
            int arrow = Math.Max(0, Math.Min(info.ArrowSize, extent / 2));
            Rectangle first = vertical ? new Rectangle(area.Left, area.Top, area.Width, arrow) : new Rectangle(area.Left, area.Top, arrow, area.Height);
            Rectangle last = vertical ? new Rectangle(area.Left, area.Bottom - arrow, area.Width, arrow) : new Rectangle(area.Right - arrow, area.Top, arrow, area.Height);
            PaintArrow(graphics, first, vertical, false, info.State | info.FirstArrow);
            PaintArrow(graphics, last, vertical, true, info.State | info.LastArrow);
            if ((info.State & 1) != 0 || (info.Thumb & 0x18001) != 0 || info.ThumbEnd <= info.ThumbStart) return;
            int start = Math.Max(arrow, Math.Min(extent - arrow, info.ThumbStart));
            int end = Math.Max(start, Math.Min(extent - arrow, info.ThumbEnd));
            Rectangle thumb = vertical ? new Rectangle(area.Left + 3, area.Top + start + 2, Math.Max(1, area.Width - 6), Math.Max(1, end - start - 4))
                : new Rectangle(area.Left + start + 2, area.Top + 3, Math.Max(1, end - start - 4), Math.Max(1, area.Height - 6));
            if (end <= start) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = SurfaceDrawing.Round(thumb, Math.Min(thumb.Width, thumb.Height) / 2f))
            using (Brush fill = new SolidBrush((info.Thumb & 8) != 0 ? ModernTheme.AccentHover : SurfaceDrawing.Blend(ModernTheme.Border, ModernTheme.Accent, .45f))) graphics.FillPath(fill, path);
        }
        static void PaintArrow(Graphics graphics, Rectangle area, bool vertical, bool forward, uint state)
        {
            if ((state & 8) != 0) using (Brush fill = new SolidBrush(ModernTheme.AccentSoft)) graphics.FillRectangle(fill, area);
            NativeControlPaint.Chevron(graphics, area, vertical, forward, (state & 1) != 0 ? ModernTheme.Border : ModernTheme.Muted);
        }
    }
}
