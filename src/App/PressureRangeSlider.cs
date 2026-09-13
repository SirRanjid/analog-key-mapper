using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Tk75.App
{
    // One shared pressure range, with independent minimum and maximum handles.
    // Updating a reading or loading settings never sends an editing event.
    public sealed class PressureRangeSlider : Control
    {
        double rangeMinimum, rangeMaximum = 385, selectedMinimum, selectedMaximum = 385;
        double? measuredValue;
        int activeHandle, hoverHandle = -1;
        bool dragging, interactionChanged;
        SliderDragPrecision pointerDrag;

        public event EventHandler ValueChanged;
        public event EventHandler ValueCommitted;

        public PressureRangeSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(260, 64); MinimumSize = new Size(96, 54);
            BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Foreground;
            TabStop = true; Cursor = Cursors.SizeWE;
            AccessibleName = "Pressure range"; AccessibleRole = AccessibleRole.Grouping;
        }

        public double RangeMinimum { get { return rangeMinimum; } }
        public double RangeMaximum { get { return rangeMaximum; } }
        public double SelectedMinimum { get { return selectedMinimum; } }
        public double SelectedMaximum { get { return selectedMaximum; } }
        public bool IsDragging { get { return dragging; } }
        public double? MeasuredValue
        {
            get { return measuredValue; }
            set
            {
                if (value.HasValue && !Finite(value.Value)) throw new ArgumentOutOfRangeException("value");
                if (measuredValue == value) return;
                int previous = MarkerPixel(measuredValue); measuredValue = value;
                int next = MarkerPixel(measuredValue);
                if (previous == next) return;
                // A live sample only moves the small tick. Keep the scale,
                // handles and surrounding editor out of its repaint region.
                InvalidateMarker(previous); InvalidateMarker(next);
            }
        }

        // Use an atomic update so a different keyboard cannot briefly produce
        // crossed handles while its limits and saved range are being loaded.
        public void SetRange(double minimum, double maximum, double selectedMin, double selectedMax)
        {
            if (!Finite(minimum) || !Finite(maximum) || minimum < 0 || maximum > 65535 || minimum >= maximum)
                throw new ArgumentOutOfRangeException("maximum", "The pressure scale must be within 0 and 65535.");
            if (!Finite(selectedMin) || !Finite(selectedMax) || selectedMin < minimum ||
                selectedMax > maximum || selectedMin >= selectedMax)
                throw new ArgumentOutOfRangeException("selectedMax", "The selected minimum must be below its maximum and inside the scale.");
            if (rangeMinimum == minimum && rangeMaximum == maximum &&
                selectedMinimum == selectedMin && selectedMaximum == selectedMax) return;
            CancelInteraction();
            rangeMinimum = minimum; rangeMaximum = maximum;
            selectedMinimum = selectedMin; selectedMaximum = selectedMax;
            Invalidate();
        }

        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static double Clamp(double value, double minimum, double maximum)
        { return Math.Max(minimum, Math.Min(maximum, value)); }
        static string Number(double value) { return value.ToString("0.##", CultureInfo.CurrentCulture); }
        float UiScale { get { return Math.Max(1f, Font.Height / 15f); } }
        float TrackLeft { get { return Math.Min(12f * UiScale, Width / 4f); } }
        float TrackRight { get { return Math.Max(TrackLeft + 1, Width - TrackLeft - 1); } }
        float TrackY { get { return Math.Max(30f * UiScale, Math.Min(Height - 15f * UiScale, 39f * UiScale)); } }
        float HandleRadius { get { return 7f * UiScale; } }
        float Position(double value)
        { return TrackLeft + (float)((Clamp(value, rangeMinimum, rangeMaximum) - rangeMinimum) / (rangeMaximum - rangeMinimum)) * (TrackRight - TrackLeft); }
        double ValueAt(float x)
        {
            double fraction = Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0, 1);
            double value = rangeMinimum + fraction * (rangeMaximum - rangeMinimum);
            return Clamp(Math.Round(value, MidpointRounding.AwayFromZero), rangeMinimum, rangeMaximum);
        }
        int MarkerPixel(double? value) { return value.HasValue ? (int)Math.Round(Position(value.Value)) : Int32.MinValue; }
        void InvalidateMarker(int pixel)
        {
            if (pixel == Int32.MinValue) return;
            Rectangle area = Rectangle.Ceiling(new RectangleF(pixel - 2 * UiScale, TrackY + 8 * UiScale, 4 * UiScale, 9 * UiScale));
            area.Intersect(ClientRectangle);
            if (!area.IsEmpty) Invalidate(area);
        }
        RectangleF HandleBounds(int handle)
        {
            float x = Position(handle == 0 ? selectedMinimum : selectedMaximum), radius = HandleRadius;
            return new RectangleF(x - radius, TrackY - radius, 2 * radius, 2 * radius);
        }
        int NearestHandle(float x)
        {
            double a = Math.Abs(x - Position(selectedMinimum)), b = Math.Abs(x - Position(selectedMaximum));
            return Math.Abs(a - b) < 0.5 ? activeHandle : a < b ? 0 : 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var background = new SolidBrush(Parent == null ? BackColor : Parent.BackColor))
                g.FillRectangle(background, e.ClipRectangle);
            if (Width < 3 || Height < 3) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Enabled ? ForeColor : ModernTheme.Muted;
            Color accent = Enabled ? ModernTheme.Accent : ModernTheme.Muted;
            int labelHeight = Math.Max(Font.Height + 4, (int)(22 * UiScale));
            TextFormatFlags labels = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
            Rectangle labelArea = new Rectangle(0, 0, Math.Max(1, Width / 2 - 4), labelHeight);
            TextRenderer.DrawText(g, "Min " + Number(selectedMinimum), Font, labelArea, ink, labels);
            labelArea.X = Width / 2 + 4; labelArea.Width = Math.Max(1, Width - labelArea.X);
            TextRenderer.DrawText(g, "Max " + Number(selectedMaximum), Font, labelArea, ink, labels | TextFormatFlags.Right);
            using (var track = new Pen(ModernTheme.Border, 4 * UiScale))
            using (var selected = new Pen(accent, 4 * UiScale))
            {
                track.StartCap = track.EndCap = selected.StartCap = selected.EndCap = LineCap.Round;
                g.DrawLine(track, TrackLeft, TrackY, TrackRight, TrackY);
                g.DrawLine(selected, Position(selectedMinimum), TrackY, Position(selectedMaximum), TrackY);
            }
            if (measuredValue.HasValue)
            {
                float x = MarkerPixel(measuredValue);
                using (var marker = new Pen(ink, Math.Max(1.5f, UiScale)))
                    g.DrawLine(marker, x, TrackY + 10 * UiScale, x, TrackY + 15 * UiScale);
            }
            // Paint the active handle last, so touching or very close handles
            // retain a clear focus cue and predictable keyboard ownership.
            DrawHandle(g, 1 - activeHandle, accent);
            DrawHandle(g, activeHandle, accent);
        }

        void DrawHandle(Graphics graphics, int handle, Color accent)
        {
            RectangleF area = HandleBounds(handle);
            bool selected = Enabled && (hoverHandle == handle || dragging && activeHandle == handle);
            using (var fill = new SolidBrush(selected ? ModernTheme.AccentSoft : ModernTheme.SurfaceAlt))
            using (var border = new Pen(selected ? ModernTheme.AccentHover : accent, Math.Max(1.5f, 1.5f * UiScale)))
            {
                graphics.FillEllipse(fill, area); graphics.DrawEllipse(border, area);
            }
            if (Focused && ShowFocusCues && activeHandle == handle)
            {
                area.Inflate(3 * UiScale, 3 * UiScale);
                using (var focus = new Pen(accent)) { focus.DashStyle = DashStyle.Dot; graphics.DrawEllipse(focus, area); }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus(); activeHandle = NearestHandle(e.X); hoverHandle = activeHandle;
            RectangleF hit = HandleBounds(activeHandle); hit.Inflate(5 * UiScale, 5 * UiScale);
            interactionChanged = false; dragging = true; Capture = true;
            if (!hit.Contains(e.Location)) MoveHandle(ValueAt(e.X));
            pointerDrag.Begin(activeHandle == 0 ? selectedMinimum : selectedMaximum, e.X, TrackY, UiScale);
            Invalidate();
        }
        void MovePointer(int x, int y)
        {
            double gap = Math.Min(1, rangeMaximum - rangeMinimum);
            double minimum = activeHandle == 0 ? rangeMinimum : selectedMinimum + gap;
            double maximum = activeHandle == 0 ? selectedMaximum - gap : rangeMaximum;
            if (pointerDrag.Move(x, y, (rangeMaximum - rangeMinimum) / (TrackRight - TrackLeft), minimum, maximum))
                MoveHandle(pointerDrag.QuantizedValue(1, minimum, maximum));
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { MovePointer(e.X, e.Y); return; }
            int next = NearestHandle(e.X);
            if (hoverHandle != next) { hoverHandle = next; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging)
            { MovePointer(e.X, e.Y); FinishInteraction(); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        { base.OnMouseCaptureChanged(e); if (dragging && !Capture) FinishInteraction(); }
        protected override void OnMouseLeave(EventArgs e)
        { hoverHandle = -1; if (!dragging) Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e)
        {
            activeHandle = (ModifierKeys & Keys.Shift) != 0 ? 1 : 0;
            base.OnGotFocus(e); Invalidate(); NotifyFocus();
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e)
        { if (!Enabled) CancelInteraction(); base.OnEnabledChanged(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == Keys.None && Focused)
            {
                bool backward = (keyData & Keys.Shift) != 0;
                if (activeHandle == (backward ? 1 : 0))
                { activeHandle = 1 - activeHandle; Invalidate(); NotifyFocus(); return true; }
            }
            return base.ProcessDialogKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || !Enabled || e.Alt || e.Control) return;
            double value = activeHandle == 0 ? selectedMinimum : selectedMaximum;
            double step = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: value -= step; break;
                case Keys.Right: case Keys.Up: value += step; break;
                case Keys.PageDown: value -= Math.Max(1, Math.Round((rangeMaximum - rangeMinimum) / 10)); break;
                case Keys.PageUp: value += Math.Max(1, Math.Round((rangeMaximum - rangeMinimum) / 10)); break;
                case Keys.Home: value = rangeMinimum; break;
                case Keys.End: value = rangeMaximum; break;
                default: return;
            }
            interactionChanged = false; MoveHandle(value); CommitValue();
            e.Handled = e.SuppressKeyPress = true;
        }

        void MoveHandle(double value)
        {
            double gap = Math.Min(1, rangeMaximum - rangeMinimum);
            if (activeHandle == 0)
            {
                value = Clamp(value, rangeMinimum, selectedMaximum - gap);
                if (selectedMinimum == value) return;
                selectedMinimum = value;
            }
            else
            {
                value = Clamp(value, selectedMinimum + gap, rangeMaximum);
                if (selectedMaximum == value) return;
                selectedMaximum = value;
            }
            interactionChanged = true; Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, activeHandle + 1);
            EventHandler handler = ValueChanged; if (handler != null) handler(this, EventArgs.Empty);
        }
        void CommitValue()
        {
            bool changed = interactionChanged; interactionChanged = false;
            if (changed) { EventHandler handler = ValueCommitted; if (handler != null) handler(this, EventArgs.Empty); }
        }
        void FinishInteraction()
        { dragging = false; Capture = false; Invalidate(); CommitValue(); }
        void CancelInteraction()
        { dragging = interactionChanged = false; if (Capture) Capture = false; }
        void NotifyFocus()
        { if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Focus, activeHandle + 1); }

        protected override AccessibleObject CreateAccessibilityInstance() { return new RangeAccessibleObject(this); }
        sealed class RangeAccessibleObject : ControlAccessibleObject
        {
            readonly PressureRangeSlider owner;
            readonly AccessibleObject[] handles;
            public RangeAccessibleObject(PressureRangeSlider control) : base(control)
            { owner = control; handles = new AccessibleObject[] { new HandleAccessibleObject(control, 0), new HandleAccessibleObject(control, 1) }; }
            public override int GetChildCount() { return 2; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < handles.Length ? handles[index] : null; }
            public override AccessibleObject GetFocused() { return owner.Focused ? handles[owner.activeHandle] : null; }
        }
        sealed class HandleAccessibleObject : AccessibleObject
        {
            readonly PressureRangeSlider owner;
            readonly int handle;
            public HandleAccessibleObject(PressureRangeSlider control, int index) { owner = control; handle = index; }
            public override string Name { get { return owner.AccessibleName + (handle == 0 ? ": Min" : ": Max"); } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.Slider; } }
            public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Ceiling(owner.HandleBounds(handle))); } }
            public override AccessibleStates State
            {
                get { return !owner.Enabled ? AccessibleStates.Unavailable : AccessibleStates.Focusable |
                    (owner.Focused && owner.activeHandle == handle ? AccessibleStates.Focused : AccessibleStates.None); }
            }
            public override string Value
            {
                get { return Number(handle == 0 ? owner.selectedMinimum : owner.selectedMaximum); }
                set
                {
                    double number;
                    if (!owner.Enabled || !Double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || !Finite(number)) return;
                    owner.activeHandle = handle; owner.interactionChanged = false; owner.MoveHandle(number); owner.CommitValue();
                }
            }
            public override void Select(AccessibleSelection flags)
            {
                if (!owner.Enabled || (flags & (AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection)) == 0) return;
                owner.Focus(); owner.activeHandle = handle; owner.Invalidate(); owner.NotifyFocus();
            }
        }
    }
}
