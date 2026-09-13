using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace Tk75.App
{
    // Hover help is a short reminder. Full instructions remain available to
    // assistive technology, without covering the editor during an adjustment.
    internal sealed class QuietToolTip : ToolTip
    {
        static readonly ConditionalWeakTable<DataGridView, QuietToolTip> GridTips = new ConditionalWeakTable<DataGridView, QuietToolTip>();
        internal const int MaximumTextLength = 180;
        public QuietToolTip()
        {
            InitialDelay = 900; ReshowDelay = 650; AutoPopDelay = 5000;
            UseAnimation = UseFading = false; ShowAlways = false; OwnerDraw = true;
            BackColor = ModernTheme.SurfaceAlt; ForeColor = ModernTheme.Foreground;
            Popup += Prepare; Draw += Paint;
        }
        public new void SetToolTip(Control control, string text)
        {
            if (control == null) throw new ArgumentNullException("control");
            if (!String.IsNullOrEmpty(text)) control.AccessibleDescription = text;
            string shortText = Summarize(text);
            if (base.GetToolTip(control) != shortText) base.SetToolTip(control, shortText);
        }
        internal static string Summarize(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            text = text.Replace("\r", "").Trim();
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text.Substring(0, newline).Trim();
            // Keep the first complete sentence; decimal separators inside a
            // number are not sentence boundaries.
            int sentence = text.IndexOf(". ", StringComparison.Ordinal);
            if (sentence >= 32) text = text.Substring(0, sentence + 1);
            if (text.Length <= MaximumTextLength) return text;
            int cut = text.LastIndexOf(' ', MaximumTextLength - 2);
            if (cut < MaximumTextLength / 2) cut = MaximumTextLength - 2;
            return text.Substring(0, cut).TrimEnd(' ', '.', ',', ';', ':') + "…";
        }
        void Prepare(object sender, PopupEventArgs e)
        {
            Control control = e.AssociatedControl;
            DataGridView grid = control as DataGridView;
            if (control == null || !control.Enabled || control.Capture || Control.MouseButtons != MouseButtons.None ||
                grid != null && (grid.IsCurrentCellInEditMode || grid is CurveSettingsGrid && ((CurveSettingsGrid)grid).SliderEditing))
            { e.Cancel = true; return; }
            string text = base.GetToolTip(control);
            if (text.Length == 0) { e.Cancel = true; return; }
            Size measured = TextRenderer.MeasureText(text, control.Font, new Size(320, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            e.ToolTipSize = new Size(Math.Min(320, measured.Width) + 18, measured.Height + 14);
        }
        void Paint(object sender, DrawToolTipEventArgs e)
        {
            using (var fill = new SolidBrush(ModernTheme.SurfaceAlt)) e.Graphics.FillRectangle(fill, e.Bounds);
            using (var border = new Pen(ModernTheme.Border)) e.Graphics.DrawRectangle(border, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - 1, e.Bounds.Height - 1);
            Font font = e.AssociatedControl == null ? SystemFonts.MessageBoxFont : e.AssociatedControl.Font;
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, Rectangle.Inflate(e.Bounds, -9, -7), ModernTheme.Foreground,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        internal static void AttachGrid(DataGridView grid)
        {
            grid.ShowCellToolTips = false;
            GridTips.GetValue(grid, delegate(DataGridView owner) {
                var tips = new QuietToolTip();
                owner.CellMouseEnter += delegate(object sender, DataGridViewCellEventArgs e) {
                    if (e.RowIndex < 0 || e.ColumnIndex < 0) { tips.SetToolTip(owner, null); return; }
                    DataGridViewCell cell = owner.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    var documented = cell as CurveSettingTextCell;
                    tips.SetToolTip(owner, documented == null ? cell.ToolTipText : documented.HelpDescription);
                };
                owner.CellMouseLeave += delegate { tips.Hide(owner); tips.SetToolTip(owner, null); };
                owner.MouseDown += delegate { tips.Hide(owner); };
                owner.Scroll += delegate { tips.Hide(owner); };
                owner.CellBeginEdit += delegate { tips.Hide(owner); };
                owner.Disposed += delegate { tips.Dispose(); };
                return tips;
            });
        }
    }

    internal class CurveSettingTextCell : DataGridViewTextBoxCell
    {
        public string HelpDescription;
        public override object Clone()
        { var cell = (CurveSettingTextCell)base.Clone(); cell.HelpDescription = HelpDescription; return cell; }
        protected override AccessibleObject CreateAccessibilityInstance() { return new HelpAccessible(this); }
        sealed class HelpAccessible : DataGridViewTextBoxCellAccessibleObject
        {
            readonly CurveSettingTextCell cell;
            public HelpAccessible(CurveSettingTextCell owner) : base(owner) { cell = owner; }
            public override string Description { get { return cell.HelpDescription ?? base.Description; } }
        }
    }
}
