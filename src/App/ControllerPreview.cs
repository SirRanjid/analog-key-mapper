using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    // Calculated output only. Style changes presentation; the application owns
    // virtual output selection, OLE, and any devices. No input is synthesized.
    public sealed partial class ControllerPreview : Control
    {
        struct Snapshot
        {
            public double LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
            public ushort Buttons;
            public bool HasInput, OutputEnabled;
            public string Error, Warning;
            public int UnavailableKeys;
            public bool SameAs(Snapshot other)
            {
                return LeftX == other.LeftX && LeftY == other.LeftY && RightX == other.RightX && RightY == other.RightY &&
                    LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger && Buttons == other.Buttons &&
                    HasInput == other.HasInput && OutputEnabled == other.OutputEnabled && Error == other.Error &&
                    Warning == other.Warning && UnavailableKeys == other.UnavailableKeys;
            }
        }
        struct StatusLayout { public Rectangle Main, Detail, Target; public int FigureHeight; }
        public bool CompactStatus { get; set; }
        struct Viewport
        {
            public float X, Y, Scale;
            public PointF ToLayout(Point point) { return new PointF((point.X - X) / Scale, (point.Y - Y) / Scale); }
            public Rectangle ToClient(RectangleF bounds)
            { return Rectangle.Ceiling(new RectangleF(X + bounds.X * Scale, Y + bounds.Y * Scale, bounds.Width * Scale, bounds.Height * Scale)); }
        }
        const ushort SupportedButtons = 0xF3FF;
        readonly ToolTip tooltip = new ToolTip();
        Snapshot current;
        ControllerLayout layout = new ControllerLayout(ControllerStyle.Xbox);
        OutputTarget? hoverTarget, dropTarget, pressedTarget, draggingTarget;
        readonly HashSet<OutputTarget> availableTargets = new HashSet<OutputTarget>();
        static readonly Color PartnerColor = MappingDragFeedback.AvailableColor;
        Point pressOrigin, pointerPosition;
        bool pointerInside, dragDispatching;
        public event Action<OutputTarget> TargetSelected;
        public event Action<OutputTarget> TargetDragRequested;

        public ControllerPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Foreground;
            Size = new Size(440, 295); TabStop = true;
            AccessibleName = UiText.Get("Controller-Vorschau", "Controller preview"); AccessibleRole = AccessibleRole.Graphic;
            tooltip.InitialDelay = 500; tooltip.AutoPopDelay = 15000;
        }
        public ControllerStyle Style
        {
            get { return layout.Style; }
            set
            {
                ControllerPresentation.Validate(value); if (value == layout.Style) return;
                CancelGesture(); hoverTarget = dropTarget = null; availableTargets.Clear(); layout = new ControllerLayout(value);
                LayoutKeyAssignments();
                RefreshPointer(); UpdateTooltip(); Invalidate(); AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }
        }
        public OutputTarget? HitTestTarget(Point clientPoint)
        {
            if (IsDisposed || !Enabled || !IsHandleCreated || !ClientRectangle.Contains(clientPoint) || ClientSize.Width < 24 || ClientSize.Height < 80) return null;
            using (Graphics graphics = CreateGraphics())
            using (Font font = StatusFont())
            {
                StatusLayout status = MeasureStatus(graphics, font); Viewport viewport = GetViewport(status.FigureHeight);
                if (viewport.Scale <= 0 || clientPoint.Y >= status.FigureHeight) return null;
                PointF point = viewport.ToLayout(clientPoint);
                // Badges are painted last. Their visible keycaps own the hit,
                // including where a rounded edge reaches a controller region.
                for (int index = layout.Regions.Count - 1; index >= 0; index--)
                {
                    ControllerRegion region = layout.Regions[index];
                    KeyAssignmentLabel label;
                    if (!keyAssignments.TryGetValue(region.Target, out label)) continue;
                    RectangleF bounds = KeyAssignmentBounds(region, label);
                    if (bounds.Contains(point)) using (GraphicsPath badge = ControllerLayout.Rounded(bounds, 3))
                        if (badge.IsVisible(point)) return region.Target;
                }
                return layout.HitTest(point);
            }
        }
        internal MappingDragVisual CreateDragVisual(OutputTarget target)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || ClientSize.Width < 24 || ClientSize.Height < 80) return null;
            ControllerRegion region = layout.Find(target); if (region == null) return null;
            using (var source = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                // Read our current drawing, including live fill and the actual
                // Xbox/PlayStation legends. No screen pixels or UI state change.
                DrawToBitmap(source, ClientRectangle);
                Viewport viewport;
                using (Graphics graphics = Graphics.FromImage(source))
                using (Font font = StatusFont()) viewport = GetViewport(MeasureStatus(graphics, font).FigureHeight);
                if (viewport.Scale <= 0) return null;
                bool drop = dropTarget == target, hover = !dropTarget.HasValue && hoverTarget == target;
                float border = drop ? 2.5f : (hover ? 1.8f : availableTargets.Contains(target) ? 1.4f : 1f) * viewport.Scale;
                using (GraphicsPath path = CreateDragTargetPath(region))
                using (var transform = new Matrix(viewport.Scale, 0, 0, viewport.Scale, viewport.X, viewport.Y))
                {
                    path.Transform(transform);
                    RectangleF ink = RectangleF.Inflate(path.GetBounds(), border / 2 + 1, border / 2 + 1);
                    Rectangle crop = Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB((int)Math.Floor(ink.Left),
                        (int)Math.Floor(ink.Top), (int)Math.Ceiling(ink.Right), (int)Math.Ceiling(ink.Bottom)));
                    if (crop.Width < 1 || crop.Height < 1) return null;
                    using (var mask = new Bitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics graphics = Graphics.FromImage(mask))
                        using (Pen edge = new Pen(Color.White, border) { DashStyle = drop ? DashStyle.Dash : DashStyle.Solid })
                        {
                            graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.TranslateTransform(-crop.Left, -crop.Top);
                            graphics.FillPath(Brushes.White, path); graphics.DrawPath(edge, path);
                        }
                        return MappingDragVisual.Extract(source, mask, crop, new Point(pressOrigin.X - crop.Left, pressOrigin.Y - crop.Top));
                    }
                }
            }
        }
        public void SetDropTarget(OutputTarget? target)
        {
            if (target.HasValue && layout.Find(target.Value) == null) throw new ArgumentOutOfRangeException("target");
            if (dropTarget == target && (target.HasValue || !hoverTarget.HasValue)) return;
            dropTarget = target; if (!target.HasValue) hoverTarget = null;
            RefreshPointer(); UpdateTooltip(); Invalidate();
        }
        public void SetAvailableTargets(IEnumerable<OutputTarget> targets)
        {
            if (targets == null) throw new ArgumentNullException("targets");
            var next = new HashSet<OutputTarget>();
            foreach (OutputTarget target in targets)
            { if (layout.Find(target) == null) throw new ArgumentOutOfRangeException("targets"); next.Add(target); }
            if (availableTargets.SetEquals(next)) return;
            availableTargets.Clear(); availableTargets.UnionWith(next); Invalidate();
        }
        // UI-thread call. Only immutable scalar fields are retained from the frame.
        public void SetFrame(ControllerFrame frame, bool inputAvailable, bool outputEnabled)
        {
            if (IsDisposed) return;
            if (IsHandleCreated && InvokeRequired) throw new InvalidOperationException("Update the controller preview on its UI thread.");
            Snapshot next = new Snapshot { OutputEnabled = outputEnabled };
            if (frame != null && frame.Errors.Count != 0) next.Error = CleanError(frame.Errors[0]);
            else if (frame != null && (!Axis(frame.LeftX) || !Axis(frame.LeftY) || !Axis(frame.RightX) || !Axis(frame.RightY) ||
                !Trigger(frame.LeftTrigger) || !Trigger(frame.RightTrigger) || (frame.Buttons & ~SupportedButtons) != 0))
                next.Error = UiText.Get("Ungültige berechnete Controllerwerte.", "Invalid calculated controller values.");
            else if (inputAvailable && frame != null)
            {
                next.HasInput = true;
                next.LeftX = frame.LeftX; next.LeftY = frame.LeftY; next.RightX = frame.RightX; next.RightY = frame.RightY;
                next.LeftTrigger = frame.LeftTrigger; next.RightTrigger = frame.RightTrigger; next.Buttons = frame.Buttons;
            }
            if (current.SameAs(next)) return; current = next; UpdateTooltip(); Invalidate();
        }
        // PreviewSnapshot is intentionally not a ControllerFrame. Missing keys
        // may leave useful preview values, but can never authorize real output.
        bool keyboardMode;
        bool unseenInputsOnly;
        public bool UnseenInputsOnly
        {
            get { return unseenInputsOnly; }
            set { if (unseenInputsOnly == value) return; unseenInputsOnly = value; UpdateTooltip(); Invalidate(); }
        }
        string missingInputLabels;
        public string MissingInputLabels
        {
            get { return missingInputLabels; }
            set { if (missingInputLabels == value) return; missingInputLabels = value; UpdateTooltip(); Invalidate(); }
        }
        public bool KeyboardMode
        {
            get { return keyboardMode; }
            set { if (keyboardMode == value) return; keyboardMode = value; UpdateTooltip(); Invalidate(); }
        }
        public void SetPreview(PreviewSnapshot preview, bool inputAvailable, bool outputEnabled)
        {
            if (IsDisposed) return;
            if (IsHandleCreated && InvokeRequired) throw new InvalidOperationException("Update the controller preview on its UI thread.");
            Snapshot next = new Snapshot { OutputEnabled = outputEnabled };
            if (keyboardMode) next.HasInput = true;
            else if (preview != null && (!Axis(preview.LeftX) || !Axis(preview.LeftY) || !Axis(preview.RightX) || !Axis(preview.RightY) ||
                !Trigger(preview.LeftTrigger) || !Trigger(preview.RightTrigger) || (preview.Buttons & ~SupportedButtons) != 0))
                next.Error = UiText.Get("Ungültige berechnete Controllerwerte.", "Invalid calculated controller values.");
            else if (inputAvailable && preview != null)
            {
                next.UnavailableKeys = preview.UnavailableKeys.Count;
                if (preview.HasValidInput)
                {
                    next.HasInput = true;
                    next.LeftX = preview.LeftX; next.LeftY = preview.LeftY; next.RightX = preview.RightX; next.RightY = preview.RightY;
                    next.LeftTrigger = preview.LeftTrigger; next.RightTrigger = preview.RightTrigger; next.Buttons = preview.Buttons;
                    if (preview.Errors.Count != 0 && !unseenInputsOnly) next.Warning = CleanError(preview.Errors[0]);
                }
                else if (preview.Errors.Count != 0 && !unseenInputsOnly) next.Error = CleanError(preview.Errors[0]);
            }
            if (current.SameAs(next)) return; current = next; UpdateTooltip(); Invalidate();
        }
        public bool HasValidInput { get { return current.HasInput; } }
        public string StatusText
        {
            get
            {
                if (keyboardMode) return current.OutputEnabled ? UiText.Get("Tastaturmodus · Controller verbunden", "Keyboard mode · controller connected") : UiText.Get("Tastaturmodus · Controller aus", "Keyboard mode · controller off");
                return current.OutputEnabled ? UiText.Get("Live-Vorschau · Controller-Ausgabe aktiv", "Live preview · controller output active")
                    : UiText.Get("Live-Vorschau · Controller-Ausgabe aus", "Live preview · controller output off");
            }
        }
        public string DetailText
        {
            get
            {
                if (keyboardMode) return UiText.Get("Controller bleibt neutral · Modusschalter zum Wechseln", "Controller stays neutral · use the mode switch");
                if (unseenInputsOnly) return UiText.Get("Weitere Tasten werden beim Drücken erkannt", "Other keys are detected when pressed");
                if (current.Error != null) return UiText.Get("Eingaben prüfen · Vorschau neutral", "Check inputs · preview neutral");
                if (current.HasInput && current.UnavailableKeys > 0 && !String.IsNullOrEmpty(missingInputLabels))
                    return UiText.Get("Aktuelle Druckwerte fehlen: ", "Current pressure values missing: ") + missingInputLabels;
                if (current.HasInput && current.UnavailableKeys > 0) return String.Format(CultureInfo.CurrentCulture,
                    UiText.Get("{0} Eingaben fehlen · verfügbare Werte werden angezeigt", "{0} inputs unavailable · available values are shown"), current.UnavailableKeys);
                if (current.HasInput && current.Warning != null) return UiText.Get("Einige Eingaben prüfen · verfügbare Werte werden angezeigt", "Check some inputs · available values are shown");
                return current.HasInput ? "" : UiText.Get("Wartet auf aktuelle Eingabewerte", "Waiting for current input values");
            }
        }
        string TargetText
        {
            get
            {
                OutputTarget? target = dropTarget ?? hoverTarget;
                return target.HasValue ? (dropTarget.HasValue ? UiText.Get("Ablegen: ", "Drop on: ") : "") + ControllerPresentation.Label(target.Value, Style) + AssignmentStatus(target.Value)
                    : UiText.Get("Ziel wählen oder eine Taste hierher ziehen", "Select an output or drag a key here");
            }
        }
        void UpdateTooltip()
        {
            string description = ControllerPresentation.Name(Style) + UiText.Get(" · Darstellung", " · presentation") + "\n" + TargetText + "\n" + StatusText;
            if (!String.IsNullOrEmpty(DetailText)) description += "\n" + DetailText;
            if (current.Error != null) description += "\n" + current.Error;
            if (current.Warning != null) description += "\n" + current.Warning;
            if (!String.Equals(tooltip.GetToolTip(this), description, StringComparison.Ordinal)) tooltip.SetToolTip(this, description);
        }
        void RememberPointer(Point point) { pointerPosition = point; pointerInside = ClientRectangle.Contains(point); }
        void RefreshPointer()
        {
            if (IsDisposed || Disposing) return;
            OutputTarget? target = !Enabled || !Visible ? null : dragDispatching ? draggingTarget :
                pressedTarget.HasValue ? pressedTarget : pointerInside ? HitTestTarget(pointerPosition) : null;
            if (target != hoverTarget) { hoverTarget = target; UpdateTooltip(); Invalidate(); }
            Cursor next = !Enabled || !Visible ? Cursors.Default : dragDispatching || pressedTarget.HasValue ? DragCursors.Grabbing :
                target.HasValue ? DragCursors.Grab : Cursors.Default;
            if (Cursor != next) Cursor = next;
        }
        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); RememberPointer(PointToClient(MousePosition)); RefreshPointer(); UpdateTooltip(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            RememberPointer(e.Location); base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) { RefreshPointer(); return; }
            CancelGesture(); OutputTarget? target = hoverTarget; if (!target.HasValue) return;
            Focus(); if (IsDisposed || !Enabled || !Visible || !Focused) return;
            pressedTarget = target; pressOrigin = e.Location; Capture = true; RefreshPointer();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            RememberPointer(e.Location); base.OnMouseMove(e); RefreshPointer();
            if (!Enabled || dragDispatching || !pressedTarget.HasValue) return;
            if ((e.Button & MouseButtons.Left) == 0 || !Capture) { CancelGesture(); return; }
            Size distance = SystemInformation.DragSize;
            Rectangle threshold = new Rectangle(pressOrigin.X - distance.Width / 2, pressOrigin.Y - distance.Height / 2, distance.Width, distance.Height);
            if (threshold.Contains(e.Location)) return;
            OutputTarget source = pressedTarget.Value;
            Action<OutputTarget> handler = TargetDragRequested;
            if (handler == null) { CancelGesture(); return; }
            // Keep the lifted source highlighted while OLE owns pointer updates,
            // including the one-off bitmap created by the drag callback.
            draggingTarget = source; dragDispatching = true; CancelGesture(); tooltip.Hide(this);
            // Clearing before OLE means neither a completed nor a cancelled drag
            // can also emit TargetSelected on the subsequent mouse-up.
            try { handler(source); }
            finally
            {
                dragDispatching = false; draggingTarget = null; CancelGesture();
                if (!IsDisposed)
                {
                    // OLE owns pointer updates until it returns to this control.
                    RememberPointer(PointToClient(MousePosition)); RefreshPointer(); Invalidate();
                }
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            RememberPointer(e.Location); base.OnMouseUp(e); if (e.Button != MouseButtons.Left) { RefreshPointer(); return; }
            OutputTarget? source = pressedTarget; CancelGesture();
            if (source.HasValue && HitTestTarget(e.Location) == source) SelectTarget(source.Value);
        }
        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); pointerInside = false; if (!Capture && !dragDispatching) CancelGesture(); RefreshPointer(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) CancelGesture(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); CancelGesture(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); CancelGesture(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { CancelGesture(); SetDropTarget(null); SetAvailableTargets(new OutputTarget[0]); } RefreshPointer(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) { CancelGesture(); SetDropTarget(null); SetAvailableTargets(new OutputTarget[0]); } RefreshPointer(); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape) { CancelGesture(); SetDropTarget(null); SetAvailableTargets(new OutputTarget[0]); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        void CancelGesture() { pressedTarget = null; if (IsHandleCreated && Capture) Capture = false; RefreshPointer(); }
        void SelectTarget(OutputTarget target) { Action<OutputTarget> handler = TargetSelected; if (handler != null) handler(target); }

        static bool Axis(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= -1 && value <= 1; }
        static bool Trigger(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 1; }
        static string CleanError(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return UiText.Get("Die Eingaben konnten nicht berechnet werden.", "The inputs could not be calculated.");
            StringBuilder result = new StringBuilder();
            foreach (char character in value) { result.Append(Char.IsControl(character) ? ' ' : character); if (result.Length >= 240) break; }
            string text = result.ToString().Trim();
            return text.Length == 0 ? UiText.Get("Die Eingaben konnten nicht berechnet werden.", "The inputs could not be calculated.") : text;
        }
        static string Percent(double value) { return Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%"; }
        double Amount(OutputTarget target)
        {
            if (!current.HasInput) return 0;
            switch (target)
            {
                case OutputTarget.LeftXPositive: return Math.Max(0, current.LeftX);
                case OutputTarget.LeftXNegative: return Math.Max(0, -current.LeftX);
                case OutputTarget.LeftYPositive: return Math.Max(0, current.LeftY);
                case OutputTarget.LeftYNegative: return Math.Max(0, -current.LeftY);
                case OutputTarget.RightXPositive: return Math.Max(0, current.RightX);
                case OutputTarget.RightXNegative: return Math.Max(0, -current.RightX);
                case OutputTarget.RightYPositive: return Math.Max(0, current.RightY);
                case OutputTarget.RightYNegative: return Math.Max(0, -current.RightY);
                case OutputTarget.LeftTrigger: return current.LeftTrigger;
                case OutputTarget.RightTrigger: return current.RightTrigger;
                default: return (current.Buttons & ControllerPresentation.ButtonMask(target)) != 0 ? 1 : 0;
            }
        }
        static Font StatusFont() { return new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point); }
        StatusLayout MeasureStatus(Graphics graphics, Font font)
        {
            int inset = Math.Min((ClientSize.Width - 1) / 2, (int)Math.Ceiling(8 * graphics.DpiX / 96f));
            int gap = Math.Max(1, (int)Math.Ceiling(4 * graphics.DpiY / 96f)), width = Math.Max(1, ClientSize.Width - inset * 2);
            if (CompactStatus)
            {
                int line = font.Height + gap, compactTop = Math.Max(0, ClientSize.Height - line * 2);
                return new StatusLayout { FigureHeight = compactTop, Main = new Rectangle(inset, compactTop, width, line), Target = new Rectangle(inset, compactTop + line, width, line) };
            }
            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int main = TextRenderer.MeasureText(graphics, StatusText, font, new Size(width, Int32.MaxValue), flags).Height;
            // Reserve two detail lines even without a warning. Incoming samples
            // must not move the controller beneath a click or an OLE drop.
            int detail = TextRenderer.MeasureText(graphics, "Ag\nAg", font, new Size(width, Int32.MaxValue), flags).Height;
            // A fixed measured hover line prevents geometry from moving under the
            // pointer when a different target name becomes highlighted.
            int target = TextRenderer.MeasureText(graphics, "Ag", font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine).Height;
            int required = gap * 3 + target + main + (detail == 0 ? 0 : gap + detail);
            int top = Math.Max(0, ClientSize.Height - required);
            int mainHeight = Math.Min(main, Math.Max(0, ClientSize.Height - top - gap * 2));
            StatusLayout result = new StatusLayout { FigureHeight = top, Main = new Rectangle(inset, top + gap, width, mainHeight) };
            int next = result.Main.Bottom + gap;
            int detailHeight = Math.Min(detail, Math.Max(0, ClientSize.Height - next - gap));
            result.Detail = new Rectangle(inset, next, width, detailHeight); if (detailHeight > 0) next += detailHeight + gap;
            result.Target = new Rectangle(inset, next, width, Math.Min(target, Math.Max(0, ClientSize.Height - next - gap)));
            return result;
        }
        Viewport GetViewport(int figureHeight)
        {
            if (figureHeight <= 0 || ClientSize.Width <= 16) return new Viewport();
            float scale = Math.Min((ClientSize.Width - 16f) / ControllerLayout.Width, figureHeight / ControllerLayout.Height);
            return new Viewport { Scale = scale, X = (ClientSize.Width - ControllerLayout.Width * scale) / 2, Y = (figureHeight - ControllerLayout.Height * scale) / 2 };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (ClientSize.Width < 24 || ClientSize.Height < 80) return;
            Graphics graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font font = StatusFont())
            {
                StatusLayout status = MeasureStatus(graphics, font); DrawStatus(graphics, font, status);
                Viewport viewport = GetViewport(status.FigureHeight); if (viewport.Scale <= 0) return;
                GraphicsState saved = graphics.Save(); graphics.TranslateTransform(viewport.X, viewport.Y); graphics.ScaleTransform(viewport.Scale, viewport.Scale);
                using (Font labels = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font small = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font face = new Font("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    DrawBody(graphics);
                    DrawLabel(graphics, Style == ControllerStyle.PlayStation5 ? "PS5" : "Xbox", labels, new RectangleF(181, 14, 78, 15), ModernTheme.Muted);
                    foreach (ControllerRegion region in layout.Regions) DrawRegion(graphics, region, labels, small, face, viewport.Scale);
                    DrawKeyAssignments(graphics);
                    DrawStickPosition(graphics, layout.LeftStick, current.LeftX, current.LeftY);
                    DrawStickPosition(graphics, layout.RightStick, current.RightX, current.RightY);
                }
                graphics.Restore(saved);
            }
        }
        void DrawStatus(Graphics graphics, Font font, StatusLayout status)
        {
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (CompactStatus) flags = (flags & ~TextFormatFlags.WordBreak) | TextFormatFlags.SingleLine;
            if (status.Main.Height > 0) TextRenderer.DrawText(graphics, StatusText, font, status.Main, current.Error != null ? ModernTheme.DangerColor : current.OutputEnabled ? ModernTheme.Accent : ModernTheme.Muted, flags);
            if (status.Detail.Height > 0) TextRenderer.DrawText(graphics, DetailText, font, status.Detail, current.Error != null ? ModernTheme.DangerColor : !unseenInputsOnly && (current.Warning != null || current.UnavailableKeys > 0) ? Color.FromArgb(218, 187, 118) : ModernTheme.Muted, flags);
            if (status.Target.Height > 0) TextRenderer.DrawText(graphics, TargetText, font, status.Target, dropTarget.HasValue ? MappingDragFeedback.DropColor : hoverTarget.HasValue ? ModernTheme.Foreground : ModernTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        void DrawBody(Graphics graphics)
        {
            bool ps = Style == ControllerStyle.PlayStation5;
            using (GraphicsPath body = layout.CreateBodyPath())
            {
                GraphicsState saved = graphics.Save(); graphics.TranslateTransform(0, 3);
                using (Brush shadow = new SolidBrush(Color.FromArgb(13, 14, 18))) graphics.FillPath(shadow, body); graphics.Restore(saved);
                using (Brush fill = new LinearGradientBrush(new RectangleF(18, 50, 404, 183), ps ? Color.FromArgb(211, 215, 227) : Color.FromArgb(45, 48, 58), ps ? Color.FromArgb(147, 154, 174) : Color.FromArgb(31, 33, 41), LinearGradientMode.Vertical)) graphics.FillPath(fill, body);
                using (Pen border = new Pen(ps ? Color.FromArgb(229, 232, 241) : Color.FromArgb(62, 66, 79), 1.2f)) graphics.DrawPath(border, body);
                if (ps)
                {
                    // Decorative DualSense-like insert and touchpad. No PS/Home,
                    // touchpad or microphone output region is defined.
                    using (GraphicsPath inset = new GraphicsPath())
                    {
                        inset.AddBezier(176, 59, 199, 63, 241, 63, 264, 59);
                        inset.AddBezier(264, 59, 265, 99, 281, 124, 326, 152);
                        inset.AddBezier(326, 152, 310, 203, 271, 197, 220, 193);
                        inset.AddBezier(220, 193, 169, 197, 130, 203, 114, 152);
                        inset.AddBezier(114, 152, 159, 124, 175, 99, 176, 59); inset.CloseFigure();
                        using (Brush fill = new SolidBrush(Color.FromArgb(25, 28, 37))) graphics.FillPath(fill, inset);
                    }
                    using (GraphicsPath touchpad = ControllerLayout.Rounded(new RectangleF(178, 68, 84, 49), 6))
                    {
                        using (Brush fill = new SolidBrush(Color.FromArgb(40, 44, 57))) graphics.FillPath(fill, touchpad);
                        using (Pen border = new Pen(Color.FromArgb(106, 111, 151), 1.2f)) graphics.DrawPath(border, touchpad);
                    }
                }
                else using (Pen seam = new Pen(Color.FromArgb(52, 56, 67), 1))
                {
                    graphics.DrawBezier(seam, new PointF(71, 143), new PointF(80, 165), new PointF(59, 202), new PointF(47, 214));
                    graphics.DrawBezier(seam, new PointF(369, 143), new PointF(360, 165), new PointF(381, 202), new PointF(393, 214));
                }
            }
        }
        void DrawRegion(Graphics graphics, ControllerRegion region, Font labels, Font small, Font face, float scale)
        {
            double amount = Amount(region.Target); bool active = current.HasInput && amount > 0;
            bool drop = dropTarget == region.Target, hover = !dropTarget.HasValue && hoverTarget == region.Target, available = availableTargets.Contains(region.Target);
            Color color = active ? Color.FromArgb(64 + (int)(amount * 75), 56 + (int)(amount * 69), 88 + (int)(amount * 103)) : Color.FromArgb(28, 31, 40);
            if (region.Kind == ControllerRegionKind.Face && active) color = FaceColor(region.Target);
            using (GraphicsPath path = region.CreatePath())
            {
                using (Brush fill = new SolidBrush(color)) graphics.FillPath(fill, path);
                if (available) using (Brush wash = new SolidBrush(Color.FromArgb(26, PartnerColor))) graphics.FillPath(wash, path);
                if (drop || hover) using (Brush fill = new SolidBrush(Color.FromArgb(drop ? 91 : 42, drop ? MappingDragFeedback.DropColor : ModernTheme.Accent))) graphics.FillPath(fill, path);
                using (Pen border = new Pen(drop ? MappingDragFeedback.DropColor : available ? PartnerColor : hover || active ? ModernTheme.Accent : Color.FromArgb(73, 78, 94),
                    drop ? 2.5f / scale : hover ? 1.8f : available ? 1.4f : 1f) { DashStyle = drop ? DashStyle.Dash : DashStyle.Solid }) graphics.DrawPath(border, path);
                if (region.Kind == ControllerRegionKind.Trigger)
                {
                    RectangleF track = new RectangleF(region.Bounds.X + 7, region.Bounds.Bottom - 8, region.Bounds.Width - 14, 4);
                    using (Brush empty = new SolidBrush(Color.FromArgb(14, 16, 22))) graphics.FillRectangle(empty, track);
                    if (active) using (Brush fill = new SolidBrush(ModernTheme.AccentHover)) graphics.FillRectangle(fill, track.X, track.Y, (float)(track.Width * amount), track.Height);
                }
            }
            Color legend = active && region.Kind == ControllerRegionKind.Face ? Color.FromArgb(25, 27, 35) : drop || active ? ModernTheme.Foreground : ModernTheme.Muted;
            if (region.Kind == ControllerRegionKind.StickDirection || region.Kind == ControllerRegionKind.Dpad)
            { if (!InlineKeyAssignment(region)) DrawArrow(graphics, region.LabelCenter, region.Direction, legend); }
            else if (region.Kind == ControllerRegionKind.Trigger)
            {
                DrawLabel(graphics, ControllerPresentation.ShortLabel(region.Target, Style), small, new RectangleF(region.Bounds.X + 7, region.Bounds.Y + 2, 24, 13), legend);
                DrawLabel(graphics, current.HasInput ? Percent(amount) : "—", small, new RectangleF(region.Bounds.Right - 30, region.Bounds.Y + 2, 26, 13), legend);
            }
            else if (region.Kind == ControllerRegionKind.Face && Style == ControllerStyle.PlayStation5) DrawPlayStationSymbol(graphics, region.Target, region.Center, legend);
            else
            {
                RectangleF labelBounds = region.Bounds;
                if (keyAssignments.ContainsKey(region.Target) && region.Kind == ControllerRegionKind.Shoulder) labelBounds.Width = 30;
                if (keyAssignments.ContainsKey(region.Target) && region.Kind == ControllerRegionKind.StickClick) labelBounds = new RectangleF(region.Bounds.X, region.Bounds.Y, region.Bounds.Width, 10);
                DrawLabel(graphics, ControllerPresentation.ShortLabel(region.Target, Style), region.Kind == ControllerRegionKind.Face ? face : region.Kind == ControllerRegionKind.Shoulder ? labels : small, labelBounds, legend);
            }
        }
        void DrawStickPosition(Graphics graphics, PointF center, double x, double y)
        {
            if (!current.HasInput || (x == 0 && y == 0)) return;
            // Position marker inside the fixed click target; it never moves L3/R3
            // or hides the four independently selectable direction regions.
            PointF point = new PointF(center.X + (float)x * 7, center.Y - (float)y * 7);
            using (Brush fill = new SolidBrush(ModernTheme.AccentHover)) graphics.FillEllipse(fill, point.X - 2, point.Y - 2, 4, 4);
            using (Pen edge = new Pen(Color.FromArgb(27, 29, 35), .8f)) graphics.DrawEllipse(edge, point.X - 2, point.Y - 2, 4, 4);
        }
        static Color FaceColor(OutputTarget target)
        {
            if (target == OutputTarget.A) return Color.FromArgb(150, 212, 175);
            if (target == OutputTarget.B) return Color.FromArgb(230, 153, 165);
            if (target == OutputTarget.X) return Color.FromArgb(143, 185, 237);
            return Color.FromArgb(230, 201, 129);
        }
        static void DrawArrow(Graphics graphics, PointF center, int direction, Color color)
        {
            double angle = direction * Math.PI / 2; PointF[] points = new PointF[3];
            PointF[] basis = { new PointF(-2, -4), new PointF(2, 0), new PointF(-2, 4) };
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(center.X + (float)(basis[i].X * Math.Cos(angle) - basis[i].Y * Math.Sin(angle)), center.Y + (float)(basis[i].X * Math.Sin(angle) + basis[i].Y * Math.Cos(angle)));
            using (Pen pen = new Pen(color, 1.5f)) { pen.StartCap = pen.EndCap = LineCap.Round; graphics.DrawLines(pen, points); }
        }
        static void DrawPlayStationSymbol(Graphics graphics, OutputTarget target, PointF center, Color color)
        {
            using (Pen pen = new Pen(color, 1.5f))
            {
                if (target == OutputTarget.A) { graphics.DrawLine(pen, center.X - 4, center.Y - 4, center.X + 4, center.Y + 4); graphics.DrawLine(pen, center.X + 4, center.Y - 4, center.X - 4, center.Y + 4); }
                else if (target == OutputTarget.B) graphics.DrawEllipse(pen, center.X - 5, center.Y - 5, 10, 10);
                else if (target == OutputTarget.X) graphics.DrawRectangle(pen, center.X - 4.5f, center.Y - 4.5f, 9, 9);
                else graphics.DrawPolygon(pen, new[] { new PointF(center.X, center.Y - 5), new PointF(center.X + 5, center.Y + 4), new PointF(center.X - 5, center.Y + 4) });
            }
        }
        static void DrawLabel(Graphics graphics, string value, Font font, RectangleF bounds, Color color)
        {
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter }) graphics.DrawString(value, font, brush, bounds, format);
        }
        Rectangle TargetBounds(ControllerRegion region)
        {
            if (!IsHandleCreated || IsDisposed) return Rectangle.Empty;
            using (Graphics graphics = CreateGraphics()) using (Font font = StatusFont())
            {
                Viewport viewport = GetViewport(MeasureStatus(graphics, font).FigureHeight); if (viewport.Scale <= 0) return Rectangle.Empty;
                using (GraphicsPath path = region.CreatePath()) return RectangleToScreen(viewport.ToClient(path.GetBounds()));
            }
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new PreviewAccessibleObject(this); }
        sealed class PreviewAccessibleObject : ControlAccessibleObject
        {
            readonly ControllerPreview owner;
            public PreviewAccessibleObject(ControllerPreview owner) : base(owner) { this.owner = owner; }
            public override string Name { get { return UiText.Get("Controller-Vorschau", "Controller preview") + " · " + ControllerPresentation.Name(owner.Style); } set { } }
            public override string Description { get { return owner.StatusText + ". " + owner.DetailText; } }
            public override int GetChildCount() { return owner.layout.Regions.Count; }
            public override AccessibleObject GetChild(int index) { return index < 0 || index >= GetChildCount() ? null : new TargetAccessibleObject(owner, owner.layout.Regions[index], this); }
            public override string Value
            {
                get
                {
                    Snapshot state = owner.current; if (!state.HasInput) return owner.DetailText;
                    return String.Format(CultureInfo.InvariantCulture, UiText.Get("LX {0:0.00}, LY {1:0.00}; RX {2:0.00}, RY {3:0.00}; LT {4}; RT {5}; Tasten 0x{6:X4}", "LX {0:0.00}, LY {1:0.00}; RX {2:0.00}, RY {3:0.00}; LT {4}; RT {5}; buttons 0x{6:X4}"), state.LeftX, state.LeftY, state.RightX, state.RightY, Percent(state.LeftTrigger), Percent(state.RightTrigger), state.Buttons);
                }
                set { }
            }
        }
        sealed class TargetAccessibleObject : AccessibleObject
        {
            readonly ControllerPreview owner; readonly ControllerRegion region; readonly AccessibleObject parent;
            public TargetAccessibleObject(ControllerPreview owner, ControllerRegion region, AccessibleObject parent) { this.owner = owner; this.region = region; this.parent = parent; }
            public override string Name { get { return ControllerPresentation.Label(region.Target, owner.Style); } set { } }
            public override string Description { get { return owner.GetAssignedKeyText(region.Target); } }
            public override string Value { get { return owner.HasValidInput ? Percent(owner.Amount(region.Target)) : UiText.Get("Keine aktuellen Messwerte", "No current readings"); } set { } }
            public override string DefaultAction { get { return UiText.Get("Auswählen", "Select"); } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { ControllerRegion current = owner.layout.Find(region.Target); return current == null ? Rectangle.Empty : owner.TargetBounds(current); } }
            public override void DoDefaultAction() { if (!owner.IsDisposed) owner.SelectTarget(region.Target); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { CancelGesture(); tooltip.Dispose(); } base.Dispose(disposing); }
    }
}
