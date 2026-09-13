using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed class LearnDialog : Form
    {
        readonly object gate = new object();
        readonly KeyLearner learner = new KeyLearner();
        readonly ReaderSession reader;
        readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(20) };
        readonly Timer timer = new Timer { Interval = 100 };
        readonly ProgressBar progress = new SleekProgressBar { Dock = DockStyle.Bottom, Height = 8, Maximum = 2 };
        int latest;
        public int KeyIndex { get; private set; }
        public LearnDialog(ReaderSession input, string label)
        {
            UiText.PreserveText(this); UiText.PreserveText(status);
            reader = input; Text = UiText.Get("Taste erkennen · ", "Identify key · ") + label; ClientSize = new Size(560, 270); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; Padding = new Padding(16);
            status.Text = String.Format(UiText.Get("Nur {0} zweimal langsam drücken und vollständig loslassen.\n\nDu bestimmst das Tempo.", "Slowly press only {0} all the way down and release fully, twice.\n\nGo at your own pace."), label); Controls.Add(status); Controls.Add(progress);
            var cancel = new SleekButton { Text = "Abbrechen", Dock = DockStyle.Bottom, Height = 42, DialogResult = DialogResult.Cancel }; Controls.Add(cancel); CancelButton = cancel;
            ModernTheme.Apply(this); UiText.Apply(this);
            Shown += delegate { reader.Sample += Feed; timer.Start(); };
            FormClosed += delegate { reader.Sample -= Feed; timer.Stop(); timer.Dispose(); };
            timer.Tick += delegate
            {
                lock (gate)
                {
                    if (!reader.IsReading) { status.Text = UiText.Get(reader.Status); return; }
                    progress.Value = learner.ReleasedCycles;
                    if (learner.State == KeyLearnerState.Completed) { KeyIndex = learner.KeyIndex; DialogResult = DialogResult.OK; Close(); }
                    else if (learner.State == KeyLearnerState.Ambiguous) status.Text = UiText.Get("Mehrere Tasten erkannt. Bitte abbrechen und nur die gewünschte Taste bewegen.");
                    else if (learner.State == KeyLearnerState.Pressing) status.Text = String.Format(UiText.Get("{0} erkannt · Messwert {1}\n\nJetzt vollständig loslassen.\nDurchgang {2} von 2", "{0} detected · reading {1}\n\nNow release fully.\nPass {2} of 2"), label, latest, learner.ReleasedCycles + 1);
                    else if (learner.State == KeyLearnerState.Armed) status.Text = String.Format(UiText.Get("Erster Durchgang geschafft.\n\n{0} noch einmal langsam drücken und vollständig loslassen.", "First pass completed.\n\nSlowly press {0} all the way down once more, then release fully."), label);
                    else status.Text = String.Format(UiText.Get("Drücke jetzt nur {0}.\n\n{1}", "Now press only {0}.\n\n{1}"), label, UiText.Get(reader.HasReceivedSamples ? "Noch keine Bewegung dieser Taste erkannt." : reader.Status));
                }
            };
        }
        void Feed(TravelSample sample, double elapsed) { lock (gate) { learner.Feed(sample, elapsed); if (sample.KeyIndex == learner.KeyIndex) latest = sample.RawValue; } }
    }

    public sealed class CurveCanvas : Control
    {
        SignalSettings settings;
        bool mixed, finishing;
        string contextKey;
        CurvePointEditing edit;
        CurveKind editKind;
        Point dragOrigin, lastMouse;
        double originX, originY;
        int hovered = -1, hoveredPart = -1, selectedPoint = -1, previousSelectedPoint = -1;
        readonly ToolTip tips = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 8000 };
        string lastTip;
        public SignalSettings Settings { get { return CopySettings(settings); } set { UpdateCurve(value, mixed, contextKey); } }
        public bool Mixed { get { return mixed; } set { UpdateCurve(settings, value, contextKey); } }
        public bool IsEditing { get { return edit != null && edit.Active; } }
        public event Action<List<CurvePoint>> Change;
        public event Action<SignalSettings> EditCompleted;
        public CurveCanvas()
        {
            DoubleBuffered = true; BackColor = ModernTheme.Surface; MinimumSize = new Size(140, 120); TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        }
        // Use one stable context key for profile + selected bindings. Identical
        // cloned settings from a parent refresh must not discard a local drag.
        public void UpdateCurve(SignalSettings value, bool isMixed, string selectionContext)
        {
            var next = CopySettings(value);
            bool contextChanged = contextKey != selectionContext;
            bool changed = contextChanged || mixed != isMixed || !SameSettings(settings, next);
            if (changed) { CancelEdit(); hovered = hoveredPart = -1; }
            settings = next; mixed = isMixed; contextKey = selectionContext;
            if (!IsEditing && (contextChanged || settings == null || !HasNodes(settings.Curve) || settings.CustomPoints == null || selectedPoint >= settings.CustomPoints.Count)) selectedPoint = -1;
            UpdateTip(); Invalidate();
        }
        static SignalSettings CopySettings(SignalSettings value)
        {
            return value == null ? null : new SignalSettings { Curve = value.Curve, Exponent = value.Exponent,
                CustomPoints = value.CustomPoints == null ? null : value.CustomPoints.Select(p => p == null ? null : new CurvePoint(p.X, p.Y) { Tangent = p.Tangent }).ToList() };
        }
        static bool SameSettings(SignalSettings first, SignalSettings second)
        {
            return first == null || second == null ? first == null && second == null :
                first.Curve == second.Curve && first.Exponent == second.Exponent && CurvePointEditing.Same(first.CustomPoints, second.CustomPoints);
        }
        RectangleF Plot
        {
            get
            {
                float width = Math.Max(20, Width - 63), height = Math.Max(20, Height - 88);
                float side = Math.Min(width, height);
                return new RectangleF(42 + (width - side) / 2, 36 + (height - side) / 2, side, side);
            }
        }
        float HitRadius { get { return Math.Max(9, Font.Height * .6f); } }
        static bool HasNodes(CurveKind kind) { return kind == CurveKind.Custom || kind == CurveKind.Bezier; }
        CurvePointEditing NewEditor()
        {
            if (settings == null) return null;
            try { return new CurvePointEditing(HasNodes(settings.Curve) ? settings.CustomPoints : new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(1, 1) }); }
            catch (ArgumentException) { return null; }
        }
        int Hit(CurvePointEditing editor, Point location, out int index)
        {
            index = -1; if (editor == null) return -1; RectangleF plot = Plot;
            return editor.HitPart(settings != null && settings.Curve == CurveKind.Bezier ? selectedPoint : -1,
                location.X, location.Y, plot.Left, plot.Top, plot.Width, plot.Height, HitRadius, out index);
        }
        PointF PlotPoint(CurvePoint point)
        { RectangleF plot = Plot; return new PointF(plot.Left + (float)point.X * plot.Width, plot.Bottom - (float)point.Y * plot.Height); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; RectangleF plot = Plot;
            using (var muted = new SolidBrush(ModernTheme.Muted))
            {
                g.DrawString(UiText.Get(Mixed ? "Gemischt · erste Zuordnung" : "Antwortkurve"), Font, muted, 8, 8);
                g.DrawString("0", Font, muted, 24, plot.Bottom - 8); g.DrawString("100 %", Font, muted, 1, plot.Top - 6); g.DrawString("100 %", Font, muted, plot.Right - 39, plot.Bottom + 7);
                if (settings == null) g.DrawString(UiText.Get("Zuordnung auswählen"), Font, muted, plot.Left, plot.Top + 20);
            }
            using (var grid = new Pen(ModernTheme.Border)) for (int i = 0; i <= 4; i++) { float t = i / 4f; g.DrawLine(grid, plot.Left + t * plot.Width, plot.Top, plot.Left + t * plot.Width, plot.Bottom); g.DrawLine(grid, plot.Left, plot.Bottom - t * plot.Height, plot.Right, plot.Bottom - t * plot.Height); }
            if (settings == null) return;
            var shown = IsEditing ? new SignalSettings { Curve = editKind, CustomPoints = edit.Preview } : settings;
            var points = new PointF[101];
            for (int i = 0; i <= 100; i++)
            {
                // A static response graph intentionally excludes history-dependent filtering.
                var copy = new SignalSettings { Curve = shown.Curve, Exponent = shown.Exponent, CustomPoints = shown.CustomPoints };
                var response = SignalProcessor.Process(i / 100.0, new Calibration(0, 1), copy, new SignalState(), 1);
                points[i] = new PointF(plot.Left + i / 100f * plot.Width, plot.Bottom - (float)response.Final * plot.Height);
            }
            using (var pen = new Pen(ModernTheme.Accent, 2.5f)) g.DrawLines(pen, points);
            if (shown.Curve == CurveKind.Bezier) DrawHandles(g, shown);
            if (HasNodes(shown.Curve) && shown.CustomPoints != null)
            {
                using (var normal = new SolidBrush(ModernTheme.Foreground))
                using (var selected = new SolidBrush(ModernTheme.Accent))
                using (var fixedPoint = new SolidBrush(ModernTheme.Muted))
                    for (int i = 0; i < shown.CustomPoints.Count; i++)
                    {
                        CurvePoint point = shown.CustomPoints[i]; if (point == null) continue;
                        bool endpoint = i == 0 || i == shown.CustomPoints.Count - 1;
                        bool active = i == selectedPoint || i == hovered;
                        float radius = active ? 6 : 4.5f;
                        g.FillEllipse(active ? selected : endpoint ? fixedPoint : normal, plot.Left + (float)point.X * plot.Width - radius, plot.Bottom - (float)point.Y * plot.Height - radius, radius * 2, radius * 2);
                    }
            }
            string hint = IsEditing ? UiText.Get("Loslassen: übernehmen · Esc: abbrechen", "Release: apply · Esc: cancel") : shown.Curve == CurveKind.Bezier ?
                UiText.Get("Punkt wählen → Griffe ziehen · Rechtsklick auf Griff: automatisch", "Select a point → drag handles · Right-click handle: automatic") :
                UiText.Get("Punkt setzen oder ziehen · Rechtsklick: löschen", "Add or drag a point · Right-click: delete");
            TextRenderer.DrawText(g, hint, Font, new Rectangle(8, Height - 24, Math.Max(1, Width - 16), 20), ModernTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        void DrawHandles(Graphics graphics, SignalSettings shown)
        {
            if (shown.CustomPoints == null || selectedPoint < 0 || selectedPoint >= shown.CustomPoints.Count) return;
            CurvePoint node = shown.CustomPoints[selectedPoint]; if (node == null) return;
            PointF center = PlotPoint(node);
            for (int side = 0; side < 2; side++)
            {
                CurvePoint handle;
                try { handle = BezierCurve.GetHandle(shown.CustomPoints, selectedPoint, side == 1); }
                catch (ArgumentException) { return; }
                if (handle == null) continue; PointF position = PlotPoint(handle);
                bool active = IsEditing ? edit.EditingHandle && hoveredPart == side + 1 : hoveredPart == side + 1;
                using (var line = new Pen(ModernTheme.Muted, 1.2f)) graphics.DrawLine(line, center, position);
                float radius = active ? 5.5f : 4;
                using (var fill = new SolidBrush(active ? ModernTheme.AccentHover : ModernTheme.SurfaceAlt))
                using (var edge = new Pen(ModernTheme.AccentHover, 1.5f))
                { graphics.FillRectangle(fill, position.X - radius, position.Y - radius, radius * 2, radius * 2); graphics.DrawRectangle(edge, position.X - radius, position.Y - radius, radius * 2, radius * 2); }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (settings == null || IsEditing || (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)) return;
            Focus(); var candidate = NewEditor(); if (candidate == null) return;
            int hit; int part = Hit(candidate, e.Location, out hit); RectangleF plot = Plot;
            CurveKind kind = settings.Curve == CurveKind.Custom ? CurveKind.Custom : CurveKind.Bezier;
            if (e.Button == MouseButtons.Right)
            {
                var changed = part > 0 ? candidate.ResetTangent(hit) : part == 0 && HasNodes(settings.Curve) ? candidate.Remove(hit) : null;
                if (changed != null) { if (part == 0) selectedPoint = -1; CompleteEdit(changed, kind); }
                hovered = hoveredPart = -1; UpdateTip(); Invalidate(); return;
            }
            if (part < 0 && !plot.Contains(e.Location)) return;
            double x = (e.X - plot.Left) / plot.Width, y = (plot.Bottom - e.Y) / plot.Height;
            previousSelectedPoint = selectedPoint;
            if (part >= 0) selectedPoint = hit;
            bool started = part > 0 ? candidate.BeginHandle(hit, part == 2) : candidate.Begin(hit, x, y, Math.Min(.02, 2.0 / plot.Width));
            if (!started) { hovered = hit; hoveredPart = part; UpdateTip(); Invalidate(); return; }
            edit = candidate; editKind = kind; selectedPoint = edit.SelectedIndex;
            var point = part > 0 ? edit.GetHandle(selectedPoint, part == 2) : edit.Preview[selectedPoint]; originX = point.X; originY = point.Y;
            dragOrigin = lastMouse = e.Location; hovered = selectedPoint; hoveredPart = Math.Max(0, part); Capture = true; Cursor = Cursors.SizeAll;
            UpdateTip(); Invalidate();
        }
        void MovePreview(Point location)
        {
            if (!IsEditing || location == lastMouse) return;
            lastMouse = location; RectangleF plot = Plot;
            edit.Move(originX + (location.X - dragOrigin.X) / (double)plot.Width, originY - (location.Y - dragOrigin.Y) / (double)plot.Height);
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsEditing) { MovePreview(e.Location); return; }
            var candidate = NewEditor(); int hit; int part = Hit(candidate, e.Location, out hit);
            if (hit != hovered || part != hoveredPart) { hovered = hit; hoveredPart = part; UpdateTip(); Invalidate(); }
            bool movable = candidate != null && hit > 0 && hit < candidate.Preview.Count - 1;
            Cursor = part > 0 || movable || part == 0 && settings != null && settings.Curve == CurveKind.Bezier ? Cursors.Hand : settings != null && Plot.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !IsEditing) return;
            MovePreview(e.Location); List<CurvePoint> changed = edit.Finish(); edit = null;
            finishing = true; try { Capture = false; } finally { finishing = false; }
            Cursor = Cursors.Default; UpdateTip(); Invalidate();
            // End capture/local state before invoking the parent: its single
            // commit can rebuild controls without interrupting the gesture.
            if (changed != null) CompleteEdit(changed, editKind);
        }
        void CompleteEdit(List<CurvePoint> points, CurveKind kind)
        {
            var result = new SignalSettings { Curve = kind, CustomPoints = points };
            // Prefer the atomic kind+points contract; the old event is fallback
            // only, so a consumer subscribing to both never commits twice.
            if (EditCompleted != null) EditCompleted(result);
            else if (Change != null) Change(points);
        }
        public void CancelEdit()
        {
            if (edit != null) { edit.Cancel(); edit = null; selectedPoint = previousSelectedPoint; }
            if (Capture) { finishing = true; try { Capture = false; } finally { finishing = false; } }
            Cursor = Cursors.Default; Invalidate();
        }
        void UpdateTip()
        {
            string text = IsEditing ? UiText.Get("Ziehe den Punkt oder Griff. Loslassen übernimmt die ganze Bewegung einmal. Esc verwirft sie.", "Drag the point or handle. Releasing applies the whole movement once. Esc cancels it.") :
                settings == null ? UiText.Get("Zuerst eine Zuordnung auswählen.", "Select a mapping first.") :
                settings.Curve == CurveKind.Bezier ? UiText.Get("Punkt anklicken: verbundene Griffe zeigen. Griff ziehen: Krümmung ändern. Rechtsklick auf Griff: automatisch glätten. Innere Punkte lassen sich ziehen und per Rechtsklick löschen; Endpunkte bleiben fest.", "Click a point to show linked handles. Drag a handle to shape the curve. Right-click a handle for automatic smoothing. Drag or right-click interior points to move or delete them; endpoints stay fixed.") :
                hovered == 0 || HasNodes(settings.Curve) && settings.CustomPoints != null && hovered == settings.CustomPoints.Count - 1 ?
                    UiText.Get("Die Endpunkte (0 %, 0 %) und (100 %, 100 %) bleiben fest.", "Endpoints (0%, 0%) and (100%, 100%) stay fixed.") :
                    UiText.Get("Klicken: eigenen Punkt setzen. Punkt ziehen: verschieben. Rechtsklick direkt auf einen inneren Punkt: löschen. Die Kurve bleibt ansteigend.", "Click to add a custom point. Drag a point to move it. Right-click directly on an interior point to delete it. The curve stays nondecreasing.");
            if (text != lastTip) { lastTip = text; tips.SetToolTip(this, text); }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); UpdateTip(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!IsEditing) { hovered = hoveredPart = -1; UpdateTip(); Invalidate(); } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !finishing && IsEditing) { CancelEdit(); UpdateTip(); } }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (IsEditing) { CancelEdit(); UpdateTip(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) CancelEdit(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) CancelEdit(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (IsEditing) CancelEdit(); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (IsEditing && keyData == Keys.Escape) { CancelEdit(); UpdateTip(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing) { if (disposing) { CancelEdit(); tips.Dispose(); } base.Dispose(disposing); }
    }
}
