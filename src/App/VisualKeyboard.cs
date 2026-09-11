using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed class KeyboardControllerBadge
    {
        public int Number;
        public Color Tint = Color.FromArgb(174, 157, 255);
        public string Name, Targets;
        public bool Selected;
    }
    // Own-window rendering only. This component never reads, writes or hooks a device.
    public class VisualKeyboard : Control
    {
        // Keep the standalone keyboard renderer in the application's dark palette.
        static readonly Color CanvasColor = Color.FromArgb(18, 19, 23);
        static readonly Color CaseColor = Color.FromArgb(27, 29, 35);
        static readonly Color KeyColor = Color.FromArgb(40, 43, 51);
        static readonly Color BorderColor = Color.FromArgb(54, 58, 69);
        static readonly Color TextColor = Color.FromArgb(243, 244, 247);
        static readonly Color MutedColor = Color.FromArgb(150, 157, 173);
        static readonly Color AccentColor = Color.FromArgb(178, 164, 255);
        static readonly Color ActiveColor = Color.FromArgb(121, 200, 175);
        static readonly Color DropColor = MappingDragFeedback.DropColor;
        static readonly Color PartnerColor = MappingDragFeedback.AvailableColor;
        sealed class KeyState { public bool Mapped, Active, Estimated; public double? Depth; }
        readonly Dictionary<int, KeyState> states = new Dictionary<int, KeyState>();
        readonly Dictionary<int, string> labels = new Dictionary<int, string>();
        readonly Dictionary<int, KeyboardControllerBadge[]> controllerAssignments = new Dictionary<int, KeyboardControllerBadge[]>();
        readonly HashSet<int> selected = new HashSet<int>();
        readonly HashSet<int> dropKeys = new HashSet<int>();
        readonly HashSet<int> availableKeys = new HashSet<int>();
        readonly ToolTip tooltip = new ToolTip();
        KeyboardLayout layout;
        KeyboardLegendStyle legend;
        string hoverCode, focusCode;
        string pressedCode;
        Point dragOrigin;
        Point pointerPosition;
        bool pointerInside;
        bool clickArmed, dragArmed, dragDispatching, collapseSelectionOnClick;
        bool marqueeArmed, marqueeDragging;
        Point marqueeOrigin;
        Rectangle marqueeBounds;
        readonly HashSet<int> marqueeSeed = new HashSet<int>(), marqueeSelection = new HashSet<int>();
        public event EventHandler SelectionChanged;
        public event Action<KeyboardKeyDefinition> UnassignedKeySelected;
        // The consumer starts its normal OLE drag synchronously. The payload is
        // a detached selection snapshot, never the internal selected collection.
        public event Action<int[]> KeyDragRequested;

        public VisualKeyboard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = CanvasColor; ForeColor = TextColor; TabStop = true;
            AccessibleName = UiText.Get("Tastatur", "Keyboard"); AccessibleRole = AccessibleRole.Pane;
            AccessibleDescription = UiText.Get("Auf freier Fläche ein Auswahlrechteck ziehen. Strg oder Umschalt ergänzt die Auswahl.", "Drag a selection rectangle from empty space. Ctrl or Shift adds to the selection.");
            Size = new Size(830, 350);
            tooltip.InitialDelay = 650; tooltip.ReshowDelay = 100; tooltip.AutoPopDelay = 12000;
        }
        public KeyboardLayout LayoutModel
        {
            get { return layout; }
            set
            {
                if (ReferenceEquals(layout, value)) return;
                CancelDragGesture(); dropKeys.Clear(); availableKeys.Clear();
                layout = value; states.Clear(); labels.Clear(); controllerAssignments.Clear(); focusCode = hoverCode = null; tooltip.SetToolTip(this, null);
                RefreshPointerCursor();
                bool changed = selected.Count != 0; selected.Clear(); Invalidate();
                if (changed) OnSelectionChanged();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }
        }
        public KeyboardLegendStyle LegendStyle
        {
            get { return legend; }
            set
            {
                if (value != KeyboardLegendStyle.Qwerty && value != KeyboardLegendStyle.Qwertz) throw new ArgumentOutOfRangeException("value");
                if (legend == value) return; legend = value; Invalidate(); RefreshHoverTooltip();
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            }
        }
        public int[] SelectedKeyIndices
        {
            get { int[] result = new int[selected.Count]; selected.CopyTo(result); Array.Sort(result); return result; }
        }
        protected virtual Keys SelectionModifiers { get { return ModifierKeys; } }
        public void SetSelectedKeys(IEnumerable<int> indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            HashSet<int> next = new HashSet<int>();
            foreach (int index in indices)
            { ValidateIndex(index); if (layout != null && layout.FindByIndex(index) != null) next.Add(index); }
            if (selected.SetEquals(next)) return;
            CancelDragGesture();
            selected.Clear(); selected.UnionWith(next); Invalidate(); OnSelectionChanged();
        }
        public void SetDropKeys(IEnumerable<int> indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            var next = new HashSet<int>();
            foreach (int index in indices)
            { ValidateIndex(index); if (layout != null && layout.FindByIndex(index) != null) next.Add(index); }
            if (dropKeys.SetEquals(next)) return;
            var changed = new HashSet<int>(dropKeys); changed.UnionWith(next);
            dropKeys.Clear(); dropKeys.UnionWith(next);
            foreach (int index in changed) InvalidateKey(index);
        }
        public void SetAvailableKeys(IEnumerable<int> indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            var next = new HashSet<int>();
            foreach (int index in indices)
            { ValidateIndex(index); if (layout != null && layout.FindByIndex(index) != null) next.Add(index); }
            if (availableKeys.SetEquals(next)) return;
            var changed = new HashSet<int>(availableKeys); changed.UnionWith(next);
            availableKeys.Clear(); availableKeys.UnionWith(next);
            foreach (int index in changed) InvalidateKey(index);
        }
        public void SetMappedKeys(IEnumerable<int> indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            HashSet<int> next = new HashSet<int>(); foreach (int index in indices) { ValidateIndex(index); next.Add(index); }
            foreach (KeyValuePair<int, KeyState> item in states)
            {
                bool mapped = next.Contains(item.Key);
                if (item.Value.Mapped != mapped) { item.Value.Mapped = mapped; InvalidateKey(item.Key); }
            }
            foreach (int index in next)
                if (!states.ContainsKey(index)) { State(index).Mapped = true; InvalidateKey(index); }
        }
        // Mapping metadata stays separate from depth/live state. Store detached
        // values so profile edits cannot change painted labels between refreshes.
        public void SetControllerAssignments(int keyIndex, IEnumerable<KeyboardControllerBadge> assignments)
        {
            ValidateIndex(keyIndex); if (assignments == null) throw new ArgumentNullException("assignments");
            var next = new List<KeyboardControllerBadge>(); var numbers = new HashSet<int>();
            foreach (KeyboardControllerBadge item in assignments)
            {
                if (item == null || item.Number < 1 || !numbers.Add(item.Number)) throw new ArgumentException("Controller badges require unique positive numbers.", "assignments");
                next.Add(new KeyboardControllerBadge { Number = item.Number, Name = item.Name ?? "", Targets = item.Targets ?? "", Selected = item.Selected, Tint = item.Tint });
            }
            next.Sort(delegate(KeyboardControllerBadge left, KeyboardControllerBadge right) { return left.Number.CompareTo(right.Number); });
            KeyboardControllerBadge[] previous; controllerAssignments.TryGetValue(keyIndex, out previous);
            bool same = (previous == null ? 0 : previous.Length) == next.Count;
            for (int i = 0; same && i < next.Count; i++)
                same = previous[i].Number == next[i].Number && previous[i].Name == next[i].Name && previous[i].Targets == next[i].Targets && previous[i].Selected == next[i].Selected && previous[i].Tint == next[i].Tint;
            if (same) return;
            if (next.Count == 0) controllerAssignments.Remove(keyIndex); else controllerAssignments[keyIndex] = next.ToArray();
            InvalidateKey(keyIndex); RefreshHoverTooltip();
        }
        public void UpdateKeyState(int keyIndex, bool mapped, bool active, double? depth)
        {
            UpdateKeyState(keyIndex, mapped, active, depth, false);
        }
        public void UpdateKeyState(int keyIndex, bool mapped, bool active, double? depth, bool estimated)
        {
            ValidateIndex(keyIndex);
            if (depth.HasValue && (Double.IsNaN(depth.Value) || Double.IsInfinity(depth.Value) || depth.Value < 0 || depth.Value > 1))
                throw new ArgumentOutOfRangeException("depth", UiText.Get("Die angezeigte Tiefe muss zwischen 0 und 1 liegen oder unbekannt sein.", "Displayed depth must be between 0 and 1, or unknown."));
            estimated = depth.HasValue && estimated;
            KeyState state = State(keyIndex);
            if (state.Mapped == mapped && state.Active == active && state.Depth == depth && state.Estimated == estimated) return;
            state.Mapped = mapped; state.Active = active; state.Depth = depth; state.Estimated = estimated;
            InvalidateKey(keyIndex);
            if (layout != null && hoverCode != null)
            {
                KeyboardKeyDefinition hovered = layout.FindByCode(hoverCode);
                if (hovered != null && hovered.KeyIndex == keyIndex) RefreshHoverTooltip();
            }
        }
        public void ClearKeyStates()
        {
            int[] previous = new int[states.Count]; states.Keys.CopyTo(previous, 0); states.Clear();
            foreach (int index in previous) InvalidateKey(index);
            RefreshHoverTooltip();
        }
        public void SetKeyLabel(int keyIndex, string label)
        {
            ValidateIndex(keyIndex);
            if (String.IsNullOrWhiteSpace(label)) labels.Remove(keyIndex);
            else labels[keyIndex] = label.Trim();
            InvalidateKey(keyIndex); RefreshHoverTooltip();
        }
        public string GetKeyLabel(KeyboardKeyDefinition key)
        {
            if (key == null) return "";
            string custom; return key.KeyIndex.HasValue && labels.TryGetValue(key.KeyIndex.Value, out custom) ? custom : key.GetLegend(legend);
        }
        // UI language is independent of physical legends and user-owned names.
        public void RefreshLanguage()
        {
            AccessibleName = UiText.Get("Tastatur", "Keyboard");
            AccessibleDescription = UiText.Get("Auf freier Fläche ein Auswahlrechteck ziehen. Strg oder Umschalt ergänzt die Auswahl.", "Drag a selection rectangle from empty space. Ctrl or Shift adds to the selection.");
            RefreshHoverTooltip(); Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
        }
        static void ValidateIndex(int index) { if (index < 0 || index > 255) throw new ArgumentOutOfRangeException("index"); }
        KeyState State(int index) { KeyState state; if (!states.TryGetValue(index, out state)) { state = new KeyState(); states.Add(index, state); } return state; }
        static string PercentText(double depth)
        { return Math.Round(depth * 100, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%"; }
        static string EstimatedDepthText()
        { return UiText.Get("Standardbereich: 0–385 · eigener Druckbereich optional", "Default range: 0–385 · custom calibration optional"); }
        void InvalidateKey(int index)
        {
            KeyboardKeyDefinition key = layout == null ? null : layout.FindByIndex(index);
            if (key == null) return;
            RectangleF bounds = key.IsKnob ? KnobBounds() : GetKeyBounds(key);
            if (!bounds.IsEmpty) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(bounds, 4, 4)));
        }
        void RefreshHoverTooltip()
        {
            KeyboardKeyDefinition key = layout == null ? null : layout.FindByCode(hoverCode);
            string text = key == null ? null : TooltipFor(key);
            if (!String.Equals(tooltip.GetToolTip(this), text, StringComparison.Ordinal)) tooltip.SetToolTip(this, text);
        }
        protected virtual void OnSelectionChanged()
        {
            EventHandler handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
            AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
        }

        public RectangleF KeyboardBounds
        {
            get
            {
                if (layout == null || ClientSize.Width <= 24 || ClientSize.Height <= 24) return RectangleF.Empty;
                float scale = Math.Min((ClientSize.Width - 24f) / layout.Size.Width, (ClientSize.Height - 24f) / layout.Size.Height);
                return new RectangleF((ClientSize.Width - layout.Size.Width * scale) / 2, (ClientSize.Height - layout.Size.Height * scale) / 2, layout.Size.Width * scale, layout.Size.Height * scale);
            }
        }
        public RectangleF GetKeyBounds(KeyboardKeyDefinition key)
        {
            RectangleF area = KeyboardBounds;
            if (key == null || layout == null || area.IsEmpty || layout.FindByCode(key.Code) != key) return RectangleF.Empty;
            float scale = area.Width / layout.Size.Width;
            return new RectangleF(area.X + key.Bounds.X * scale, area.Y + key.Bounds.Y * scale, key.Bounds.Width * scale, key.Bounds.Height * scale);
        }
        internal MappingDragVisual CreateDragVisual(int[] indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            if (IsDisposed || Disposing || layout == null || KeyboardBounds.IsEmpty || indices.Length == 0) return null;
            var keys = new List<KeyboardKeyDefinition>(); var unique = new HashSet<int>();
            RectangleF ink = RectangleF.Empty;
            foreach (int index in indices)
            {
                if (!unique.Add(index)) continue;
                KeyboardKeyDefinition key = layout.FindByIndex(index);
                if (key == null) return null;
                RectangleF bounds = GetKeyBounds(key); if (bounds.IsEmpty) return null;
                keys.Add(key);
                // The largest existing ink is the outer drop marker or the
                // three-pixel knob shadow. Keep a transparent margin for both.
                bounds = RectangleF.Inflate(bounds, 4, 4);
                ink = ink.IsEmpty ? bounds : RectangleF.Union(ink, bounds);
            }
            Rectangle crop = Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB(
                (int)Math.Floor(ink.Left), (int)Math.Floor(ink.Top), (int)Math.Ceiling(ink.Right), (int)Math.Ceiling(ink.Bottom)));
            if (crop.Width < 1 || crop.Height < 1) return null;
            // Render our own control once at its current size. This preserves
            // native TextRenderer legends, focus cues and badges without moving
            // their coordinates or capturing any part of the user's screen.
            using (var source = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var mask = new Bitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(source, ClientRectangle);
                using (Graphics graphics = Graphics.FromImage(mask))
                {
                    graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.TranslateTransform(-crop.Left, -crop.Top);
                    foreach (KeyboardKeyDefinition key in keys) PaintDragMask(graphics, key);
                }
                return MappingDragVisual.Extract(source, mask, crop, new Point(dragOrigin.X - crop.Left, dragOrigin.Y - crop.Top));
            }
        }
        void PaintDragMask(Graphics graphics, KeyboardKeyDefinition key)
        {
            RectangleF bounds = GetKeyBounds(key), shadow = bounds;
            if (key.IsKnob)
            {
                RectangleF knob = KnobBounds(); shadow = knob; shadow.Offset(0, 3);
                // The knob has three addressable segments. Keep just the
                // selected segment, including its curved outside edge/shadow.
                RectangleF segment = RectangleF.FromLTRB(bounds.Left == knob.Left ? bounds.Left - 1 : bounds.Left,
                    knob.Top - 1, bounds.Right == knob.Right ? bounds.Right + 1 : bounds.Right, knob.Bottom + 4);
                GraphicsState saved = graphics.Save(); graphics.SetClip(segment, CombineMode.Intersect);
                graphics.FillEllipse(Brushes.White, shadow); graphics.FillEllipse(Brushes.White, knob);
                using (Pen edge = new Pen(Color.White, 1)) graphics.DrawEllipse(edge, knob);
                graphics.Restore(saved); return;
            }
            shadow.Offset(0, 2.5f);
            using (GraphicsPath path = KeyPath(key, shadow)) graphics.FillPath(Brushes.White, path);
            using (GraphicsPath path = KeyPath(key, bounds))
            {
                graphics.FillPath(Brushes.White, path);
                float width = IsVisuallySelected(key.KeyIndex.Value) ? 1.5f : 1f;
                if (availableKeys.Contains(key.KeyIndex.Value)) width = Math.Max(width, 1.4f);
                using (Pen edge = new Pen(Color.White, width)) graphics.DrawPath(edge, path);
            }
            if (dropKeys.Contains(key.KeyIndex.Value))
                using (GraphicsPath path = KeyPath(key, RectangleF.Inflate(bounds, 1.5f, 1.5f)))
                using (Pen marker = new Pen(Color.White, 2.5f) { DashStyle = DashStyle.Dash }) graphics.DrawPath(marker, path);
        }
        public KeyboardKeyDefinition HitTest(Point point)
        {
            if (layout == null) return null;
            foreach (KeyboardKeyDefinition key in layout.Keys)
            {
                RectangleF bounds = GetKeyBounds(key); if (!bounds.Contains(point)) continue;
                if (key.IsKnob)
                { using (GraphicsPath circle = KnobPath()) if (circle.IsVisible(point)) return key; continue; }
                using (GraphicsPath path = KeyPath(key, bounds)) if (path.IsVisible(point)) return key;
            }
            return null;
        }
        // Public to keep mouse, keyboard and accessibility activation identical and testable.
        public void SelectKey(KeyboardKeyDefinition key, bool toggle)
        {
            if (key == null || layout == null || layout.FindByCode(key.Code) != key) return;
            CancelDragGesture();
            focusCode = key.Code;
            if (!key.KeyIndex.HasValue)
            { Action<KeyboardKeyDefinition> handler = UnassignedKeySelected; if (handler != null) handler(key); Invalidate(); return; }
            HashSet<int> next = toggle ? new HashSet<int>(selected) : new HashSet<int>();
            if (!toggle || !next.Remove(key.KeyIndex.Value)) next.Add(key.KeyIndex.Value);
            SetSelectedKeys(next); Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            RememberPointer(e.Location);
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !Enabled) return;
            CancelDragGesture();
            Focus(); KeyboardKeyDefinition key = HitTest(e.Location);
            if (key == null)
            {
                if (layout == null || !Focused || !ClientRectangle.Contains(e.Location)) return;
                marqueeOrigin = e.Location; marqueeArmed = true; marqueeDragging = false; marqueeBounds = Rectangle.Empty;
                marqueeSeed.Clear(); marqueeSelection.Clear();
                if ((SelectionModifiers & (Keys.Control | Keys.Shift)) != 0) marqueeSeed.UnionWith(selected);
                hoverCode = null; tooltip.Hide(this); Cursor = Cursors.Cross; Capture = true; return;
            }
            bool toggle = (SelectionModifiers & Keys.Control) != 0;
            bool deferSelection = !toggle && key.KeyIndex.HasValue && selected.Contains(key.KeyIndex.Value);
            if (deferSelection) { focusCode = key.Code; Invalidate(); }
            else SelectKey(key, toggle);
            // Ctrl-clicking an already selected key removes it; that gesture must
            // not unexpectedly start a drag of the remaining selected keys.
            if (IsDisposed || !Focused || !Enabled || layout == null || layout.FindByCode(key.Code) != key ||
                !key.KeyIndex.HasValue) return;
            clickArmed = true; Capture = true; RefreshPointerCursor();
            if (!selected.Contains(key.KeyIndex.Value)) return;
            pressedCode = key.Code; dragOrigin = e.Location;
            collapseSelectionOnClick = deferSelection; dragArmed = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            RememberPointer(e.Location);
            base.OnMouseMove(e);
            if (!Enabled) { RefreshPointerCursor(); return; }
            if (marqueeArmed)
            {
                if ((e.Button & MouseButtons.Left) == 0 || !Capture) CancelDragGesture();
                else UpdateMarquee(e.Location);
                return;
            }
            if (clickArmed && !dragDispatching && ((e.Button & MouseButtons.Left) == 0 || !Capture)) CancelDragGesture();
            if (dragArmed && !dragDispatching)
            {
                if ((e.Button & MouseButtons.Left) == 0 || !Capture) CancelDragGesture();
                else
                {
                    Size threshold = SystemInformation.DragSize;
                    Rectangle clickArea = new Rectangle(dragOrigin.X - threshold.Width / 2, dragOrigin.Y - threshold.Height / 2, threshold.Width, threshold.Height);
                    if (!clickArea.Contains(e.Location))
                    {
                        Action<int[]> handler = KeyDragRequested;
                        int[] indices = SelectedKeyIndices;
                        if (handler != null && indices.Length > 0)
                        {
                            dragDispatching = true;
                            CancelDragGesture(); tooltip.Hide(this);
                            try { handler(indices); }
                            finally
                            {
                                dragDispatching = false; CancelDragGesture();
                                if (!IsDisposed)
                                {
                                    // OLE owns mouse movement during the drag. Its
                                    // release point may be outside this keyboard.
                                    RememberPointer(PointToClient(MousePosition));
                                    SetDropKeys(new int[0]); hoverCode = null; RefreshPointerCursor(); Invalidate();
                                }
                            }
                            return;
                        }
                        // Crossing the threshold is no longer a simple click even
                        // when no drag consumer is registered.
                        CancelDragGesture();
                    }
                }
            }
            KeyboardKeyDefinition key = HitTest(e.Location);
            SetPointerCursor(key);
            string next = key == null ? null : key.Code; if (next == hoverCode) return;
            hoverCode = next;
            tooltip.SetToolTip(this, key == null ? null : TooltipFor(key)); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            RememberPointer(e.Location);
            base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
            if (marqueeArmed)
            {
                if (!Capture) { CancelDragGesture(); return; }
                UpdateMarquee(e.Location);
                var next = new HashSet<int>(marqueeDragging ? marqueeSelection : marqueeSeed);
                CancelDragGesture(); SetSelectedKeys(next); return;
            }
            string clickedCode = pressedCode;
            bool collapse = dragArmed && collapseSelectionOnClick && !dragDispatching;
            CancelDragGesture();
            KeyboardKeyDefinition clicked = collapse ? HitTest(e.Location) : null;
            if (clicked != null && clicked.Code == clickedCode) SelectKey(clicked, false);
        }
        void CancelDragGesture()
        {
            clickArmed = dragArmed = collapseSelectionOnClick = false; pressedCode = null;
            if (marqueeArmed || marqueeDragging)
            {
                marqueeArmed = marqueeDragging = false; marqueeBounds = Rectangle.Empty;
                marqueeSeed.Clear(); marqueeSelection.Clear(); Cursor = Cursors.Default; Invalidate();
            }
            if (IsHandleCreated && Capture) Capture = false;
            RefreshPointerCursor();
        }
        void RememberPointer(Point point)
        { pointerPosition = point; pointerInside = ClientRectangle.Contains(point); }
        void SetPointerCursor(KeyboardKeyDefinition key)
        {
            Cursor next = !Enabled || !Visible ? Cursors.Default : dragDispatching ? DragCursors.Grabbing :
                marqueeArmed ? Cursors.Cross : clickArmed ? DragCursors.Grabbing : key == null ? Cursors.Default : DragCursors.Grab;
            if (Cursor != next) Cursor = next;
        }
        void RefreshPointerCursor()
        {
            if (IsDisposed || Disposing) return;
            SetPointerCursor(pointerInside && Enabled && Visible ? HitTest(pointerPosition) : null);
        }
        void UpdateMarquee(Point pointer)
        {
            if (!marqueeArmed || layout == null) return;
            Size threshold = SystemInformation.DragSize;
            if (!marqueeDragging && Math.Abs(pointer.X - marqueeOrigin.X) < threshold.Width / 2 && Math.Abs(pointer.Y - marqueeOrigin.Y) < threshold.Height / 2) return;
            pointer = new Point(Math.Max(0, Math.Min(ClientSize.Width - 1, pointer.X)), Math.Max(0, Math.Min(ClientSize.Height - 1, pointer.Y)));
            Rectangle nextBounds = Rectangle.FromLTRB(Math.Min(marqueeOrigin.X, pointer.X), Math.Min(marqueeOrigin.Y, pointer.Y),
                Math.Max(marqueeOrigin.X, pointer.X) + 1, Math.Max(marqueeOrigin.Y, pointer.Y) + 1);
            if (marqueeDragging && nextBounds == marqueeBounds) return;
            var previous = new HashSet<int>(marqueeDragging ? marqueeSelection : selected);
            Rectangle previousBounds = marqueeBounds; marqueeBounds = nextBounds; marqueeDragging = true;
            marqueeSelection.Clear(); marqueeSelection.UnionWith(marqueeSeed);
            foreach (KeyboardKeyDefinition key in layout.Keys)
            {
                if (!key.KeyIndex.HasValue) continue;
                RectangleF bounds = GetKeyBounds(key);
                if (!bounds.IntersectsWith(marqueeBounds)) continue;
                using (GraphicsPath path = key.IsKnob ? KnobPath() : KeyPath(key, bounds))
                using (var region = new Region(path))
                {
                    if (key.IsKnob) region.Intersect(bounds);
                    if (region.IsVisible(marqueeBounds)) marqueeSelection.Add(key.KeyIndex.Value);
                }
            }
            var changed = new HashSet<int>(previous); changed.UnionWith(marqueeSelection);
            foreach (int index in changed) if (previous.Contains(index) != marqueeSelection.Contains(index)) InvalidateKey(index);
            if (!previousBounds.IsEmpty) Invalidate(Rectangle.Inflate(previousBounds, 2, 2));
            Invalidate(Rectangle.Inflate(marqueeBounds, 2, 2));
        }
        bool IsVisuallySelected(int index) { return (marqueeDragging ? marqueeSelection : selected).Contains(index); }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) CancelDragGesture();
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); CancelDragGesture(); }
        protected override void OnEnabledChanged(EventArgs e)
        { base.OnEnabledChanged(e); if (!Enabled) { CancelDragGesture(); SetDropKeys(new int[0]); SetAvailableKeys(new int[0]); } else RefreshPointerCursor(); }
        protected override void OnVisibleChanged(EventArgs e)
        { base.OnVisibleChanged(e); if (!Visible) { CancelDragGesture(); SetDropKeys(new int[0]); SetAvailableKeys(new int[0]); } else RefreshPointerCursor(); }
        string TooltipFor(KeyboardKeyDefinition key)
        {
            string identity = !key.KeyIndex.HasValue ? UiText.Get("Zuordnung noch anlernen", "Identify this key first")
                : key.VerifiedOnHardware ? UiText.Get("Physisch bestätigt", "Physically verified")
                : UiText.Get("Herstellerlayout · Druckwerte noch prüfen", "Manufacturer layout · pressure data still needs verification");
            string action = key.IsKnob || key.Code == "Fn" ? "\n" + UiText.Get("Druckfähigkeit nicht bestätigt.", "Pressure sensing is not confirmed.") : "";
            KeyState state = null; if (key.KeyIndex.HasValue) states.TryGetValue(key.KeyIndex.Value, out state);
            string depth = state != null && state.Depth.HasValue ? (state.Estimated ? EstimatedDepthText() : UiText.Get("Kalibrierte Drucktiefe: ", "Calibrated depth: ") + PercentText(state.Depth.Value))
                : state != null && state.Active ? UiText.Get("Gedrückt · keine kalibrierte Drucktiefe verfügbar.", "Pressed · calibrated depth is unavailable.")
                : UiText.Get("Drucktiefe unbekannt oder noch nicht kalibriert.", "Depth is unknown or not calibrated yet.");
            string dragHelp = key.KeyIndex.HasValue ? "\n" + UiText.Get("Auswahl zum Controller ziehen · Controller-Ziel hier ablegen", "Drag selection onto the controller · Drop a controller control here") : "";
            string mappings = ""; KeyboardControllerBadge[] assigned;
            if (key.KeyIndex.HasValue && controllerAssignments.TryGetValue(key.KeyIndex.Value, out assigned))
            {
                mappings = "\n\n" + UiText.Get("Zugeordnet zu:", "Assigned to:");
                foreach (KeyboardControllerBadge badge in assigned)
                    mappings += "\n" + badge.Number + " · " + badge.Name + (badge.Selected ? UiText.Get(" (ausgewählt)", " (selected)") : "") + ": " + badge.Targets;
            }
            else mappings = "\n\n" + UiText.Get("Noch keinem Controller zugeordnet.", "Not assigned to a controller yet.");
            return GetKeyLabel(key) + " · " + identity + "\n" + depth + action + mappings + "\n\n" + UiText.Get("Klicken: auswählen · Strg+Klicken: mehrere Tasten", "Click to select · Ctrl+click to select multiple keys") + dragHelp;
        }
        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); pointerInside = false; hoverCode = null; RefreshPointerCursor(); Invalidate(); }
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (focusCode == null && layout != null) focusCode = layout.FindByCode("Escape") != null ? "Escape" : layout.Keys[0].Code;
            Invalidate();
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); CancelDragGesture(); Invalidate(); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return (key == Keys.Escape && (marqueeArmed || clickArmed || dragArmed || dragDispatching || dropKeys.Count > 0)) || key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Space || key == Keys.Enter || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && (marqueeArmed || clickArmed || dragArmed || dragDispatching || dropKeys.Count > 0))
            { CancelDragGesture(); SetDropKeys(new int[0]); SetAvailableKeys(new int[0]); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (layout == null) return;
            KeyboardKeyDefinition focus = layout.FindByCode(focusCode) ?? layout.Keys[0];
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { SelectKey(focus, e.Control); e.Handled = true; e.SuppressKeyPress = true; return; }
            int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
            int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
            if (dx == 0 && dy == 0) return;
            double best = Double.MaxValue; KeyboardKeyDefinition nearest = null;
            PointF origin = Center(focus.Bounds);
            foreach (KeyboardKeyDefinition candidate in layout.Keys)
            {
                PointF center = Center(candidate.Bounds); float x = center.X - origin.X, y = center.Y - origin.Y;
                float forward = x * dx + y * dy; if (forward <= 0) continue;
                float lateral = dx == 0 ? Math.Abs(x) : Math.Abs(y);
                double score = forward + lateral * 3.0;
                if (score < best) { best = score; nearest = candidate; }
            }
            if (nearest != null) { focusCode = nearest.Code; if (!e.Control) SelectKey(nearest, false); Invalidate(); }
            e.Handled = true; e.SuppressKeyPress = true;
        }
        static PointF Center(RectangleF bounds) { return new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (layout == null)
            {
                TextRenderer.DrawText(e.Graphics, UiText.Get("Tastatur verbinden, um Tasten einzurichten.", "Connect a keyboard to configure its keys."), Font, ClientRectangle, MutedColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix); return;
            }
            RectangleF area = KeyboardBounds; if (area.IsEmpty) return;
            RectangleF shell = RectangleF.Inflate(area, 9, 9);
            RectangleF shadow = shell; shadow.Offset(0, 3);
            using (GraphicsPath path = Rounded(shadow, 12))
            using (Brush fill = new SolidBrush(Color.FromArgb(10, 11, 15))) e.Graphics.FillPath(fill, path);
            using (GraphicsPath path = Rounded(shell, 12))
            {
                using (Brush fill = new LinearGradientBrush(shell, Color.FromArgb(31, 33, 40), CaseColor, LinearGradientMode.Vertical)) e.Graphics.FillPath(fill, path);
                using (Pen edge = new Pen(Color.FromArgb(43, 46, 56), 1f)) e.Graphics.DrawPath(edge, path);
            }
            float scale = area.Width / layout.Size.Width;
            using (Font keyFont = new Font("Segoe UI", Math.Max(7f, Math.Min(12f, 10f * scale)), FontStyle.Regular, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", Math.Max(6f, Math.Min(8f, 7f * scale)), FontStyle.Regular, GraphicsUnit.Point))
            {
                foreach (KeyboardKeyDefinition key in layout.Keys)
                    if (!key.IsKnob && e.Graphics.IsVisible(RectangleF.Inflate(GetKeyBounds(key), 4, 4))) PaintKey(e.Graphics, key, keyFont, valueFont);
                if (e.Graphics.IsVisible(RectangleF.Inflate(KnobBounds(), 4, 4))) PaintKnob(e.Graphics, keyFont);
            }
            if (marqueeDragging)
            {
                using (Brush fill = new SolidBrush(Color.FromArgb(28, AccentColor))) e.Graphics.FillRectangle(fill, marqueeBounds);
                using (Pen border = new Pen(AccentColor, 1.2f)) e.Graphics.DrawRectangle(border, marqueeBounds);
            }
        }
        RectangleF KnobBounds()
        {
            RectangleF result = RectangleF.Empty;
            if (layout != null) foreach (KeyboardKeyDefinition key in layout.Keys)
                if (key.IsKnob) result = result.IsEmpty ? GetKeyBounds(key) : RectangleF.Union(result, GetKeyBounds(key));
            return result;
        }
        GraphicsPath KnobPath()
        {
            GraphicsPath path = new GraphicsPath(); RectangleF bounds = KnobBounds();
            if (!bounds.IsEmpty) path.AddEllipse(bounds); return path;
        }
        void PaintKnob(Graphics graphics, Font keyFont)
        {
            RectangleF bounds = KnobBounds(); if (bounds.IsEmpty) return;
            RectangleF shadow = bounds; shadow.Offset(0, 3);
            using (Brush brush = new SolidBrush(Color.FromArgb(10, 11, 15))) graphics.FillEllipse(brush, shadow);
            using (GraphicsPath circle = KnobPath())
            {
                using (Brush brush = new LinearGradientBrush(bounds, Color.FromArgb(63, 67, 79), Color.FromArgb(28, 30, 37), LinearGradientMode.Vertical)) graphics.FillPath(brush, circle);
                float inset = Math.Min(6f, Math.Min(bounds.Width, bounds.Height) * .09f);
                RectangleF face = RectangleF.Inflate(bounds, -inset, -inset);
                using (Brush brush = new LinearGradientBrush(face, Color.FromArgb(46, 49, 59), Color.FromArgb(32, 35, 43), LinearGradientMode.Vertical)) graphics.FillEllipse(brush, face);
                using (Pen ring = new Pen(Color.FromArgb(24, 26, 32), 1f)) graphics.DrawEllipse(ring, face);
                PointF center = Center(bounds); float radius = bounds.Width / 2;
                using (Pen tick = new Pen(Color.FromArgb(78, 83, 97), 1f))
                {
                    for (int number = 0; number < 28; number++)
                    {
                        double angle = number * Math.PI * 2 / 28;
                        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                        graphics.DrawLine(tick, center.X + cos * radius * .84f, center.Y + sin * radius * .84f,
                            center.X + cos * radius * .94f, center.Y + sin * radius * .94f);
                    }
                }
                GraphicsState saved = graphics.Save(); graphics.SetClip(circle, CombineMode.Intersect);
                foreach (KeyboardKeyDefinition key in layout.Keys)
                {
                    if (!key.IsKnob) continue;
                    RectangleF segment = GetKeyBounds(key); KeyState state = null;
                    if (key.KeyIndex.HasValue) states.TryGetValue(key.KeyIndex.Value, out state);
                    bool chosen = key.KeyIndex.HasValue && IsVisuallySelected(key.KeyIndex.Value);
                    if (chosen || key.Code == hoverCode || (state != null && state.Active))
                    {
                        Color color = chosen ? Color.FromArgb(55, AccentColor) : state != null && state.Active ? Color.FromArgb(45, ActiveColor) : Color.FromArgb(20, TextColor);
                        using (Brush brush = new SolidBrush(color)) graphics.FillRectangle(brush, segment);
                    }
                    if (key.KeyIndex.HasValue && availableKeys.Contains(key.KeyIndex.Value))
                    {
                        using (Brush brush = new SolidBrush(Color.FromArgb(24, PartnerColor))) graphics.FillRectangle(brush, segment);
                        RectangleF partner = RectangleF.Inflate(segment, -1, -1);
                        if (partner.Width > 0 && partner.Height > 0)
                            using (Pen pen = new Pen(PartnerColor, 1.3f)) graphics.DrawRectangle(pen, partner.X, partner.Y, partner.Width, partner.Height);
                    }
                    TextRenderer.DrawText(graphics, GetKeyLabel(key), keyFont, Rectangle.Round(segment), chosen ? TextColor : MutedColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    float badgeHeight = DrawControllerBadges(graphics, key, segment);
                    if (badgeHeight == 0 && state != null && state.Mapped)
                        using (Brush brush = new SolidBrush(AccentColor)) graphics.FillEllipse(brush, segment.X + segment.Width / 2 - 2, bounds.Bottom - 9, 4, 4);
                    if (key.KeyIndex.HasValue && dropKeys.Contains(key.KeyIndex.Value))
                    {
                        RectangleF target = RectangleF.Inflate(segment, -3, -3);
                        if (target.Width > 0 && target.Height > 0)
                            using (Pen marker = new Pen(DropColor, 2.5f) { DashStyle = DashStyle.Dash })
                                graphics.DrawRectangle(marker, target.X, target.Y, target.Width, target.Height);
                    }
                    if (Focused && focusCode == key.Code)
                        ControlPaint.DrawFocusRectangle(graphics, Rectangle.Round(RectangleF.Inflate(segment, -2, -4)), AccentColor, KeyColor);
                }
                graphics.Restore(saved);
                using (Pen pen = new Pen(Color.FromArgb(65, 70, 83), 1f)) graphics.DrawPath(pen, circle);
                using (Pen highlight = new Pen(Color.FromArgb(107, 111, 126), 1f)) graphics.DrawArc(highlight, bounds, 215, 100);
            }
        }
        void PaintKey(Graphics graphics, KeyboardKeyDefinition key, Font keyFont, Font valueFont)
        {
            RectangleF bounds = GetKeyBounds(key); if (bounds.Width < 1 || bounds.Height < 1) return;
            KeyState state = null; if (key.KeyIndex.HasValue) states.TryGetValue(key.KeyIndex.Value, out state);
            bool chosen = key.KeyIndex.HasValue && IsVisuallySelected(key.KeyIndex.Value), active = state != null && state.Active;
            bool hovering = key.Code == hoverCode;
            Color fillTop = chosen ? Color.FromArgb(61, 54, 85) : hovering ? Color.FromArgb(51, 54, 65) : Color.FromArgb(44, 47, 56);
            Color fillBottom = chosen ? Color.FromArgb(48, 43, 66) : hovering ? Color.FromArgb(43, 46, 56) : KeyColor;
            Color edge = chosen ? AccentColor : active ? Color.FromArgb(72, 126, 110) : hovering ? Color.FromArgb(88, 84, 111) : BorderColor;
            if (active && !chosen) { fillTop = Color.FromArgb(42, 57, 54); fillBottom = Color.FromArgb(35, 46, 44); }
            RectangleF shadow = bounds; shadow.Offset(0, 2.5f);
            using (GraphicsPath path = KeyPath(key, shadow))
            using (Brush brush = new SolidBrush(Color.FromArgb(11, 12, 16))) graphics.FillPath(brush, path);
            using (GraphicsPath path = KeyPath(key, bounds))
            {
                using (Brush brush = new LinearGradientBrush(bounds, fillTop, fillBottom, LinearGradientMode.Vertical)) graphics.FillPath(brush, path);
                if (state != null && state.Depth.HasValue && state.Depth.Value > 0)
                {
                    GraphicsState saved = graphics.Save(); graphics.SetClip(path, CombineMode.Intersect);
                    float height = (float)(bounds.Height * state.Depth.Value);
                    Color depthColor = chosen ? AccentColor : ActiveColor;
                    // One bottom-up level fills the actual cap outline, including
                    // the ISO Enter notch. The sample is drawn without smoothing.
                    using (Brush brush = new SolidBrush(Color.FromArgb(96, depthColor))) graphics.FillRectangle(brush, bounds.X, bounds.Bottom - height, bounds.Width, height);
                    graphics.Restore(saved);
                }
                using (Pen pen = new Pen(edge, chosen ? 1.5f : 1f)) graphics.DrawPath(pen, path);
                if (key.KeyIndex.HasValue && availableKeys.Contains(key.KeyIndex.Value))
                {
                    using (Brush wash = new SolidBrush(Color.FromArgb(22, PartnerColor))) graphics.FillPath(wash, path);
                    using (Pen partner = new Pen(PartnerColor, 1.4f)) graphics.DrawPath(partner, path);
                }
                // A faint upper bevel gives the cap depth without heavy borders.
                GraphicsState bevelState = graphics.Save(); graphics.SetClip(path, CombineMode.Intersect);
                using (Pen bevel = new Pen(Color.FromArgb(chosen ? 30 : 16, TextColor), 1f))
                    graphics.DrawLine(bevel, bounds.Left + 5, bounds.Top + 1.5f, bounds.Right - 5, bounds.Top + 1.5f);
                graphics.Restore(bevelState);
            }
            Rectangle textBounds = Rectangle.Round(bounds); textBounds.Inflate(-2, -2);
            if (textBounds.Width < 1 || textBounds.Height < 1) return;
            Rectangle legendBounds = textBounds;
            float badgeHeight = DrawControllerBadges(graphics, key, bounds);
            if (badgeHeight > 0)
            { int inset = Math.Min(textBounds.Height / 2, (int)Math.Ceiling(badgeHeight)); legendBounds.Y += inset; legendBounds.Height = Math.Max(1, legendBounds.Height - inset); }
            bool showPercent = active && state.Depth.HasValue && !state.Estimated && textBounds.Width >= 22 &&
                legendBounds.Height >= (int)Math.Ceiling(keyFont.GetHeight(graphics) + valueFont.GetHeight(graphics)) + 4;
            if (showPercent)
            {
                int valueHeight = Math.Min(textBounds.Height / 2, (int)Math.Ceiling(valueFont.GetHeight(graphics)) + 1);
                Rectangle valueBounds = new Rectangle(textBounds.Left, textBounds.Bottom - valueHeight - 3, textBounds.Width, valueHeight);
                legendBounds.Height = Math.Max(1, valueBounds.Top - legendBounds.Top - 1);
                TextRenderer.DrawText(graphics, PercentText(state.Depth.Value), valueFont, valueBounds,
                    chosen ? Color.FromArgb(223, 215, 255) : Color.FromArgb(175, 230, 211),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            TextRenderer.DrawText(graphics, GetKeyLabel(key), keyFont, legendBounds, chosen ? Color.FromArgb(235, 230, 255) : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (badgeHeight == 0 && state != null && state.Mapped)
            {
                float diameter = Math.Max(3, Math.Min(5, bounds.Width / 9));
                using (Brush brush = new SolidBrush(chosen ? Color.FromArgb(218, 207, 255) : AccentColor)) graphics.FillEllipse(brush, bounds.Right - diameter - 4, bounds.Top + 4, diameter, diameter);
            }
            if (!key.KeyIndex.HasValue)
                TextRenderer.DrawText(graphics, "?", keyFont, new Point((int)bounds.Right - 12, (int)bounds.Top + 2), MutedColor);
            if (Focused && focusCode == key.Code)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Round(RectangleF.Inflate(bounds, -4, -4)), AccentColor, fillBottom);
            if (key.KeyIndex.HasValue && dropKeys.Contains(key.KeyIndex.Value))
            {
                // A warm dashed outer outline is separate from the violet inner
                // selection border and the green pressure fill.
                using (GraphicsPath target = KeyPath(key, RectangleF.Inflate(bounds, 1.5f, 1.5f)))
                using (Pen marker = new Pen(DropColor, 2.5f) { DashStyle = DashStyle.Dash })
                    graphics.DrawPath(marker, target);
            }
        }
        float DrawControllerBadges(Graphics graphics, KeyboardKeyDefinition key, RectangleF bounds)
        {
            KeyboardControllerBadge[] assigned;
            if (!key.KeyIndex.HasValue || !controllerAssignments.TryGetValue(key.KeyIndex.Value, out assigned) || assigned.Length == 0 || bounds.Width < 12 || bounds.Height < 14) return 0;
            var ordered = new List<KeyboardControllerBadge>(assigned);
            ordered.Sort(delegate(KeyboardControllerBadge left, KeyboardControllerBadge right)
            { int selectedFirst = right.Selected.CompareTo(left.Selected); return selectedFirst != 0 ? selectedFirst : left.Number.CompareTo(right.Number); });
            // Keep the current player visible even on a narrow key. Overflow is
            // explicit (+N); the tooltip always contains every name and target.
            int slots = bounds.Width >= 70 ? 3 : 2;
            int direct = Math.Min(ordered.Count, slots);
            bool overflow = ordered.Count > slots; if (overflow) direct = slots - 1;
            var texts = new List<string>(); var highlighted = new List<bool>(); var tints = new List<Color>();
            for (int i = 0; i < direct; i++) { texts.Add(ordered[i].Number.ToString(CultureInfo.InvariantCulture)); highlighted.Add(ordered[i].Selected); tints.Add(ordered[i].Tint); }
            if (overflow) { texts.Add("+" + (ordered.Count - direct).ToString(CultureInfo.InvariantCulture)); highlighted.Add(false); tints.Add(Color.FromArgb(24, 27, 34)); }
            float fontSize = Math.Min(9f, Math.Max(6f, bounds.Height * .19f));
            float available = bounds.Width - 6, total = 0;
            using (Font measure = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                foreach (string text in texts) total += graphics.MeasureString(text, measure, Int32.MaxValue, StringFormat.GenericTypographic).Width + 6;
            if (total > available) fontSize *= available / total;
            using (Font font = new Font("Segoe UI", Math.Max(4f, fontSize), FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var widths = new List<float>(); total = 0;
                foreach (string text in texts) { float width = graphics.MeasureString(text, font, Int32.MaxValue, StringFormat.GenericTypographic).Width + 4; widths.Add(width); total += width + 2; }
                float height = font.GetHeight(graphics) + 1, x = Math.Max(bounds.Left + 2, bounds.Right - total - 1), y = bounds.Top + 3;
                GraphicsState saved = graphics.Save();
                using (GraphicsPath clip = KeyPath(key, bounds)) graphics.SetClip(clip, CombineMode.Intersect);
                for (int i = 0; i < texts.Count; i++)
                {
                    var area = new RectangleF(x, y, widths[i], height);
                    using (GraphicsPath pill = Rounded(area, 2))
                    using (Brush fill = new SolidBrush(tints[i]))
                    {
                        graphics.FillPath(fill, pill);
                        if (highlighted[i]) using (Pen border = new Pen(Color.FromArgb(210, 220, 230), .7f)) graphics.DrawPath(border, pill);
                    }
                    Color inkColor = tints[i].R * .299 + tints[i].G * .587 + tints[i].B * .114 > 155 ? Color.FromArgb(24, 26, 32) : Color.White;
                    using (Brush ink = new SolidBrush(inkColor))
                    using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        graphics.DrawString(texts[i], font, ink, area, format);
                    x += widths[i] + 2;
                }
                graphics.Restore(saved); return height + 3;
            }
        }
        static GraphicsPath KeyPath(KeyboardKeyDefinition key, RectangleF bounds)
        {
            if (key.Code == "Enter" && key.Bounds.Height > key.Bounds.Width)
            {
                // ISO return: the manufacturer's rectangle is the outer box; the
                // lower left notch expresses its physical two-row keycap shape.
                float notch = bounds.Width * .24f, shoulder = bounds.Height * .48f;
                GraphicsPath path = new GraphicsPath();
                path.AddPolygon(new[] { new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Top),
                    new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Left + notch, bounds.Bottom),
                    new PointF(bounds.Left + notch, bounds.Top + shoulder), new PointF(bounds.Left, bounds.Top + shoulder) });
                return path;
            }
            return Rounded(bounds, Math.Min(key.IsKnob ? 4 : 6, bounds.Width / 4));
        }
        static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath(); float diameter = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new KeyboardAccessibleObject(this); }
        sealed class KeyboardAccessibleObject : ControlAccessibleObject
        {
            readonly VisualKeyboard owner;
            public KeyboardAccessibleObject(VisualKeyboard owner) : base(owner) { this.owner = owner; }
            public override string Name { get { return UiText.Get("Tastatur", "Keyboard"); } set { } }
            public override int GetChildCount() { return owner.layout == null ? 0 : owner.layout.Keys.Count; }
            public override AccessibleObject GetChild(int index) { return index < 0 || index >= GetChildCount() ? null : new KeyAccessibleObject(owner, owner.layout.Keys[index], this); }
        }
        sealed class KeyAccessibleObject : AccessibleObject
        {
            readonly VisualKeyboard owner; readonly KeyboardKeyDefinition key; readonly AccessibleObject parent;
            public KeyAccessibleObject(VisualKeyboard owner, KeyboardKeyDefinition key, AccessibleObject parent) { this.owner = owner; this.key = key; this.parent = parent; }
            public override string Name { get { return owner.GetKeyLabel(key); } set { } }
            public override string Description { get { return owner.TooltipFor(key); } }
            public override string Value
            {
                get
                {
                    KeyState state;
                    return key.KeyIndex.HasValue && owner.states.TryGetValue(key.KeyIndex.Value, out state) && state.Depth.HasValue
                        ? (state.Estimated ? EstimatedDepthText() : PercentText(state.Depth.Value)) : UiText.Get("Drucktiefe unbekannt", "Depth unknown");
                }
                set { }
            }
            public override string DefaultAction { get { return key.KeyIndex.HasValue ? UiText.Get("Auswählen", "Select") : UiText.Get("Anlernen", "Identify key"); } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(owner.GetKeyBounds(key))); } }
            public override AccessibleStates State { get { return AccessibleStates.Focusable | AccessibleStates.Selectable | (key.KeyIndex.HasValue && owner.selected.Contains(key.KeyIndex.Value) ? AccessibleStates.Selected : AccessibleStates.None) | (owner.Focused && owner.focusCode == key.Code ? AccessibleStates.Focused : AccessibleStates.None); } }
            public override void DoDefaultAction() { if (!owner.IsDisposed) { owner.Focus(); owner.SelectKey(key, false); } }
        }
        protected override void Dispose(bool disposing) { if (disposing) { CancelDragGesture(); tooltip.Dispose(); } base.Dispose(disposing); }
    }
}
