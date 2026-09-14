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
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int SetWindowTheme(IntPtr window, string application, string classes);
        [DllImport("uxtheme.dll", ExactSpelling = true)]
        static extern int BufferedPaintStopAllAnimations(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
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

        internal static void OwnScrollbarPainting(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            // These surfaces are already completely drawn by the app. Leaving
            // the native theme renderer active also leaves its hover/fade paint
            // paths active. Disable that second painter on this owned HWND only;
            // keep native range, geometry and accessible controls. Mouse input
            // is owned centrally so the system cannot start a second painter.
            // The documented association lasts for this handle's lifetime.
            // https://learn.microsoft.com/windows/win32/api/uxtheme/nf-uxtheme-setwindowtheme
            SetWindowTheme(window, "", "");
            BufferedPaintStopAllAnimations(window);
        }

        internal static void RefreshLayout(Control root)
        {
            if (root == null || root.IsDisposed || !root.IsHandleCreated || !root.Visible) return;
            // Managed AutoScrollPosition / ScrollControlIntoView can draw a
            // native scrollbar directly without raising Layout or a scroll
            // message, even when the requested position has not changed.
            // Complete the whole affected view after that managed operation:
            // client-only Invalidate/Refresh would miss its non-client frame,
            // and a frame-only pass would keep recycled child-control pixels.
            // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN |
            // RDW_UPDATENOW; repaint through each control's usual paint owner.
            RefreshWindow(root.Handle);
        }

        internal static void RefreshWindow(IntPtr window)
        { if (window != IntPtr.Zero) RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080 | 0x0100); }

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
        static readonly ConditionalWeakTable<Control, ComboSurface> Combos = new ConditionalWeakTable<Control, ComboSurface>();

        internal static void RefreshLayout(Control root) { NativeControlPaint.RefreshLayout(root); }

        internal static void AttachCombo(ComboBox control)
        {
            if (control is SleekComboBox) return;
            Combos.GetValue(control, delegate(Control owner) { return new ComboSurface((ComboBox)owner); });
            control.DrawMode = DrawMode.OwnerDrawFixed;
            if (!(control is DataGridViewComboBoxEditingControl)) control.ItemHeight = Math.Max(24, control.Font.Height + 10);
        }

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
        // There are no global hooks, changed system colors or hidden input
        // controls. Scrollbars share one mouse owner; other input stays native.
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

        // DataGridView supplies and reuses a native ComboBox editing control.
        // Keep that control (including its editing/accessibility contract) and
        // apply the same drawing as every other picker, even after reuse.
        sealed class ComboSurface : ControlSurface
        {
            readonly ComboBox combo;
            readonly ComboListSurface list = new ComboListSurface();
            bool hovered;
            public ComboSurface(ComboBox owner) : base(owner)
            {
                combo = owner; combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += DrawItem;
                combo.HandleCreated += Created; combo.HandleDestroyed += Destroyed;
                combo.DropDown += DropDown; combo.DropDownClosed += Changed;
                combo.SelectedIndexChanged += Changed; combo.TextChanged += Changed;
                combo.GotFocus += Changed; combo.LostFocus += Changed; combo.EnabledChanged += Changed;
                combo.MouseEnter += Enter; combo.MouseLeave += Leave;
                AttachList();
            }
            void Created(object sender, EventArgs e) { AttachList(); }
            void Destroyed(object sender, EventArgs e) { list.Detach(); }
            void AttachList()
            {
                if (!combo.IsHandleCreated) return;
                NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo(); info.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                if (NativeControlPaint.GetComboBoxInfo(combo.Handle, ref info)) list.Attach(info.List);
            }
            void DropDown(object sender, EventArgs e) { AttachList(); combo.Invalidate(); }
            void Changed(object sender, EventArgs e) { combo.Invalidate(); }
            void Enter(object sender, EventArgs e) { hovered = true; combo.Invalidate(); }
            void Leave(object sender, EventArgs e) { hovered = false; combo.Invalidate(); }
            void DrawItem(object sender, DrawItemEventArgs e) { ComboSurfaceDrawing.Item(combo, e); }
            protected override void WndProc(ref Message message)
            {
                if (combo != null && combo.DropDownStyle != ComboBoxStyle.Simple &&
                    NativeControlPaint.TryClientPaint(ref message, combo.ClientSize, delegate(Graphics graphics) { ComboSurfaceDrawing.Face(combo, hovered, true, graphics); })) return;
                base.WndProc(ref message);
            }
            public override void Dispose()
            {
                combo.DrawItem -= DrawItem; combo.HandleCreated -= Created; combo.HandleDestroyed -= Destroyed;
                combo.DropDown -= DropDown; combo.DropDownClosed -= Changed;
                combo.SelectedIndexChanged -= Changed; combo.TextChanged -= Changed;
                combo.GotFocus -= Changed; combo.LostFocus -= Changed; combo.EnabledChanged -= Changed;
                combo.MouseEnter -= Enter; combo.MouseLeave -= Leave;
                list.Dispose(); base.Dispose();
            }
        }

        sealed class ScrollSurface : ControlSurface
        {
            readonly bool standalone, vertical;
            readonly ScrollbarInteraction interaction;
            public ScrollSurface(Control owner) : base(owner)
            {
                standalone = owner is ScrollBar; vertical = owner is VScrollBar;
                interaction = new ScrollbarInteraction(owner, standalone, vertical, Repaint);
                interaction.Attach(Handle);
                if (owner is ScrollableControl) owner.Layout += LayoutChanged;
                owner.HandleCreated += ScrollHandleCreated;
                owner.HandleDestroyed += ScrollHandleDestroyed;
                owner.VisibleChanged += VisibilityChanged;
                if (owner.IsHandleCreated) NativeControlPaint.OwnScrollbarPainting(owner.Handle);
            }
            void ScrollHandleCreated(object sender, EventArgs e)
            { interaction.Attach(Handle); NativeControlPaint.OwnScrollbarPainting(Handle); }
            void ScrollHandleDestroyed(object sender, EventArgs e) { interaction.Detach(); }
            void Repaint()
            {
                if (Handle == IntPtr.Zero || Owner.IsDisposed || !Owner.Visible) return;
                if (standalone) PaintStandaloneWindow(); else ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero);
            }
            void VisibilityChanged(object sender, EventArgs e)
            {
                if (!Owner.Visible) return;
                if (standalone) PaintStandaloneWindow();
                else ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero);
            }
            void LayoutChanged(object sender, LayoutEventArgs e)
            {
                // PerformLayout also runs directly from managed code, outside
                // any WM_SIZE/WM_WINDOWPOSCHANGED dispatch. ScrollableControl
                // synchronizes its native scrollbars before raising Layout;
                // SetScrollInfo(redraw:true) can overwrite our frame there.
                // Restore it synchronously at that edge, not on a queued paint.
                if (Handle != IntPtr.Zero && !Owner.IsDisposed) ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero);
            }
            public override void Dispose()
            {
                if (Owner is ScrollableControl) Owner.Layout -= LayoutChanged;
                Owner.HandleCreated -= ScrollHandleCreated;
                Owner.HandleDestroyed -= ScrollHandleDestroyed;
                Owner.VisibleChanged -= VisibilityChanged;
                interaction.Dispose();
                base.Dispose();
            }
            protected override void WndProc(ref Message message)
            {
                if (interaction != null && interaction.Process(ref message)) return;
                if (interaction != null && (ScrollbarDrawing.IsScrollInput(message.Msg) || standalone && (message.Msg == 0x0007 || message.Msg == 0x0008)))
                {
                    Message update = message;
                    interaction.Update(delegate { base.WndProc(ref update); });
                    message.Result = update.Result; return;
                }
                if (standalone && NativeControlPaint.TryClientPaint(ref message, Owner.ClientSize, PaintStandalone)) return;
                if (standalone && SuppressNativeRedraw(ref message)) return;
                if (!standalone && message.Msg == 0x0085)
                { ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
                // A managed scroll/layout performed inside a paint callback can
                // overwrite the frame after WM_NCPAINT has already finished.
                if (!standalone && message.Msg == NativeControlPaint.Paint)
                { ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); return; }
                if (!standalone && (message.Msg == NativeControlPaint.Print || message.Msg == NativeControlPaint.PrintClient))
                { if (message.Msg == NativeControlPaint.Print) ScrollbarDrawing.PaintFrame(Handle, message.WParam); return; }
                if (ScrollbarDrawing.ChangesScrollAppearance(message.Msg))
                {
                    if (standalone) PaintStandaloneWindow();
                    // Non-client scrollbars do not expose the SBM_* redraw
                    // flag. Finish their frame within this same input/layout
                    // message, before a later queued paint can expose the
                    // native light frame for a whole display interval.
                    else if (ScrollbarDrawing.ChangesFrameAppearance(message.Msg)) ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero);
                }
            }
            void PaintStandaloneWindow()
            {
                if (Handle == IntPtr.Zero || Owner.IsDisposed || !Owner.Visible || Owner.ClientSize.Width < 1 || Owner.ClientSize.Height < 1) return;
                IntPtr dc = NativeControlPaint.GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try
                {
                    using (Graphics target = Graphics.FromHdc(dc))
                    using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(target, new Rectangle(Point.Empty, Owner.ClientSize)))
                    { PaintStandalone(buffer.Graphics); buffer.Render(target); }
                }
                finally { NativeControlPaint.ReleaseDC(Handle, dc); }
            }
            bool SuppressNativeRedraw(ref Message message)
            {
                // These documented messages can paint synchronously through
                // GetDC, bypassing WM_PAINT entirely. A later dark repaint is
                // too late: the white native thumb has already been shown.
                // Change only the redraw request, retain native range/position
                // processing and its exact return value, then queue our paint.
                // https://learn.microsoft.com/windows/win32/controls/sbm-setscrollinfo
                int kind = message.Msg;
                bool redraw = kind == 0x00E9 ? message.WParam != IntPtr.Zero :
                    kind == 0x00E0 ? message.LParam != IntPtr.Zero : kind == 0x00E6;
                if (!redraw) return false;
                Message update = message;
                if (kind == 0x00E9) update.WParam = IntPtr.Zero;
                else if (kind == 0x00E0) update.LParam = IntPtr.Zero;
                else update.Msg = 0x00E2;
                base.WndProc(ref update); message.Result = update.Result;
                Owner.Invalidate(); return true;
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
            readonly ScrollbarInteraction interaction;
            internal ComboListSurface()
            { interaction = new ScrollbarInteraction(null, false, true, delegate { ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); }); }
            public void Attach(IntPtr handle)
            {
                if (Handle == handle) return;
                interaction.Detach(); ReleaseHandle(); if (handle != IntPtr.Zero) AssignHandle(handle);
                interaction.Attach(handle);
                NativeControlPaint.OwnScrollbarPainting(handle);
            }
            protected override void WndProc(ref Message message)
            {
                if (interaction.Process(ref message)) return;
                if (ScrollbarDrawing.IsScrollInput(message.Msg))
                {
                    Message update = message;
                    interaction.Update(delegate { base.WndProc(ref update); });
                    message.Result = update.Result; return;
                }
                if (message.Msg == 0x0085)
                { ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero); message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
                if (message.Msg == NativeControlPaint.Print) ScrollbarDrawing.PaintFrame(Handle, message.WParam);
                else if (ScrollbarDrawing.ChangesFrameAppearance(message.Msg)) ScrollbarDrawing.PaintFrame(Handle, IntPtr.Zero);
            }
            // Detach permits handle recreation; final disposal also releases
            // the interaction's auto-repeat timer and its native resources.
            internal void Detach() { interaction.Detach(); ReleaseHandle(); }
            public void Dispose() { interaction.Dispose(); ReleaseHandle(); }
        }
    }

    internal static class ComboSurfaceDrawing
    {
        internal static void Face(ComboBox combo, bool hovered, bool showFocus, Graphics graphics)
        {
            graphics.Clear(combo.Parent == null ? ModernTheme.Surface : combo.Parent.BackColor);
            if (combo.Width < 3 || combo.Height < 3) return;
            NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo(); info.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
            bool nativeBounds = NativeControlPaint.GetComboBoxInfo(combo.Handle, ref info);
            Rectangle button = nativeBounds ? info.Button.Bounds : new Rectangle(combo.Width - SystemInformation.VerticalScrollBarWidth - 2, 2, SystemInformation.VerticalScrollBarWidth, combo.Height - 4);
            Rectangle area = nativeBounds ? info.Item.Bounds : new Rectangle(3, 2, Math.Max(1, button.Left - 5), combo.Height - 4);
            area.Inflate(-7, 0);
            string label = combo.SelectedIndex >= 0 ? combo.GetItemText(combo.SelectedItem) : combo.Text;
            if (combo.SelectedItem is string) label = UiText.Get(label);
            if (String.IsNullOrEmpty(label)) label = UiText.Get("Auswählen");
            Field(graphics, new Rectangle(Point.Empty, combo.ClientSize), button, area, combo.Font,
                combo.DropDownStyle == ComboBoxStyle.DropDownList ? label : null, combo.Enabled,
                combo.Focused || combo.DroppedDown, hovered, combo.DroppedDown, combo.RightToLeft == RightToLeft.Yes);
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList && combo.Focused && showFocus && !combo.DroppedDown)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(area, -1, -5), ModernTheme.Accent, ModernTheme.SurfaceAlt);
        }
        internal static void Field(Graphics graphics, Rectangle bounds, Rectangle button, Rectangle text, Font font, string label,
            bool enabled, bool focused, bool hovered, bool droppedDown, bool rightToLeft)
        {
            if (bounds.Width < 3 || bounds.Height < 3) return;
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color border = enabled && focused ? ModernTheme.Accent : hovered && enabled ? ModernTheme.Muted : ModernTheme.Border;
            using (GraphicsPath path = SurfaceDrawing.Round(new RectangleF(bounds.Left + .5f, bounds.Top + .5f, bounds.Width - 1.5f, bounds.Height - 1.5f), 7))
            using (Brush fill = new SolidBrush(ModernTheme.SurfaceAlt))
            using (Pen pen = new Pen(border)) { graphics.FillPath(fill, path); graphics.DrawPath(pen, path); }
            NativeControlPaint.Chevron(graphics, button, true, !droppedDown, enabled ? ModernTheme.Accent : ModernTheme.Muted);
            graphics.SmoothingMode = previous;
            if (label != null) TextRenderer.DrawText(graphics, label, font, text, enabled ? ModernTheme.Foreground : ModernTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform |
                (rightToLeft ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
        }
        internal static void Item(ComboBox combo, DrawItemEventArgs e)
        {
            if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            bool listSelection = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color fill = listSelection ? ModernTheme.AccentSoft : ModernTheme.SurfaceAlt;
            string label = e.Index >= 0 && e.Index < combo.Items.Count ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
            if (e.Index >= 0 && e.Index < combo.Items.Count && combo.Items[e.Index] is string) label = UiText.Get(label);
            if (String.IsNullOrEmpty(label)) label = UiText.Get("Auswählen");
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds))
            {
                using (Brush brush = new SolidBrush(fill)) buffer.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(buffer.Graphics, label, combo.Font, Rectangle.Inflate(e.Bounds, -10, 0),
                    combo.Enabled ? ModernTheme.Foreground : ModernTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping |
                    (combo.RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
                if ((e.State & DrawItemState.Focus) != 0 && (e.State & (DrawItemState.ComboBoxEdit | DrawItemState.NoFocusRect)) == 0)
                    ControlPaint.DrawFocusRectangle(buffer.Graphics, e.Bounds, ModernTheme.Foreground, fill);
                buffer.Render(e.Graphics);
            }
        }
    }

    internal static class ScrollbarDrawing
    {
        internal static bool IsScrollInput(int message)
        { return message == 0x0114 || message == 0x0115 || message == 0x020A || message == 0x020E || message == 0x0100; }
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
            return message == 0x0005 || message == 0x0007 || message == 0x0008 || message == 0x000A ||
                message == 0x0018 || message == 0x0047 || message == 0x007D || message == 0x0085 || message == 0x00A0 ||
                message == 0x00A1 || message == 0x00A2 || message == 0x0114 || message == 0x0115 ||
                message == 0x0200 || message == 0x0201 || message == 0x0202 || message == 0x020A ||
                message == 0x020E || message == 0x0215 || message == 0x02A2 || message == 0x02A3 ||
                message == 0x0128 || message == 0x031A ||
                message == 0x00E0 || message == 0x00E2 || message == 0x00E9;
        }
        internal static bool ChangesFrameAppearance(int message)
        {
            // A pointer moving over the panel/list contents does not change its
            // non-client bar. Keep those high-rate input events paint-free;
            // native scrollbar hover arrives as WM_NCMOUSEMOVE/NCMOUSELEAVE.
            return message != 0x0200 && message != 0x0201 && message != 0x0202 && ChangesScrollAppearance(message);
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
                    ScrollInfo vertical, horizontal;
                    bool hasVertical = Read(window, 0xFFFFFFFB, out vertical), hasHorizontal = Read(window, 0xFFFFFFFA, out horizontal);
                    Point offset = new Point(-bounds.Left, -bounds.Top);
                    Rectangle verticalArea = vertical.Area.Bounds; verticalArea.Offset(offset);
                    Rectangle horizontalArea = horizontal.Area.Bounds; horizontalArea.Offset(offset);
                    // Never erase a visible track/thumb before painting it.
                    // Only the frame border/corner is drawn directly; each bar
                    // is composed offscreen and copied in one operation.
                    if (hasVertical) graphics.ExcludeClip(verticalArea);
                    if (hasHorizontal) graphics.ExcludeClip(horizontalArea);
                    using (Brush surface = new SolidBrush(ModernTheme.Border)) graphics.FillRectangle(surface, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                    graphics.Restore(state);
                    if (hasVertical) PaintBuffered(graphics, window, 0xFFFFFFFB, true, offset, verticalArea);
                    if (hasHorizontal) PaintBuffered(graphics, window, 0xFFFFFFFA, false, offset, horizontalArea);
                }
            }
            finally { if (suppliedDc == IntPtr.Zero) NativeControlPaint.ReleaseDC(window, dc); }
        }

        static void PaintBuffered(Graphics target, IntPtr window, uint objectId, bool vertical, Point offset, Rectangle area)
        {
            if (area.Width < 1 || area.Height < 1) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(target, area))
            { Paint(buffer.Graphics, window, objectId, vertical, offset); buffer.Render(target); }
        }

        // Native geometry retains the actual DPI/RTL layout and thumb size.
        // The shared input owner contributes hover/pressed state and the pending
        // thumb position when content scrolling is deferred until mouse-up.
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-scrollbarinfo
        internal static void Paint(Graphics graphics, IntPtr window, uint objectId, bool vertical, Point offset)
        {
            ScrollInfo info;
            if (!Read(window, objectId, out info))
            { if (objectId == 0xFFFFFFFC) graphics.Clear(ModernTheme.SurfaceAlt); return; }
            ScrollbarInteraction.Appearance(window, objectId, ref info);
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
            using (Brush fill = new SolidBrush((info.Thumb & 8) != 0 ? ModernTheme.Accent : (info.Thumb & 128) != 0 ? ModernTheme.AccentHover : SurfaceDrawing.Blend(ModernTheme.Border, ModernTheme.Accent, .45f))) graphics.FillPath(fill, path);
        }
        static void PaintArrow(Graphics graphics, Rectangle area, bool vertical, bool forward, uint state)
        {
            if ((state & 136) != 0)
                using (Brush fill = new SolidBrush((state & 8) != 0 ? ModernTheme.AccentSoft : SurfaceDrawing.Blend(ModernTheme.SurfaceAlt, ModernTheme.Foreground, .06f)))
                    graphics.FillRectangle(fill, area);
            NativeControlPaint.Chevron(graphics, area, vertical, forward, (state & 1) != 0 ? ModernTheme.Border : ModernTheme.Muted);
        }
    }
}
