using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tk75.App
{
    internal sealed class LiveValueLabel : Label
    {
        bool updatingFixedText;
        protected override void OnTextChanged(EventArgs e)
        {
            // Label.AdjustSize repeats the existing bounds on each Text change,
            // even with AutoSize off. Size also requests parent layout after
            // SetBoundsCore. A docked fixed-size readout needs neither pass.
            Control parent = Parent;
            if (AutoSize || Dock != DockStyle.Fill || parent == null) { base.OnTextChanged(e); return; }
            parent.SuspendLayout();
            bool previous = updatingFixedText; updatingFixedText = true;
            try { base.OnTextChanged(e); }
            finally { updatingFixedText = previous; parent.ResumeLayout(false); }
        }
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            // AdjustSize also temporarily restores the Label's requested size.
            // Keep the docked bounds throughout the text update, not just after it.
            if (updatingFixedText && !AutoSize && Dock == DockStyle.Fill) return;
            base.SetBoundsCore(x, y, width, height, specified);
        }
    }

    // Live output values should not expose a separate background erase on each
    // sample. Keep native layout, cell painting and selection behavior intact.
    internal sealed class BufferedValueGrid : DataGridView
    {
        public BufferedValueGrid() { DoubleBuffered = true; }
    }

    internal static class SurfaceDrawing
    {
        public static GraphicsPath Round(RectangleF area, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(area.Width, area.Height)));
            path.AddArc(area.Left, area.Top, d, d, 180, 90);
            path.AddArc(area.Right - d, area.Top, d, d, 270, 90);
            path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
            path.AddArc(area.Left, area.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        public static Color Blend(Color from, Color to, float amount)
        { return Color.FromArgb((int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount)); }
    }

    // Owner painting changes appearance only; native Button keyboard, accessible
    // role, DialogResult, default-button and click behavior stay intact.
    public class SleekButton : Button
    {
        bool hover, pressed;
        public string Appearance { get; set; }
        public bool IconOnly { get; set; }
        public SleekButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand; Padding = new Padding(12, 4, 12, 4); Height = 38;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent == null ? ModernTheme.Background : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 3 || Height < 3) return;
            Color fill = Enabled ? BackColor : ModernTheme.SurfaceAlt;
            Color ink = Enabled ? ForeColor : ModernTheme.Muted;
            Color border = Appearance == "primary" && Enabled ? fill : ModernTheme.Border;
            if (Enabled && hover) { fill = SurfaceDrawing.Blend(fill, Color.White, 0.065f); border = SurfaceDrawing.Blend(border, ModernTheme.Foreground, 0.14f); }
            if (Enabled && pressed) fill = SurfaceDrawing.Blend(fill, Color.Black, 0.12f);
            using (var path = SurfaceDrawing.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 9))
            using (var brush = new SolidBrush(fill)) using (var pen = new Pen(border)) { g.FillPath(brush, path); g.DrawPath(pen, path); }
            Rectangle text = IconOnly ? new Rectangle(3, 3 + (pressed ? 1 : 0), Math.Max(1, Width - 6), Math.Max(1, Height - 6))
                : new Rectangle(Padding.Left, Padding.Top + (pressed ? 1 : 0), Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                (IconOnly ? TextFormatFlags.NoPadding | TextFormatFlags.SingleLine : TextFormatFlags.EndEllipsis);
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(g, Text, Font, text, ink, flags);
            if (Focused && ShowFocusCues)
                using (var path = SurfaceDrawing.Round(new RectangleF(3.5f, 3.5f, Width - 8, Height - 8), 6))
                using (var pen = new Pen(Appearance == "primary" ? ModernTheme.Background : ModernTheme.Accent)) g.DrawPath(pen, path);
        }
    }

    public class SleekCard : Panel
    {
        public int CornerRadius { get; set; }
        public SleekCard()
        {
            CornerRadius = 18; Padding = new Padding(16); BackColor = ModernTheme.Surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? ModernTheme.Background : Parent.BackColor);
            if (Width < 3 || Height < 3) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = SurfaceDrawing.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), CornerRadius))
            using (var brush = new SolidBrush(BackColor)) using (var pen = new Pen(ModernTheme.Border))
            { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }

    public class SleekComboBox : ComboBox
    {
        readonly NativeSurfaceTheme.ComboListSurface listSurface = new NativeSurfaceTheme.ComboListSurface();
        bool hovered;
        // Pressure testing should not run a hidden letter search in the target
        // picker. Other pickers retain the native behavior unless explicitly opted in.
        public bool IgnoreClosedTextInput { get; set; }
        public SleekComboBox()
        {
            FlatStyle = FlatStyle.Flat; DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList;
            ItemHeight = 28; IntegralHeight = false; DropDownHeight = 300;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AttachListSurface(); }
        protected override void OnHandleDestroyed(EventArgs e) { listSurface.Dispose(); base.OnHandleDestroyed(e); }
        protected override void Dispose(bool disposing) { if (disposing) listSurface.Dispose(); base.Dispose(disposing); }
        void AttachListSurface()
        {
            if (!IsHandleCreated) return;
            NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo();
            info.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
            if (NativeControlPaint.GetComboBoxInfo(Handle, ref info)) listSurface.Attach(info.List);
        }
        protected override void OnDropDown(EventArgs e) { AttachListSurface(); base.OnDropDown(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void WndProc(ref Message message)
        {
            // One complete buffered face replaces WM_PAINT. The native combo
            // procedure continues to own its real arrow hit target, drop-down,
            // editable child, keyboard navigation and accessibility tree.
            if (DropDownStyle != ComboBoxStyle.Simple && NativeControlPaint.TryClientPaint(ref message, ClientSize, PaintFace)) return;
            base.WndProc(ref message);
        }
        void PaintFace(Graphics graphics)
        {
            graphics.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor);
            if (Width < 3 || Height < 3) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color border = Enabled && (Focused || DroppedDown) ? ModernTheme.Accent : hovered && Enabled ? ModernTheme.Muted : ModernTheme.Border;
            using (GraphicsPath path = SurfaceDrawing.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), 7))
            using (Brush fill = new SolidBrush(ModernTheme.SurfaceAlt))
            using (Pen pen = new Pen(border)) { graphics.FillPath(fill, path); graphics.DrawPath(pen, path); }
            NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo();
            info.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
            bool nativeBounds = NativeControlPaint.GetComboBoxInfo(Handle, ref info);
            Rectangle button = nativeBounds ? info.Button.Bounds : new Rectangle(Width - SystemInformation.VerticalScrollBarWidth - 2, 2, SystemInformation.VerticalScrollBarWidth, Height - 4);
            NativeControlPaint.Chevron(graphics, button, true, !DroppedDown, Enabled ? ModernTheme.Accent : ModernTheme.Muted);
            if (DropDownStyle == ComboBoxStyle.DropDownList)
            {
                Rectangle area = nativeBounds ? info.Item.Bounds : new Rectangle(3, 2, Math.Max(1, button.Left - 5), Height - 4);
                area.Inflate(-7, 0);
                string label = SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text;
                if (SelectedItem is string) label = UiText.Get(label);
                if (String.IsNullOrEmpty(label)) label = UiText.Get("Auswählen");
                TextRenderer.DrawText(graphics, label, Font, area, Enabled ? ModernTheme.Foreground : ModernTheme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                    (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
                if (Focused && ShowFocusCues && !DroppedDown)
                    ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(area, -1, -5), ModernTheme.Accent, ModernTheme.SurfaceAlt);
            }
        }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            bool ignore = IgnoreClosedTextInput && DropDownStyle == ComboBoxStyle.DropDownList &&
                !DroppedDown && !Char.IsControl(e.KeyChar);
            if (ignore) e.Handled = true;
            base.OnKeyPress(e);
            if (ignore) e.Handled = true;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            bool listSelection = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color fill = listSelection ? ModernTheme.AccentSoft : ModernTheme.SurfaceAlt;
            string label = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            if (e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is string) label = UiText.Get(label);
            if (String.IsNullOrEmpty(label)) label = UiText.Get("Auswählen");
            // Popup rows use the native owner's supplied area and preserve native
            // selection. The closed face is painted by its single WM_PAINT pass.
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds))
            {
                using (var brush = new SolidBrush(fill)) buffer.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(buffer.Graphics, label, Font, Rectangle.Inflate(e.Bounds, -10, 0),
                    Enabled ? ModernTheme.Foreground : ModernTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
                if ((e.State & DrawItemState.Focus) != 0 && (e.State & (DrawItemState.ComboBoxEdit | DrawItemState.NoFocusRect)) == 0)
                    ControlPaint.DrawFocusRectangle(buffer.Graphics, e.Bounds, ModernTheme.Foreground, fill);
                buffer.Render(e.Graphics);
            }
        }
    }

    // Keep the existing Value/Maximum contract while drawing a quiet, static
    // pressure meter. There is no visual animation that could imply new samples.
    public class SleekProgressBar : ProgressBar
    {
        public SleekProgressBar()
        { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg >= 0x0401 && m.Msg <= 0x0410) Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 3 || Height < 3) return;
            RectangleF area = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var track = SurfaceDrawing.Round(area, Height / 2f)) using (var brush = new SolidBrush(ModernTheme.Border)) e.Graphics.FillPath(brush, track);
            float fraction = Maximum > Minimum ? (Value - Minimum) / (float)(Maximum - Minimum) : 0;
            float amount = area.Width * fraction;
            if (amount > 0)
                using (var path = SurfaceDrawing.Round(new RectangleF(0, 0, amount, area.Height), Height / 2f))
                using (var brush = new SolidBrush(ModernTheme.Accent)) e.Graphics.FillPath(brush, path);
        }
    }
}
