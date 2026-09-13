using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Tk75.App
{
    internal sealed class CurveSettingRange
    {
        public string Property;
        public double Minimum, Maximum, Step;
        public double FirstValue;
        public bool Available;

        public double Clamp(double value)
        { return Math.Max(Minimum, Math.Min(Maximum, value)); }
        public double FromFraction(double fraction)
        {
            if (fraction <= 0) return Minimum;
            if (fraction >= 1) return Maximum;
            return Clamp(Math.Round((Minimum + fraction * (Maximum - Minimum)) / Step) * Step);
        }
        public string Display(double value)
        {
            if (Property == "SmoothingTimeConstant") return (value * 1000).ToString("0.##", CultureInfo.CurrentCulture) + " ms";
            if (Property == "Exponent" || Property == "Scale") return value.ToString("0.###", CultureInfo.CurrentCulture) + " ×";
            return (value * 100).ToString("0.##", CultureInfo.CurrentCulture) + " %";
        }
    }

    internal sealed class CurveSettingSliderCell : CurveSettingTextCell
    {
        public CurveSettingRange Range;
        public CurveSettingSliderCell() { ValueType = typeof(double); }
        public override object Clone()
        { var cell = (CurveSettingSliderCell)base.Clone(); cell.Range = Range; return cell; }
        public static Rectangle Track(Rectangle cell)
        { return new Rectangle(cell.Left + 11, cell.Top + cell.Height / 2, Math.Max(1, cell.Width - 22), 4); }
        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
            DataGridViewElementStates cellState, object value, object formattedValue, string errorText,
            DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText,
                cellStyle, advancedBorderStyle, paintParts & ~DataGridViewPaintParts.ContentForeground);
            if (Range == null) return;
            bool available = Range.Available && DataGridView.Enabled;
            Rectangle track = Track(cellBounds); Color ink = available ? ModernTheme.Accent : ModernTheme.Muted;
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(ModernTheme.Border, 4)) { pen.StartCap = pen.EndCap = LineCap.Round; graphics.DrawLine(pen, track.Left, track.Top, track.Right, track.Top); }
            bool mixed = value == null;
            if (available && !mixed)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                double fraction = Range.Maximum > Range.Minimum ? (Range.Clamp(number) - Range.Minimum) / (Range.Maximum - Range.Minimum) : 0;
                float x = track.Left + (float)fraction * track.Width;
                using (var pen = new Pen(ink, 4)) { pen.StartCap = pen.EndCap = LineCap.Round; graphics.DrawLine(pen, track.Left, track.Top, x, track.Top); }
                using (var brush = new SolidBrush(ink)) graphics.FillEllipse(brush, x - 5, track.Top - 5, 10, 10);
                using (var edge = new Pen(ModernTheme.Surface, 1.5f)) graphics.DrawEllipse(edge, x - 5, track.Top - 5, 10, 10);
            }
            graphics.SmoothingMode = previous;
            // The adjacent Value column carries the exact number or Mixed.
            // Omit a redundant label and any invented thumb for mixed values.
        }
    }

    // A slider gesture owns a local cell value. Only its completed event may
    // update the profile/runtime; cancellation restores the exact mixed/value state.
    internal sealed class CurveSettingsGrid : DataGridView
    {
        CurveSettingSliderCell editingSlider;
        object originalValue;
        double draftValue;
        bool mouseGesture, finishing, draftChanged;
        SliderDragPrecision pointerDrag;
        public event Action<string> SliderStarted;
        public event Action<string, double> SliderPreviewed;
        public event Action<string, double> SliderCommitted;
        public event Action SliderCanceled;
        public bool SliderEditing { get { return editingSlider != null; } }
        public CurveSettingsGrid() { DoubleBuffered = true; }

        bool BeginSlider(CurveSettingSliderCell cell, bool mouse)
        {
            if (cell == null || cell.Range == null || !cell.Range.Available || !Enabled || cell.Range.Maximum < cell.Range.Minimum) return false;
            if (editingSlider == cell) return true;
            CancelSlider();
            editingSlider = cell; originalValue = cell.Value; draftValue = cell.Range.Clamp(cell.Value == null ? cell.Range.FirstValue : Convert.ToDouble(cell.Value, CultureInfo.InvariantCulture));
            mouseGesture = mouse; draftChanged = false;
            if (SliderStarted != null) SliderStarted(cell.Range.Property);
            return editingSlider == cell;
        }
        void Preview(double value)
        {
            if (editingSlider == null) return;
            value = editingSlider.Range.Clamp(value);
            if (editingSlider.Value != null && value == draftValue) return;
            draftValue = value; editingSlider.Value = value; draftChanged = true;
            if (SliderPreviewed != null) SliderPreviewed(editingSlider.Range.Property, value);
            InvalidateCell(editingSlider);
        }
        void FinishSlider()
        {
            if (editingSlider == null) return;
            CurveSettingSliderCell cell = editingSlider; object original = originalValue; double value = draftValue;
            editingSlider = null; originalValue = null;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            if (draftChanged && cell.Value != null && (original == null || Convert.ToDouble(original, CultureInfo.InvariantCulture) != value))
            { if (SliderCommitted != null) SliderCommitted(cell.Range.Property, value); }
            else if (SliderCanceled != null) SliderCanceled();
        }
        public void CancelSlider()
        {
            if (editingSlider == null) return;
            CurveSettingSliderCell cell = editingSlider; editingSlider = null; cell.Value = originalValue; originalValue = null;
            finishing = true; try { if (Capture) Capture = false; } finally { finishing = false; }
            if (cell.DataGridView == this) InvalidateCell(cell);
            if (SliderCanceled != null) SliderCanceled();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            HitTestInfo hit = HitTest(e.X, e.Y);
            if (hit.RowIndex < 0 || hit.ColumnIndex < 0) return;
            var cell = Rows[hit.RowIndex].Cells[hit.ColumnIndex] as CurveSettingSliderCell;
            if (!BeginSlider(cell, true)) return;
            Rectangle track = CurveSettingSliderCell.Track(GetCellDisplayRectangle(hit.ColumnIndex, hit.RowIndex, false));
            float thumb = track.Left + (float)((draftValue - cell.Range.Minimum) / Math.Max(.00000001, cell.Range.Maximum - cell.Range.Minimum)) * track.Width;
            bool preserveThumb = cell.Value != null && Math.Abs(e.X - thumb) <= 7;
            if (!preserveThumb) Preview(cell.Range.FromFraction((double)(e.X - track.Left) / track.Width));
            pointerDrag.Begin(draftValue, e.X, track.Top, Math.Max(1, Font.Height / 15.0));
            Capture = true;
        }
        void MoveSlider(int x, int y)
        {
            if (editingSlider == null) return;
            Rectangle track = CurveSettingSliderCell.Track(GetCellDisplayRectangle(editingSlider.ColumnIndex, editingSlider.RowIndex, false));
            CurveSettingRange range = editingSlider.Range;
            if (!pointerDrag.Move(x, y, (range.Maximum - range.Minimum) / track.Width, range.Minimum, range.Maximum)) return;
            Preview(pointerDrag.QuantizedValue(range.Step, range.Minimum, range.Maximum));
        }
        protected override void OnMouseMove(MouseEventArgs e)
        { if (editingSlider != null && mouseGesture) MoveSlider(e.X, e.Y); else base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && editingSlider != null && mouseGesture) { MoveSlider(e.X, e.Y); FinishSlider(); }
            base.OnMouseUp(e);
        }
        bool SliderKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && SliderEditing) { CancelSlider(); e.Handled = e.SuppressKeyPress = true; return true; }
            if (e.KeyCode == Keys.Enter && SliderEditing) { FinishSlider(); e.Handled = e.SuppressKeyPress = true; return true; }
            if (e.Control || e.Alt || mouseGesture && SliderEditing) return false;
            if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right && e.KeyCode != Keys.Home && e.KeyCode != Keys.End && e.KeyCode != Keys.PageUp && e.KeyCode != Keys.PageDown) return false;
            var cell = CurrentCell as CurveSettingSliderCell;
            if (!BeginSlider(cell, false)) return false;
            double step = cell.Range.Step * (e.Shift || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown ? 10 : 1);
            double value = e.KeyCode == Keys.Home ? cell.Range.Minimum : e.KeyCode == Keys.End ? cell.Range.Maximum :
                draftValue + (e.KeyCode == Keys.Left || e.KeyCode == Keys.PageDown ? -step : step);
            Preview(Math.Round(value, 10)); e.Handled = e.SuppressKeyPress = true; return true;
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (!SliderKey(e)) base.OnKeyDown(e); }
        protected override bool ProcessDialogKey(Keys keyData)
        { return SliderKey(new KeyEventArgs(keyData)) || base.ProcessDialogKey(keyData); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (SliderEditing && !mouseGesture && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)) FinishSlider();
            base.OnKeyUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        { base.OnMouseCaptureChanged(e); if (!Capture && !finishing && mouseGesture) CancelSlider(); }
        protected override void OnLostFocus(EventArgs e) { CancelSlider(); base.OnLostFocus(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) CancelSlider(); base.OnVisibleChanged(e); }
        protected override void OnSizeChanged(EventArgs e) { CancelSlider(); base.OnSizeChanged(e); }
        protected override void OnScroll(ScrollEventArgs e) { CancelSlider(); base.OnScroll(e); }
        protected override void OnCurrentCellChanged(EventArgs e)
        { if (editingSlider != null && CurrentCell != editingSlider) CancelSlider(); base.OnCurrentCellChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) CancelSlider(); base.OnEnabledChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) CancelSlider(); base.Dispose(disposing); }
    }
}
