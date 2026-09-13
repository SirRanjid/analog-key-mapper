using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Tk75.App
{
    internal sealed class InputThresholdSlider : Control
    {
        double value = 10, originalValue;
        bool mixed, originalMixed, editing, mouseEdit, finishing, changed;
        int pointerOrigin;
        float thumbOffset;
        bool preserveThumb;
        double? measuredPercent;
        public event Action<double> Previewed;
        public event Action<double> Committed;
        public event Action Canceled;
        public bool IsEditing { get { return editing; } }
        public double Value { get { return value; } }
        public bool Mixed { get { return mixed; } }
        public double? MeasuredPercent
        {
            get { return measuredPercent; }
            set { if (measuredPercent == value) return; measuredPercent = value; Invalidate(); }
        }
        public InputThresholdSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(90, 180); MinimumSize = new Size(58, 64); TabStop = true; Cursor = Cursors.SizeNS;
            AccessibleRole = AccessibleRole.Slider;
        }
        public void SetValue(double percent, bool isMixed)
        {
            if (Double.IsNaN(percent) || Double.IsInfinity(percent) || percent < .01 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            if (value == percent && mixed == isMixed) return;
            CancelEdit(); value = percent; mixed = isMixed; Invalidate();
        }
        float TrackX { get { return Math.Max(12, Math.Min(24, Width * .22f)); } }
        float TrackTop { get { return 11; } }
        float TrackBottom { get { return Math.Max(TrackTop + 1, Height - 14); } }
        float Position(double percent) { return TrackTop + (float)(percent / 100) * (TrackBottom - TrackTop); }
        static double Limit(double percent) { return Math.Max(.01, Math.Min(100, percent)); }
        void BeginEdit(bool mouse)
        {
            if (editing || !Enabled) return;
            originalValue = value; originalMixed = mixed; editing = true; mouseEdit = mouse; changed = false;
        }
        void Preview(double next)
        {
            next = Limit(Math.Round(next, 2));
            if (value == next && !mixed) return;
            value = next; mixed = false; changed = true; Invalidate();
            if (Previewed != null) Previewed(value);
        }
        void FinishEdit()
        {
            if (!editing) return;
            bool apply = changed && (originalMixed || originalValue != value); editing = false;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            if (apply) { if (Committed != null) Committed(value); }
            else if (Canceled != null) Canceled();
            Invalidate();
        }
        public void CancelEdit()
        {
            if (!editing) return;
            editing = false; value = originalValue; mixed = originalMixed;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            Invalidate(); if (Canceled != null) Canceled();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics; graphics.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor); graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 3 || Height < 3) return;
            Color accent = Enabled ? ModernTheme.Accent : ModernTheme.Muted;
            float x = TrackX, top = TrackTop, bottom = TrackBottom;
            using (var track = new Pen(ModernTheme.Border, 4)) { track.StartCap = track.EndCap = LineCap.Round; graphics.DrawLine(track, x, top, x, bottom); }
            using (var ticks = new Pen(ModernTheme.Muted, 1))
                for (int i = 0; i <= 20; i++)
                {
                    float y = top + (bottom - top) * i / 20;
                    bool major = i % 5 == 0; graphics.DrawLine(ticks, x + 8, y, x + (major ? 15 : 11), y);
                    if (major && (Height >= 125 || i % 10 == 0))
                        TextRenderer.DrawText(graphics, (i * 5).ToString(CultureInfo.InvariantCulture), Font,
                            new Rectangle((int)x + 19, (int)y - Font.Height / 2, Math.Max(1, Width - (int)x - 20), Font.Height + 1),
                            ModernTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
            if (!mixed)
            {
                float y = Position(value);
                using (var fill = new Pen(accent, 4)) { fill.StartCap = fill.EndCap = LineCap.Round; graphics.DrawLine(fill, x, top, x, y); }
                using (var fill = new SolidBrush(accent)) graphics.FillEllipse(fill, x - 6, y - 6, 12, 12);
                using (var edge = new Pen(ModernTheme.Surface, 1.5f)) graphics.DrawEllipse(edge, x - 6, y - 6, 12, 12);
                using (var marker = new Pen(accent, 1.5f)) graphics.DrawLine(marker, x - 11, y, x - 8, y);
            }
            else
            {
                TextRenderer.DrawText(graphics, "?", Font, new Rectangle((int)x - 7, (int)((top + bottom) / 2) - Font.Height / 2, 14, Font.Height + 1),
                    ModernTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if (measuredPercent.HasValue)
            {
                float y = Position(Limit(measuredPercent.Value));
                using (var marker = new Pen(ModernTheme.Foreground, 1.5f)) graphics.DrawLine(marker, x - 10, y, x + 13, y);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, new Rectangle(1, 1, Width - 3, Height - 3), accent, BackColor);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus(); BeginEdit(true); pointerOrigin = e.Y; thumbOffset = 0;
            preserveThumb = !mixed && Math.Abs(e.Y - Position(value)) <= 8;
            if (preserveThumb) thumbOffset = e.Y - Position(value);
            Capture = true; if (!preserveThumb) MovePointer(e.Y);
        }
        void MovePointer(int y)
        {
            if (!editing || preserveThumb && y == pointerOrigin) return;
            preserveThumb = false; Preview((y - thumbOffset - TrackTop) / (TrackBottom - TrackTop) * 100);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        { base.OnMouseMove(e); if (editing && mouseEdit) MovePointer(e.Y); }
        protected override void OnMouseUp(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left && editing && mouseEdit) { MovePointer(e.Y); FinishEdit(); } base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && editing) { CancelEdit(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter && editing) { FinishEdit(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.Control || e.Alt || !Enabled || editing && mouseEdit) { base.OnKeyDown(e); return; }
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down && e.KeyCode != Keys.Home && e.KeyCode != Keys.End && e.KeyCode != Keys.PageUp && e.KeyCode != Keys.PageDown) { base.OnKeyDown(e); return; }
            BeginEdit(false);
            double step = e.Shift || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown ? 1 : .1;
            Preview(e.KeyCode == Keys.Home ? .01 : e.KeyCode == Keys.End ? 100 : value + (e.KeyCode == Keys.Up || e.KeyCode == Keys.PageUp ? -step : step));
            e.Handled = e.SuppressKeyPress = true;
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (editing && !mouseEdit && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)) FinishEdit();
            base.OnKeyUp(e);
        }
        protected override bool ProcessDialogKey(Keys keyData)
        { if (editing && keyData == Keys.Escape) { CancelEdit(); return true; } return base.ProcessDialogKey(keyData); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !finishing && mouseEdit) CancelEdit(); }
        protected override void OnLostFocus(EventArgs e) { CancelEdit(); base.OnLostFocus(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) CancelEdit(); base.OnVisibleChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) CancelEdit(); base.OnEnabledChanged(e); }
        protected override void OnSizeChanged(EventArgs e) { CancelEdit(); base.OnSizeChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) CancelEdit(); base.Dispose(disposing); }
    }
}
