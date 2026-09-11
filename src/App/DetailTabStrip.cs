using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Tk75.App
{
    // These buttons retain native click/Space/Enter behavior while exposing
    // one tab stop and page-tab semantics for the fixed detail work area.
    internal sealed class DetailTabStrip : Panel
    {
        public DetailTabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Margin = Padding = Padding.Empty; TabStop = false;
            AccessibleRole = AccessibleRole.PageTabList;
        }
        internal void RefreshTabStops()
        {
            var tabs = Controls.OfType<DetailTabButton>().ToArray();
            var selected = tabs.FirstOrDefault(tab => tab.IsSelected && tab.Enabled) ?? tabs.FirstOrDefault(tab => tab.Enabled);
            foreach (var tab in tabs) tab.TabStop = tab == selected;
        }
        internal void MoveSelection(DetailTabButton current, Keys key)
        {
            var tabs = Controls.OfType<DetailTabButton>().Where(tab => tab.Enabled && tab.Visible).ToArray();
            if (tabs.Length == 0) return;
            int index = Array.IndexOf(tabs, current);
            int next = key == Keys.Home ? 0 : key == Keys.End ? tabs.Length - 1 :
                (index + (key == Keys.Left ? -1 : 1) + tabs.Length) % tabs.Length;
            tabs[next].Focus(); tabs[next].PerformClick();
        }
        protected override void OnControlAdded(ControlEventArgs e)
        { base.OnControlAdded(e); e.Control.TabIndex = Controls.Count - 1; RefreshTabStops(); PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int count = Controls.Count; if (count == 0) return;
            // Ignore generic button margins: tabs meet along a shared baseline.
            for (int i = 0; i < count; i++)
            {
                int left = ClientSize.Width * i / count, right = ClientSize.Width * (i + 1) / count;
                Controls[i].SetBounds(left, 0, right - left, ClientSize.Height);
            }
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new TabStripAccessibleObject(this); }
        sealed class TabStripAccessibleObject : ControlAccessibleObject
        {
            readonly DetailTabStrip owner;
            public TabStripAccessibleObject(DetailTabStrip owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
            public override AccessibleObject GetSelected()
            {
                var selected = owner.Controls.OfType<DetailTabButton>().FirstOrDefault(tab => tab.IsSelected);
                return selected == null ? null : selected.AccessibilityObject;
            }
        }
    }

    internal sealed class DetailTabButton : Button
    {
        bool selected, hovered, pressed;
        public DetailTabButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            Margin = Padding = Padding.Empty; TabStop = false; AccessibleRole = AccessibleRole.PageTab;
        }
        public bool IsSelected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                var strip = Parent as DetailTabStrip;
                if (strip != null) strip.RefreshTabStops(); else TabStop = value;
                Invalidate();
                if (IsHandleCreated)
                {
                    AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                    if (value) AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
                }
            }
        }
        static bool NavigationKey(Keys key) { return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End; }
        protected override bool IsInputKey(Keys keyData)
        { return (keyData & Keys.Modifiers) == Keys.None && NavigationKey(keyData) || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var strip = Parent as DetailTabStrip;
            if (e.Modifiers == Keys.None && NavigationKey(e.KeyCode) && strip != null)
            { strip.MoveSelection(this, e.KeyCode); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None) { pressed = true; Invalidate(); }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = false; Invalidate(); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e); pressed = false;
            var strip = Parent as DetailTabStrip; if (strip != null) strip.RefreshTabStops();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(Parent == null ? ModernTheme.Background : Parent.BackColor);
            if (ClientSize.Width < 2 || ClientSize.Height < 8) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (selected || hovered && Enabled)
            {
                RectangleF area = new RectangleF(.5f, 4, ClientSize.Width - 1, ClientSize.Height - 4);
                using (GraphicsPath path = TabShape(area))
                using (Brush fill = new SolidBrush(pressed ? ModernTheme.AccentSoft : selected ? ModernTheme.Surface : ModernTheme.SurfaceAlt))
                    graphics.FillPath(fill, path);
            }
            using (Pen baseline = new Pen(ModernTheme.Border)) graphics.DrawLine(baseline, 0, Height - 1, Width, Height - 1);
            if (selected)
                using (Brush accent = new SolidBrush(Enabled ? ModernTheme.Accent : ModernTheme.Muted))
                    graphics.FillRectangle(accent, 0, Height - 3, Width, 3);
            Rectangle text = new Rectangle(8, 3, Math.Max(0, Width - 16), Height - 7);
            TextRenderer.DrawText(graphics, Text, Font, text, !Enabled ? ModernTheme.Muted : selected ? ModernTheme.Accent : hovered ? ModernTheme.Foreground : ModernTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(text, -3, -6), ModernTheme.Accent, ModernTheme.Surface);
        }
        static GraphicsPath TabShape(RectangleF bounds)
        {
            const float diameter = 12;
            var path = new GraphicsPath();
            path.AddLine(bounds.Left, bounds.Bottom, bounds.Left, bounds.Top + diameter / 2);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddLine(bounds.Right, bounds.Top + diameter / 2, bounds.Right, bounds.Bottom);
            path.CloseFigure(); return path;
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new TabAccessibleObject(this); }
        sealed class TabAccessibleObject : ControlAccessibleObject
        {
            readonly DetailTabButton owner;
            public TabAccessibleObject(DetailTabButton owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }
            public override AccessibleStates State { get { return (base.State & ~(AccessibleStates.Pressed | AccessibleStates.Selected)) | AccessibleStates.Selectable | (owner.IsSelected ? AccessibleStates.Selected : 0); } }
            public override string DefaultAction { get { return UiText.Get("Auswählen", "Select"); } }
            public override void DoDefaultAction() { if (!owner.IsDisposed) owner.PerformClick(); }
            public override void Select(AccessibleSelection flags)
            {
                if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus();
                if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) DoDefaultAction();
            }
        }
    }
}
