using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Tk75.App
{
    // A compact two-handle range using the same completed-gesture contract as
    // the numeric grid. Drawing and pointer motion never configure the runtime.
    internal sealed class CurveRangeRail : Control
    {
        readonly double[] values = new double[2], minimum = new double[2], maximum = new double[2];
        readonly bool[] mixed = new bool[2];
        bool editing, mouseGesture, finishing, originalMixed, changed;
        int active;
        double originalValue;
        SliderDragPrecision pointerDrag;
        public bool Reversed { get; set; }
        public bool IsEditing { get { return editing; } }
        public int ActiveHandle { get { return active; } }
        public event Action<int> EditStarted;
        public event Action<int, double> Previewed, Committed;
        public event Action Canceled;
        public CurveRangeRail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; AccessibleRole = AccessibleRole.Slider; Cursor = Cursors.SizeNS;
            Size = new Size(22, 120);
        }
        public void Configure(int handle, double value, bool isMixed, double low, double high)
        {
            if (handle < 0 || handle > 1 || value < 0 || value > 1 || low < 0 || high > 1 || low > high) throw new ArgumentOutOfRangeException("handle");
            if (editing) return;
            values[handle] = value; mixed[handle] = isMixed; minimum[handle] = low; maximum[handle] = high;
            Invalidate();
        }
        public double ValueAt(int handle) { return values[handle]; }
        public bool MixedAt(int handle) { return mixed[handle]; }
        float TrackHeight { get { return Math.Max(1, Height - 12); } }
        float Position(double value) { return 6 + (float)(Reversed ? 1 - value : value) * TrackHeight; }
        int Hit(Point point)
        {
            double first = Math.Abs(point.Y - Position(values[0])), second = Math.Abs(point.Y - Position(values[1]));
            return Math.Abs(first - second) < .1 ? active : first < second ? 0 : 1;
        }
        void Begin(int handle, bool mouse)
        {
            if (!Enabled || editing) return;
            active = handle;
            if (EditStarted != null) EditStarted(handle);
            if (!Enabled) return;
            originalValue = values[active]; originalMixed = mixed[active]; changed = false;
            editing = true; mouseGesture = mouse;
        }
        void Preview(double value)
        { PreviewExact(Math.Round(value, 3)); }
        void PreviewExact(double value)
        {
            if (!editing) return;
            value = Math.Max(minimum[active], Math.Min(maximum[active], value));
            if (values[active] == value && !mixed[active]) return;
            values[active] = value; mixed[active] = false; changed = true; Invalidate();
            if (Previewed != null) Previewed(active, value);
        }
        void Finish()
        {
            if (!editing) return;
            bool apply = changed && (originalMixed || values[active] != originalValue);
            editing = false;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            if (apply) { if (Committed != null) Committed(active, values[active]); }
            else if (Canceled != null) Canceled();
        }
        public void CancelEdit()
        {
            if (!editing) return;
            editing = false; values[active] = originalValue; mixed[active] = originalMixed;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            Invalidate(); if (Canceled != null) Canceled();
        }
        void MovePointer(int x, int y)
        {
            if (!pointerDrag.Move(y, x, (Reversed ? -1.0 : 1.0) / TrackHeight, minimum[active], maximum[active])) return;
            PreviewExact(pointerDrag.QuantizedValue(.001, minimum[active], maximum[active]));
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus(); Begin(Hit(e.Location), true); if (!editing) return;
            float position = Position(values[active]);
            // Picking up either a normal or mixed handle is not a new value.
            // Keep its exact fractional position until the pointer moves.
            bool preserveThumb = Math.Abs(e.Y - position) <= 7;
            if (!preserveThumb)
            {
                double amount = (e.Y - 6) / TrackHeight;
                Preview(Reversed ? 1 - amount : amount);
            }
            pointerDrag.Begin(values[active], e.Y, Width / 2.0, Math.Max(1, Font.Height / 15.0));
            Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        { base.OnMouseMove(e); if (editing && mouseGesture) MovePointer(e.X, e.Y); }
        protected override void OnMouseUp(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left && editing && mouseGesture) { MovePointer(e.X, e.Y); Finish(); } base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e)
        { base.OnMouseCaptureChanged(e); if (!finishing && !Capture && editing && mouseGesture) CancelEdit(); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { CancelEdit(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Space)
            { if (!editing) { active = e.KeyCode == Keys.Left ? 0 : e.KeyCode == Keys.Right ? 1 : 1 - active; Invalidate(); } e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down && e.KeyCode != Keys.Home && e.KeyCode != Keys.End) return;
            if (mouseGesture && editing) return;
            Begin(active, false);
            double direction = (e.KeyCode == Keys.Up ? -1 : 1) * (Reversed ? -1 : 1);
            Preview(e.KeyCode == Keys.Home ? minimum[active] : e.KeyCode == Keys.End ? maximum[active] : values[active] + direction * (e.Shift ? .01 : .001));
            e.Handled = e.SuppressKeyPress = true;
        }
        protected override void OnKeyUp(KeyEventArgs e)
        { base.OnKeyUp(e); if (editing && !mouseGesture && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)) { Finish(); e.Handled = e.SuppressKeyPress = true; } }
        protected override void OnLostFocus(EventArgs e) { CancelEdit(); base.OnLostFocus(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) CancelEdit(); base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) CancelEdit(); base.OnVisibleChanged(e); }
        protected override void OnSizeChanged(EventArgs e) { if (editing) CancelEdit(); base.OnSizeChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = Width / 2f; Color ink = Enabled ? ModernTheme.Accent : ModernTheme.Muted;
            using (var track = new Pen(ModernTheme.Border, 3)) g.DrawLine(track, x, 6, x, Height - 6);
            if (!mixed[0] && !mixed[1]) using (var span = new Pen(Color.FromArgb(150, ink), 3)) g.DrawLine(span, x, Position(values[0]), x, Position(values[1]));
            for (int i = 0; i < 2; i++)
            {
                float y = Position(values[i]);
                // Distinct horizontal handles, not decorative curve sample dots.
                using (var fill = new SolidBrush(mixed[i] ? ModernTheme.Surface : ink)) g.FillRectangle(fill, x - 6, y - 3, 12, 6);
                using (var edge = new Pen(Focused && active == i ? ModernTheme.Foreground : ink, 1)) g.DrawRectangle(edge, x - 6, y - 3, 12, 6);
                if (mixed[i]) using (var mark = new Pen(ModernTheme.Muted)) g.DrawLine(mark, x - 3, y, x + 3, y);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)));
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new RangeAccessible(this); }
        sealed class RangeAccessible : ControlAccessibleObject
        {
            readonly CurveRangeRail owner;
            public RangeAccessible(CurveRangeRail owner) : base(owner) { this.owner = owner; }
            public override string Value { get { return String.Format(CultureInfo.CurrentCulture, "{0:0.#}% – {1:0.#}%", owner.values[0] * 100, owner.values[1] * 100); } }
        }
    }
}
