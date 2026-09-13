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

    public sealed partial class CurveCanvas : Control
    {
        SignalSettings settings, editableShape;
        bool shapeFitAttempted, shapeFitUnavailable;
        bool mixed, finishing;
        string contextKey;
        CurvePointEditing edit;
        CurveKind editKind;
        Point dragOrigin, lastMouse;
        double originX, originY;
        int hovered = -1, hoveredPart = -1, selectedPoint = -1, previousSelectedPoint = -1;
        double? inspectedInput;
        SignalSettings sampledCurve;
        double[] responseSamples;
        CurveResponsePreview sampledResponse;
        bool sampledShape;
        public SignalSettings Settings { get { return CopySettings(settings); } set { UpdateCurve(value, mixed, contextKey); } }
        public bool Mixed { get { return mixed; } set { UpdateCurve(settings, value, contextKey); } }
        public bool IsEditing { get { return edit != null && edit.Active; } }
        public event Action<List<CurvePoint>> Change;
        public event Action<SignalSettings> EditCompleted;
        public CurveCanvas()
        {
            DoubleBuffered = true; BackColor = ModernTheme.Surface; MinimumSize = new Size(140, 120); TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BuildViewButton();
        }
        // Use one stable context key for profile + selected bindings. Identical
        // cloned settings from a parent refresh must not discard a local drag.
        public void UpdateCurve(SignalSettings value, bool isMixed, string selectionContext)
        {
            var next = CopySettings(value);
            bool contextChanged = contextKey != selectionContext;
            bool changed = contextChanged || mixed != isMixed || !SameSettings(settings, next);
            bool shapeChanged = settings == null || next == null || settings.Curve != next.Curve || settings.Exponent != next.Exponent ||
                !CurvePointEditing.Same(settings.CustomPoints, next.CustomPoints);
            if (changed) { CancelEdit(); hovered = hoveredPart = -1; }
            if (shapeChanged) { editableShape = null; shapeFitAttempted = shapeFitUnavailable = false; }
            if (contextChanged) { inspectedInput = null; activeSetting = null; activeInputField = InputActivationFields.None; }
            settings = next; mixed = isMixed; contextKey = selectionContext;
            var editable = shapeEditing ? EditableShape() : settings;
            if (shapeEditing && editable == null) shapeEditing = false;
            if (!IsEditing && (contextChanged || editable == null || !HasNodes(editable.Curve) || editable.CustomPoints == null || selectedPoint >= editable.CustomPoints.Count)) selectedPoint = -1;
            UpdateViewButton(); UpdateTip(); Invalidate();
        }
        static SignalSettings CopySettings(SignalSettings value)
        {
            return CurveResponsePreview.Copy(value);
        }
        static bool SameSettings(SignalSettings first, SignalSettings second)
        {
            return CurveResponsePreview.Same(first, second);
        }
        RectangleF Plot
        {
            get
            {
                float top = Math.Max(46, viewButton.Bottom + 17);
                float width = Math.Max(20, Width - 63), height = Math.Max(20, Height - top - 52);
                float side = Math.Min(width, height);
                return new RectangleF(42 + (width - side) / 2, top + (height - side) / 2, side, side);
            }
        }
        float HitRadius { get { return Math.Max(9, Font.Height * .6f); } }
        static bool HasNodes(CurveKind kind) { return kind == CurveKind.Custom || kind == CurveKind.Bezier; }
        SignalSettings EditableShape()
        {
            if (settings == null || HasNodes(settings.Curve)) return settings;
            if (!shapeFitAttempted)
            {
                // A view-only, detached fit. Merely opening Shape, selecting a
                // handle, or canceling a drag never converts the saved mapping.
                shapeFitAttempted = true;
                List<CurvePoint> points;
                if (CurveBezierEditing.TryCreate(settings, out points))
                {
                    editableShape = CopySettings(settings);
                    editableShape.CustomPoints = points;
                    editableShape.Curve = CurveKind.Bezier;
                }
                else shapeFitUnavailable = true;
            }
            return editableShape;
        }
        string ShapeUnavailableText()
        { return UiText.Get("Form zu steil für Bézier. Krümmung zuerst reduzieren.", "Shape too steep for Bézier. Reduce curvature first."); }
        CurvePointEditing NewEditor()
        {
            if (settings == null) return null;
            try { var editable = EditableShape(); return editable == null ? null : new CurvePointEditing(editable.CustomPoints); }
            catch (ArgumentException) { return null; }
        }
        int Hit(CurvePointEditing editor, Point location, out int index)
        {
            index = -1; if (editor == null) return -1; RectangleF plot = Plot;
            return editor.HitPart(settings != null && EditableShape().Curve == CurveKind.Bezier ? selectedPoint : -1,
                location.X, location.Y, plot.Left, plot.Top, plot.Width, plot.Height, HitRadius, out index);
        }
        PointF PlotPoint(CurvePoint point)
        { RectangleF plot = Plot; return new PointF(plot.Left + (float)point.X * plot.Width, plot.Bottom - (float)point.Y * plot.Height); }
        double[] CurveSamples(SignalSettings shown)
        {
            if (responseSamples != null && sampledShape == shapeEditing && CurveResponsePreview.SameResponse(sampledCurve, shown)) return responseSamples;
            sampledCurve = CopySettings(shown); sampledShape = shapeEditing;
            var displayed = shapeEditing ? new SignalSettings { Curve = shown.Curve, Exponent = shown.Exponent, CustomPoints = shown.CustomPoints } : shown;
            sampledResponse = CurveResponsePreview.Create(displayed, 256); responseSamples = sampledResponse.Press;
            return responseSamples;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; RectangleF plot = Plot;
            if (DrawDynamicPreview(g)) return;
            using (var muted = new SolidBrush(ModernTheme.Muted))
            {
                TextRenderer.DrawText(g, shapeEditing ? UiText.Get("Form", "Shape") : UiText.Get("Antwort", "Response"), Font,
                    new Rectangle(8, 5, Math.Max(1, viewButton.Left - 12), 24), ModernTheme.Foreground, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                g.DrawString("0", Font, muted, plot.Left + 3, plot.Bottom - Font.Height - 1); g.DrawString("100 %", Font, muted, plot.Left + 3, plot.Top + 1); g.DrawString("100 %", Font, muted, plot.Right - 45, plot.Bottom + 7);
                if (plot.Width >= 110) { g.DrawString("50", Font, muted, plot.Left + plot.Width / 2 - 8, plot.Bottom + 7); g.DrawString("50", Font, muted, plot.Left + 3, plot.Top + plot.Height / 2 - 8); }
                if (settings == null) g.DrawString(UiText.Get("Zuordnung auswählen"), Font, muted, plot.Left, plot.Top + 20);
            }
            DrawRangeRailLabels(g);
            using (var grid = new Pen(ModernTheme.Border)) for (int i = 0; i <= 4; i++) { float t = i / 4f; g.DrawLine(grid, plot.Left + t * plot.Width, plot.Top, plot.Left + t * plot.Width, plot.Bottom); g.DrawLine(grid, plot.Left, plot.Bottom - t * plot.Height, plot.Right, plot.Bottom - t * plot.Height); }
            if (settings == null) return;
            var shown = CopySettings(shapeEditing ? EditableShape() : settings);
            if (IsEditing) { shown.Curve = editKind; shown.CustomPoints = edit.Preview; }
            double[] samples = CurveSamples(shown); var points = new PointF[samples.Length];
            if (!shapeEditing) DrawResponseGuides(g, shown);
            for (int i = 0; i < samples.Length; i++)
            {
                points[i] = new PointF(plot.Left + i / (float)(samples.Length - 1) * plot.Width, plot.Bottom - (float)samples[i] * plot.Height);
            }
            if (!shapeEditing && shown.Hysteresis > 0) DrawReleaseResponse(g, sampledResponse.Release);
            using (var pen = new Pen(ModernTheme.Accent, 2.5f)) g.DrawLines(pen, points);
            if (shapeEditing && shown.Curve == CurveKind.Bezier) DrawHandles(g, shown);
            if (shapeEditing && HasNodes(shown.Curve) && shown.CustomPoints != null)
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
            DrawInspectedPoint(g, shown, points);
            string hint = !shapeEditing ? ResponseCaption(shown) : IsEditing ? UiText.Get("Loslassen: übernehmen · Esc: abbrechen", "Release: apply · Esc: cancel") : shown.Curve == CurveKind.Bezier ?
                UiText.Get("Punkt wählen → Griffe ziehen · Rechtsklick auf Griff: automatisch", "Select a point → drag handles · Right-click handle: automatic") :
                UiText.Get("Punkt setzen oder ziehen · Rechtsklick: löschen", "Add or drag a point · Right-click: delete");
            TextRenderer.DrawText(g, hint, Font, new Rectangle(8, Height - 24, Math.Max(1, Width - 16), 20), ModernTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        void DrawInspectedPoint(Graphics graphics, SignalSettings shown, PointF[] points)
        {
            CurvePoint selected = null;
            int index = IsEditing ? selectedPoint : hoveredPart == 0 ? hovered : selectedPoint;
            if (shapeEditing && HasNodes(shown.Curve) && shown.CustomPoints != null && index >= 0 && index < shown.CustomPoints.Count) selected = shown.CustomPoints[index];
            if (selected == null && !inspectedInput.HasValue) return;
            int sample = inspectedInput.HasValue ? Math.Max(0, Math.Min(points.Length - 1, (int)Math.Round(inspectedInput.Value * (points.Length - 1)))) : 0;
            PointF position = selected == null ? points[sample] : PlotPoint(selected); RectangleF plot = Plot;
            using (var guide = new Pen(ModernTheme.Muted, 1))
            { guide.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot; graphics.DrawLine(guide, plot.Left, position.Y, position.X, position.Y); graphics.DrawLine(guide, position.X, position.Y, position.X, plot.Bottom); }
            // An inspection cursor is a crosshair, never a hollow draggable node.
            using (var edge = new Pen(ModernTheme.AccentHover, 1.5f))
            { graphics.DrawLine(edge, position.X - 4, position.Y, position.X + 4, position.Y); graphics.DrawLine(edge, position.X, position.Y - 4, position.X, position.Y + 4); }
            // Keep unobtrusive axis guides without covering the curve or its
            // handles with a floating tooltip-style readout.
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
            if (!shapeEditing || settings == null || IsEditing || (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)) return;
            Focus(); var candidate = NewEditor(); if (candidate == null) return;
            int hit; int part = Hit(candidate, e.Location, out hit); RectangleF plot = Plot;
            CurveKind kind = EditableShape().Curve;
            if (e.Button == MouseButtons.Right)
            {
                var changed = part > 0 ? candidate.ResetTangent(hit) : part == 0 ? candidate.Remove(hit) : null;
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
            UpdateTip(); Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (DynamicPreviewVisible) { Cursor = Cursors.Default; return; }
            if (IsEditing) { MovePreview(e.Location); return; }
            var candidate = shapeEditing ? NewEditor() : null; int hit; int part = Hit(candidate, e.Location, out hit);
            double? inspected = settings != null && Plot.Contains(e.Location) ? (double?)Math.Round((e.X - Plot.Left) / Plot.Width, 2) : null;
            if (hit != hovered || part != hoveredPart || inspected != inspectedInput) { hovered = hit; hoveredPart = part; inspectedInput = inspected; UpdateTip(); Invalidate(); }
            bool movable = candidate != null && hit > 0 && hit < candidate.Preview.Count - 1;
            Cursor = part > 0 || movable || part == 0 && settings != null && EditableShape().Curve == CurveKind.Bezier ? Cursors.Hand : settings != null && Plot.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
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
            var result = CopySettings(settings); result.Curve = kind; result.CustomPoints = points;
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
            string text = DynamicPreviewVisible ? DynamicPreviewHelp() : !shapeEditing ? ResponseHelp(settings) : IsEditing ? UiText.Get("Ziehe den Punkt oder Griff. Loslassen übernimmt die ganze Bewegung einmal. Esc verwirft sie.", "Drag the point or handle. Releasing applies the whole movement once. Esc cancels it.") :
                settings == null ? UiText.Get("Zuerst eine Zuordnung auswählen.", "Select a mapping first.") :
                settings.Curve == CurveKind.Bezier ? UiText.Get("Punkt anklicken: verbundene Griffe zeigen. Griff ziehen: Krümmung ändern. Rechtsklick auf Griff: automatisch glätten. Innere Punkte lassen sich ziehen und per Rechtsklick löschen; Endpunkte bleiben fest.", "Click a point to show linked handles. Drag a handle to shape the curve. Right-click a handle for automatic smoothing. Drag or right-click interior points to move or delete them; endpoints stay fixed.") :
                hovered == 0 || HasNodes(settings.Curve) && settings.CustomPoints != null && hovered == settings.CustomPoints.Count - 1 ?
                    UiText.Get("Die Endpunkte (0 %, 0 %) und (100 %, 100 %) bleiben fest.", "Endpoints (0%, 0%) and (100%, 100%) stay fixed.") :
                    UiText.Get("Klicken: eigenen Punkt setzen. Punkt ziehen: verschieben. Rechtsklick direkt auf einen inneren Punkt: löschen. Die Kurve bleibt ansteigend.", "Click to add a custom point. Drag a point to move it. Right-click directly on an interior point to delete it. The curve stays nondecreasing.");
            if (shapeEditing) text += "\n\n" + UiText.Get("Form-Editor: Horizontal Eingabe nach den Totbereichen, vertikal Kurvenergebnis, jeweils 0–100 %. Nur eigene Punkte und Bézier-Griffe lassen sich ziehen. Mit Antwort siehst du wieder das Ergebnis einschließlich der anderen Einstellungen.",
                "Shape editor: horizontal input after deadzones, vertical curve result, both 0–100%. Only custom points and Bézier handles are draggable. Response returns to the output including the other settings.");
            AccessibleDescription = shapeFitUnavailable ? ShapeUnavailableText() + " " + text : text;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); UpdateTip(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!IsEditing) { hovered = hoveredPart = -1; inspectedInput = null; UpdateTip(); Invalidate(); } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !finishing && IsEditing) { CancelEdit(); UpdateTip(); } }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (IsEditing) { CancelEdit(); UpdateTip(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) CancelEdit(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) CancelEdit(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (IsEditing) CancelEdit(); UpdateViewButton(); PositionRangeRails(); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (IsEditing && keyData == Keys.Escape) { CancelEdit(); UpdateTip(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing) { if (disposing) CancelEdit(); base.Dispose(disposing); }
    }
}
