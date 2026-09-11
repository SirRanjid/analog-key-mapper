using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tk75.App
{
    // A gesture requests a connection; only SetConnection confirms the runtime
    // state. The movable plug is drawn directly, with a fixed cable anchor.
    public sealed class ControllerConnector : Control
    {
        const float HorizontalScale = 1.6f, HorizontalPlugLength = 24 * HorizontalScale;
        const float PlugWidth = 22, TipWidth = 18, SocketWidth = 24, SocketBorder = 2;
        // The shell holes and socket catches share their transverse axes before
        // the common rotation/scale, with a compact gap around the centerline.
        static float RetentionAxis(int index)
        { return (index == 0 ? -1 : 1) * (SocketWidth - 2 * SocketBorder) * .18f; }
        readonly ToolTip tooltip = new ToolTip();
        bool connected, connecting, armed, dragging, gestureConnected, gestureMoved;
        bool requestingConnection, requestedConnection;
        bool vertical, compact, pointerInside;
        bool spaceDown, enterDown;
        int pressedPart, hoveredPart;
        Point origin, pointer;
        float initialAxis, movingAxis;
        string unavailable, controllerLabel, controllerName;
        Color accentColor = ModernTheme.Accent;
        public event Action<bool> ConnectionRequested;

        public bool Vertical
        {
            get { return vertical; }
            set { if (vertical == value) return; CancelGesture(); vertical = value; RefreshLanguage(); RefreshPointer(); }
        }
        public bool Compact
        {
            get { return compact; }
            set { if (compact == value) return; CancelGesture(); compact = value; Invalidate(); }
        }
        public string ControllerLabel
        {
            get { return controllerLabel; }
            set { if (controllerLabel == value) return; CancelGesture(); controllerLabel = value; RefreshLanguage(); }
        }
        public string ControllerName
        {
            get { return controllerName; }
            set { if (controllerName == value) return; controllerName = value; RefreshLanguage(); }
        }
        public Color AccentColor
        {
            get { return accentColor; }
            set { if (accentColor == value) return; accentColor = value; Invalidate(); }
        }

        public ControllerConnector()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Height = 94; TabStop = true; BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Foreground;
            AccessibleRole = AccessibleRole.PushButton; RefreshLanguage();
        }
        public void SetConnection(bool isConnected, string unavailableReason)
        { SetConnection(isConnected, unavailableReason, false); }
        public void SetConnection(bool isConnected, string unavailableReason, bool isConnecting)
        {
            isConnecting = isConnecting && !isConnected;
            if (connected == isConnected && connecting == isConnecting && unavailable == unavailableReason) return;
            CancelGesture(); connected = isConnected; connecting = isConnecting; unavailable = unavailableReason;
            RefreshLanguage(); RefreshPointer(); AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
        bool ConnectionEngaged { get { return connected || connecting; } }
        string ActionText { get { return connecting ? UiText.Get("Verbindung abbrechen", "Cancel connection") : connected ? UiText.Get("Controller trennen", "Disconnect controller") : UiText.Get("Controller verbinden", "Connect controller"); } }
        string StateText { get { return connecting ? UiText.Get("Verbindung wird hergestellt…", "Connecting…") : connected ? UiText.Get("Verbunden", "Connected") : unavailable == null ? UiText.Get("Nicht verbunden", "Disconnected") : UiText.Get("Noch nicht bereit", "Not ready yet"); } }
        string HintText
        {
            get
            {
                return UiText.Get("Stecker oder Buchse anklicken. Stecker zum Verbinden hineinschieben, zum Trennen herausziehen. Esc bricht ab.",
                    "Click the plug or socket. Slide the plug in to connect, pull it out to disconnect. Esc cancels.");
            }
        }
        public void RefreshLanguage()
        {
            string name = String.IsNullOrEmpty(controllerName) ? "" : controllerName + " · ";
            AccessibleName = name + ActionText;
            AccessibleDescription = StateText + ". " + HintText + (unavailable == null ? "" : "\n" + unavailable);
            tooltip.SetToolTip(this, name + AccessibleDescription); Invalidate();
        }

        float CenterLine { get { return Math.Min(ClientSize.Height / 2f, 40); } }
        RectangleF SocketBounds
        {
            get { return Vertical ? new RectangleF((ClientSize.Width - SocketWidth) / 2f, 2, SocketWidth, 12) : new RectangleF(204, CenterLine - SocketWidth * HorizontalScale / 2, 12 * HorizontalScale, SocketWidth * HorizontalScale); }
        }
        RectangleF SocketOpeningBounds
        {
            get { float border = Vertical ? SocketBorder : SocketBorder * HorizontalScale; return RectangleF.Inflate(SocketBounds, -border, -border); }
        }
        PointF CableAnchor { get { return Vertical ? new PointF(ClientSize.Width / 2f, ClientSize.Height - 1) : new PointF(16, CenterLine); } }
        // The axis increases toward the socket in either orientation.
        float RestAxis { get { return Vertical ? -Math.Max(SocketBounds.Bottom + 18, ClientSize.Height - 32) : 70; } }
        float DockAxis { get { return Vertical ? -SocketBounds.Bottom : SocketBounds.Left - HorizontalPlugLength; } }
        float ConnectTolerance { get { return Vertical ? Math.Min(8, Math.Abs(DockAxis - RestAxis) * .30f) : 16; } }
        float DisconnectTolerance { get { return Vertical ? Math.Min(12, Math.Abs(DockAxis - RestAxis) * .65f) : 28; } }
        RectangleF PlugAt(float axis)
        { return Vertical ? new RectangleF((ClientSize.Width - PlugWidth) / 2f, -axis, PlugWidth, 24) : new RectangleF(axis, CenterLine - PlugWidth * HorizontalScale / 2, HorizontalPlugLength, PlugWidth * HorizontalScale); }
        RectangleF PlugBounds { get { return PlugAt(dragging ? movingAxis : (requestingConnection ? requestedConnection : ConnectionEngaged) ? DockAxis : RestAxis); } }
        RectangleF TipBounds(RectangleF plug)
        {
            return Vertical ? new RectangleF(plug.Left + (PlugWidth - TipWidth) / 2, plug.Top - 15, TipWidth, 16) : new RectangleF(plug.Right - HorizontalScale, plug.Top + (PlugWidth - TipWidth) * HorizontalScale / 2, 16 * HorizontalScale, TipWidth * HorizontalScale);
        }
        RectangleF VisibleTipBounds(RectangleF plug)
        {
            RectangleF tip = TipBounds(plug);
            if (Vertical)
            {
                float top = Math.Max(tip.Top, SocketOpeningBounds.Top);
                return new RectangleF(tip.Left, top, tip.Width, Math.Max(0, tip.Bottom - top));
            }
            return new RectangleF(tip.Left, tip.Top, Math.Max(0, Math.Min(tip.Right, SocketOpeningBounds.Right) - tip.Left), tip.Height);
        }
        bool SocketHighlighted { get { return dragging && WouldDock(movingAxis); } }
        bool WouldDock(float axis) { return axis >= DockAxis - (gestureConnected ? DisconnectTolerance : ConnectTolerance); }
        float PointerAxis(Point point) { return Vertical ? -point.Y : point.X; }
        float ClampedAxis(Point point)
        {
            float minimum = Vertical ? -(ClientSize.Height - 30) : CableAnchor.X + 16;
            return Math.Max(minimum, Math.Min(DockAxis, initialAxis + PointerAxis(point) - PointerAxis(origin)));
        }
        int HitPart(Point point)
        {
            // Metal remains visible inside the opening until its far inner edge.
            // Only the visible portion can be picked up; the covered tip cannot.
            if (PlugBounds.Contains(point) || VisibleTipBounds(PlugBounds).Contains(point)) return 1;
            return SocketBounds.Contains(point) ? 2 : 0;
        }
        void TrackPointer(Point point)
        {
            pointer = point; pointerInside = ClientRectangle.Contains(point);
        }
        void RefreshPointer()
        {
            int part = Enabled && Visible && pointerInside && ClientRectangle.Contains(pointer) ? HitPart(pointer) : 0;
            if (part != hoveredPart) { hoveredPart = part; Invalidate(); }
            Cursor next = !Enabled || !Visible ? Cursors.Default : dragging ? DragCursors.Grabbing :
                armed ? DragCursors.Grabbing : part == 0 ? Cursors.Default : DragCursors.Grab;
            if (Cursor != next) Cursor = next;
        }
        void Request(bool value)
        {
            if (requestingConnection) return;
            Action<bool> handler = ConnectionRequested;
            if (!Enabled || value == ConnectionEngaged || handler == null) { CancelGesture(); return; }
            // Keep the released plug at its requested destination before
            // releasing capture. SetConnection supplies the pending state across
            // asynchronous callbacks and confirms the actual runtime connection.
            requestingConnection = true; requestedConnection = value;
            try
            {
                CancelGesture();
                if (IsDisposed || Disposing || !Enabled || value == ConnectionEngaged) return;
                Invalidate(); Update();
                if (!IsDisposed && !Disposing && Enabled && value != ConnectionEngaged) handler(value);
            }
            finally
            {
                requestingConnection = false;
                if (!IsDisposed && !Disposing) { RefreshPointer(); Invalidate(); }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            TrackPointer(e.Location); RefreshPointer();
            if (e.Button != MouseButtons.Left || !Enabled || requestingConnection) return;
            int part = HitPart(e.Location); if (part == 0) return;
            Focus(); origin = e.Location; gestureConnected = ConnectionEngaged; pressedPart = part;
            initialAxis = movingAxis = ConnectionEngaged ? DockAxis : RestAxis;
            armed = true; dragging = gestureMoved = false; Capture = true; RefreshPointer();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            TrackPointer(e.Location);
            if (!armed)
            {
                RefreshPointer();
                return;
            }
            if ((e.Button & MouseButtons.Left) == 0 || !Capture) { CancelGesture(); return; }
            Size threshold = SystemInformation.DragSize;
            if (Math.Abs(e.X - origin.X) >= threshold.Width || Math.Abs(e.Y - origin.Y) >= threshold.Height) gestureMoved = true;
            if (!dragging && pressedPart == 1 && Math.Abs(PointerAxis(e.Location) - PointerAxis(origin)) >= (Vertical ? threshold.Height : threshold.Width)) dragging = true;
            if (dragging)
            {
                float next = ClampedAxis(e.Location);
                if (next != movingAxis) { movingAxis = next; Invalidate(); }
            }
            RefreshPointer();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            TrackPointer(e.Location); RefreshPointer();
            if (e.Button != MouseButtons.Left || !armed) return;
            bool sameState = ConnectionEngaged == gestureConnected;
            bool send = false, next = ConnectionEngaged;
            if (dragging) { next = WouldDock(ClampedAxis(e.Location)); send = next != gestureConnected; }
            else if (!gestureMoved)
            {
                Size threshold = SystemInformation.DragSize;
                bool still = Math.Abs(e.X - origin.X) < threshold.Width && Math.Abs(e.Y - origin.Y) < threshold.Height;
                RectangleF hit = pressedPart == 1 ? RectangleF.Union(PlugBounds, VisibleTipBounds(PlugBounds)) : SocketBounds;
                send = still && RectangleF.Inflate(hit, 4, 4).Contains(e.Location); next = !gestureConnected;
            }
            if (sameState && send) Request(next);
            else CancelGesture();
        }
        void CancelGesture()
        {
            bool changed = armed || dragging; armed = dragging = gestureMoved = false; pressedPart = 0;
            if (IsHandleCreated && Capture) Capture = false;
            RefreshPointer();
            if (changed) Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); TrackPointer(PointToClient(MousePosition)); RefreshPointer(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pointerInside = false; RefreshPointer(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) CancelGesture(); }
        void ResetKeyboardPress() { spaceDown = enterDown = false; }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { CancelGesture(); ResetKeyboardPress(); } RefreshPointer(); Invalidate(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) { CancelGesture(); ResetKeyboardPress(); } RefreshPointer(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); CancelGesture(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); CancelGesture(); ResetKeyboardPress(); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData)
        { return keyData == Keys.Space || keyData == Keys.Enter || keyData == Keys.Escape || base.IsInputKey(keyData); }
        protected override bool ProcessKeyEventArgs(ref Message message)
        {
            // After focus/enable changes a held key can arrive without its
            // initial down. Windows marks those repeats with previous-state bit 30.
            int key = message.WParam.ToInt32();
            if ((message.Msg == 0x0100 || message.Msg == 0x0104) && (key == (int)Keys.Space || key == (int)Keys.Enter) &&
                (message.LParam.ToInt64() & (1L << 30)) != 0) return true;
            return base.ProcessKeyEventArgs(ref message);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { CancelGesture(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                bool repeated = e.KeyCode == Keys.Space ? spaceDown : enterDown;
                if (e.KeyCode == Keys.Space) spaceDown = true; else enterDown = true;
                // Remember modified presses too: releasing Ctrl/Alt/Shift while
                // keeping the activation key held must not cause a later toggle.
                if (e.Modifiers != Keys.None) return;
                if (!repeated) { CancelGesture(); Request(!ConnectionEngaged); }
                e.Handled = true;
            }
        }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.KeyChar == ' ' || e.KeyChar == '\r') e.Handled = true;
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) spaceDown = false;
            else if (e.KeyCode == Keys.Enter) enterDown = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = !Enabled ? ModernTheme.Muted : connecting ? Color.FromArgb(255, 198, 92) : connected ? Color.FromArgb(121, 200, 175) : AccentColor;
            RectangleF socket = SocketBounds, plug = PlugBounds;
            DrawSocket(graphics, socket, ink);
            PointF end = Vertical ? new PointF(plug.Left + plug.Width / 2, plug.Bottom) : new PointF(plug.Left, plug.Top + plug.Height / 2);
            using (Pen cable = new Pen(Color.FromArgb(12, 14, 19), Vertical ? 4 : 4 * HorizontalScale) { StartCap = LineCap.Round })
                graphics.DrawLine(cable, CableAnchor, end);
            using (Pen cable = new Pen(Color.FromArgb(57, 61, 70), Vertical ? 2.5f : 2.5f * HorizontalScale) { StartCap = LineCap.Round })
                graphics.DrawLine(cable, CableAnchor, end);
            PointF highlightStart = CableAnchor, highlightEnd = end;
            if (Vertical) { highlightStart.X -= .6f; highlightEnd.X -= .6f; }
            else { highlightStart.Y -= .6f * HorizontalScale; highlightEnd.Y -= .6f * HorizontalScale; }
            using (Pen highlight = new Pen(Color.FromArgb(95, 100, 111), Vertical ? .6f : .6f * HorizontalScale)) graphics.DrawLine(highlight, highlightStart, highlightEnd);
            DrawPlug(graphics, plug, ink);
            if (Vertical)
            {
                if (!String.IsNullOrEmpty(ControllerLabel)) TextRenderer.DrawText(graphics, ControllerLabel, Font,
                    new Rectangle(0, 0, (int)socket.Left, 15), ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            else
            {
                Rectangle caption = new Rectangle(252, Compact ? 7 : 15, Math.Max(0, ClientSize.Width - 260), 25);
                string captionText = Compact && ClientSize.Width < 430 ? connecting ? UiText.Get("Abbrechen", "Cancel") : connected ? UiText.Get("Trennen", "Disconnect") : UiText.Get("Verbinden", "Connect") : ActionText;
                TextRenderer.DrawText(graphics, captionText, Font, caption, Enabled ? ForeColor : ModernTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                Rectangle hint = new Rectangle(caption.Left, Compact ? 33 : 43, caption.Width, Compact ? 22 : 42);
                TextRenderer.DrawText(graphics, Compact ? StateText : HintText, Font, hint, ModernTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (Compact ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak));
                if (!Compact) TextRenderer.DrawText(graphics, StateText, Font, new Rectangle(16, 65, 223, 20), ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ink, BackColor);
        }
        void DrawSocket(Graphics graphics, RectangleF socket, Color ink)
        {
            if (Vertical) { DrawSocketFace(graphics, socket, ink); return; }
            GraphicsState saved = graphics.Save();
            try
            {
                // Reuse the small connector face, rotated and uniformly enlarged.
                graphics.TranslateTransform(socket.Right, socket.Top);
                graphics.RotateTransform(90);
                graphics.ScaleTransform(HorizontalScale, HorizontalScale);
                DrawSocketFace(graphics, new RectangleF(0, 0, SocketWidth, 12), ink);
            }
            finally { graphics.Restore(saved); }
        }
        void DrawPlug(Graphics graphics, RectangleF plug, Color color)
        {
            if (Vertical) { DrawVerticalPlug(graphics, plug, color, SocketOpeningBounds.Top); return; }
            GraphicsState saved = graphics.Save();
            try
            {
                graphics.TranslateTransform(plug.Right, plug.Top);
                graphics.RotateTransform(90);
                graphics.ScaleTransform(HorizontalScale, HorizontalScale);
                DrawVerticalPlug(graphics, new RectangleF(0, 0, PlugWidth, 24), color,
                    (plug.Right - SocketOpeningBounds.Right) / HorizontalScale);
            }
            finally { graphics.Restore(saved); }
        }
        void DrawSocketFace(Graphics graphics, RectangleF socket, Color ink)
        {
            bool ready = SocketHighlighted && Enabled;
            if (ready)
            {
                using (GraphicsPath glow = Rounded(RectangleF.Inflate(socket, 3, 2), 4))
                using (Brush light = new SolidBrush(Color.FromArgb(38, AccentColor))) graphics.FillPath(light, glow);
            }
            RectangleF shadow = socket; shadow.Offset(0, 1);
            using (GraphicsPath recess = Rounded(RectangleF.Inflate(shadow, 1, 1), 2))
            using (Brush darkness = new SolidBrush(Color.FromArgb(8, 10, 14))) graphics.FillPath(darkness, recess);
            using (GraphicsPath rim = Rounded(socket, 1.5f))
            using (LinearGradientBrush metal = Material(socket, Color.FromArgb(176, 183, 193), Color.FromArgb(89, 97, 109), Color.FromArgb(38, 43, 52)))
            using (Pen outline = new Pen(ready || hoveredPart == 2 ? AccentColor : ConnectionEngaged ? ink : Color.FromArgb(113, 121, 134), ready ? 1.5f : .8f))
            { graphics.FillPath(metal, rim); graphics.DrawPath(outline, rim); }
            RectangleF opening = RectangleF.Inflate(socket, -SocketBorder, -SocketBorder);
            using (Brush cavity = new SolidBrush(Color.FromArgb(5, 7, 10))) graphics.FillRectangle(cavity, opening);
            using (Pen lip = new Pen(Color.FromArgb(28, 32, 40), 1))
                graphics.DrawLine(lip, opening.Left, opening.Top + .5f, opening.Right, opening.Top + .5f);
            RectangleF tongue = new RectangleF(opening.Left + (2), opening.Top + opening.Height * .55f,
                opening.Width - (4), 2.5f);
            using (Brush plastic = new SolidBrush(Color.FromArgb(48, 54, 65))) graphics.FillRectangle(plastic, tongue);
            using (Pen ridge = new Pen(Color.FromArgb(90, 96, 105), .7f)) graphics.DrawLine(ridge, tongue.Left, tongue.Top, tongue.Right, tongue.Top);
            using (Brush contact = new SolidBrush(Color.FromArgb(195, 173, 114)))
                for (int i = 0; i < 4; i++) graphics.FillRectangle(contact, tongue.Left + (i + .5f) * tongue.Width / 4 - (.7f), tongue.Top + .4f, 1.4f, 1.5f);
            using (Pen catches = new Pen(Color.FromArgb(142, 149, 159), .7f))
            {
                for (int i = 0; i < 2; i++)
                {
                    float axis = opening.Left + opening.Width / 2 + RetentionAxis(i), direction = i == 0 ? 1 : -1;
                    graphics.DrawLine(catches, axis - direction, opening.Top, axis + direction, opening.Top + 1);
                }
            }
        }
        void DrawVerticalPlug(Graphics graphics, RectangleF plug, Color color, float farInsideEdge)
        {
            GraphicsState saved = graphics.Save();
            float length = plug.Height, breadth = plug.Width;
            try
            {
                // Both sizes use the same material and silhouette. In local
                // coordinates the cable enters at x=0 and the tip points right.
                graphics.TranslateTransform(plug.Left + plug.Width / 2, plug.Bottom); graphics.RotateTransform(-90);
                float tipLength = 16, tipBreadth = TipWidth;
                RectangleF tip = new RectangleF(length - 1, -tipBreadth / 2, tipLength, tipBreadth);
                GraphicsState tipState = graphics.Save();
                // Keep the inserted shell visible through the opening. The far
                // inner rim, rather than the entrance rim, hides its leading end.
                float mouth = plug.Bottom - farInsideEdge;
                graphics.SetClip(new RectangleF(tip.Left - 1, tip.Top - 1, Math.Max(0, mouth - tip.Left + 1), tip.Height + 2), CombineMode.Intersect);
                try
                {
                using (LinearGradientBrush steel = Material(tip, Color.FromArgb(222, 227, 232), Color.FromArgb(157, 166, 177), Color.FromArgb(85, 94, 108)))
                using (Pen edge = new Pen(Color.FromArgb(58, 66, 78), .8f))
                { graphics.FillRectangle(steel, tip); graphics.DrawRectangle(edge, tip.X, tip.Y, tip.Width, tip.Height); }
                using (Pen bevel = new Pen(Color.FromArgb(243, 246, 249), .8f))
                    graphics.DrawLine(bevel, tip.Left + 1, tip.Top + .8f, tip.Right - 1, tip.Top + .8f);
                using (Pen grain = new Pen(Color.FromArgb(25, 255, 255, 255), .6f))
                    for (float y = tip.Top + 3; y < tip.Bottom - 1; y += 3) graphics.DrawLine(grain, tip.Left + 1, y, tip.Right - 1, y);
                float holeLength = 4, holeWidth = 2.5f;
                for (int i = 0; i < 2; i++)
                {
                    RectangleF hole = new RectangleF(tip.Right - tipLength * .56f, RetentionAxis(i) - holeWidth / 2, holeLength, holeWidth);
                    using (GraphicsPath cutout = Rounded(hole, .6f))
                    using (Brush dark = new SolidBrush(Color.FromArgb(35, 41, 50)))
                    using (Pen lowerEdge = new Pen(Color.FromArgb(199, 207, 218), .6f))
                    { graphics.FillPath(dark, cutout); graphics.DrawLine(lowerEdge, hole.Left, hole.Bottom, hole.Right, hole.Bottom); }
                }
                using (Pen foldedEdge = new Pen(Color.FromArgb(113, 122, 135), .8f))
                    graphics.DrawLine(foldedEdge, tip.Right - 2, tip.Top + 1, tip.Right - 2, tip.Bottom - 1);
                }
                finally { graphics.Restore(tipState); }

                float bootLength = 5;
                RectangleF boot = new RectangleF(-bootLength, -breadth * .23f, bootLength + 5, breadth * .46f);
                using (GraphicsPath bootShape = Rounded(boot, 1.4f))
                using (LinearGradientBrush rubber = Material(boot, Color.FromArgb(59, 64, 73), Color.FromArgb(31, 35, 43), Color.FromArgb(17, 20, 26)))
                using (Pen edge = new Pen(Color.FromArgb(11, 14, 19), .8f))
                { graphics.FillPath(rubber, bootShape); graphics.DrawPath(edge, bootShape); }
                using (Pen rib = new Pen(Color.FromArgb(15, 18, 24), .8f))
                    for (float x = -bootLength + 2; x < 0; x += 2) graphics.DrawLine(rib, x, boot.Top + 1, x, boot.Bottom - 1);

                RectangleF body = new RectangleF(0, -breadth / 2, length, breadth);
                using (GraphicsPath silhouette = PlugSilhouette(length, breadth))
                using (LinearGradientBrush plastic = Material(body, Color.FromArgb(66, 70, 80), Color.FromArgb(36, 40, 49), Color.FromArgb(19, 22, 29)))
                using (Pen edge = new Pen(Color.FromArgb(9, 12, 17), 1))
                { graphics.FillPath(plastic, silhouette); graphics.DrawPath(edge, silhouette); }
                using (Pen bevel = new Pen(Color.FromArgb(103, 109, 122), .7f))
                    graphics.DrawLine(bevel, length * .25f, body.Top + 1.4f, length - 3, body.Top + 1.4f);
                using (Pen collar = new Pen(Color.FromArgb(16, 20, 27), 1))
                    graphics.DrawLine(collar, length - 3, body.Top + 2, length - 3, body.Bottom - 2);
                // A small colored collar identifies the controller; the casing
                // itself stays graphite rather than looking like a neon button.
                using (Pen identity = new Pen(Color.FromArgb(175, color), 1.1f))
                    graphics.DrawLine(identity, 3, -breadth * .27f, 3, breadth * .27f);
            }
            finally { graphics.Restore(saved); }
            DrawUsbMark(graphics, plug);
        }
        static GraphicsPath PlugSilhouette(float length, float breadth)
        {
            float half = breadth / 2, corner = Math.Min(2.5f, breadth * .1f);
            var path = new GraphicsPath();
            path.AddBezier(0, -half * .55f, 0, -half * .85f, length * .15f, -half, length * .27f, -half);
            path.AddLine(length * .27f, -half, length - corner, -half);
            path.AddArc(length - corner * 2, -half, corner * 2, corner * 2, 270, 90);
            path.AddLine(length, -half + corner, length, half - corner);
            path.AddArc(length - corner * 2, half - corner * 2, corner * 2, corner * 2, 0, 90);
            path.AddLine(length - corner, half, length * .27f, half);
            path.AddBezier(length * .27f, half, length * .15f, half, 0, half * .85f, 0, half * .55f);
            path.CloseFigure(); return path;
        }
        static LinearGradientBrush Material(RectangleF bounds, Color top, Color middle, Color bottom)
        {
            var brush = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
            brush.InterpolationColors = new ColorBlend { Colors = new[] { top, middle, bottom }, Positions = new[] { 0f, .45f, 1f } };
            return brush;
        }
        static void DrawUsbMark(Graphics graphics, RectangleF plug)
        {
            float x = plug.Left + plug.Width / 2, y = plug.Top + 5;
            Color emboss = Color.FromArgb(155, 165, 180);
            using (Pen line = new Pen(emboss, .85f))
            using (Brush fill = new SolidBrush(emboss))
            {
                graphics.DrawLine(line, x, y + 12, x, y + 1);
                graphics.DrawLines(line, new[] { new PointF(x, y + 8), new PointF(x - 4, y + 5), new PointF(x - 4, y + 3) });
                graphics.DrawLines(line, new[] { new PointF(x, y + 10), new PointF(x + 4, y + 7), new PointF(x + 4, y + 5) });
                graphics.FillPolygon(fill, new[] { new PointF(x, y), new PointF(x - 1.8f, y + 2.8f), new PointF(x + 1.8f, y + 2.8f) });
                graphics.FillEllipse(fill, x - 5.2f, y + 1.8f, 2.4f, 2.4f);
                graphics.FillRectangle(fill, x + 2.8f, y + 3.8f, 2.4f, 2.4f);
                graphics.FillEllipse(fill, x - 1.5f, y + 11, 3, 3);
            }
        }
        static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath(); float diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ConnectorAccessibleObject(this); }
        sealed class ConnectorAccessibleObject : ControlAccessibleObject
        {
            readonly ControllerConnector owner;
            public ConnectorAccessibleObject(ControllerConnector owner) : base(owner) { this.owner = owner; }
            public override string DefaultAction { get { return owner.ActionText; } }
            public override string Value { get { return owner.StateText; } set { } }
            public override AccessibleStates State { get { return base.State | (owner.connecting ? AccessibleStates.Busy : AccessibleStates.None); } }
            public override void DoDefaultAction() { if (!owner.IsDisposed) owner.Request(!owner.ConnectionEngaged); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { CancelGesture(); tooltip.Dispose(); } base.Dispose(disposing); }
    }
}
