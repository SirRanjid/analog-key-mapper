using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tk75.App
{
    public static class ModernTheme
    {
        public static readonly Color Background = Color.FromArgb(18, 19, 23);
        public static readonly Color Surface = Color.FromArgb(27, 29, 35);
        public static readonly Color SurfaceAlt = Color.FromArgb(35, 38, 46);
        public static readonly Color NavigationBackground = Color.FromArgb(22, 23, 29);
        public static readonly Color Foreground = Color.FromArgb(243, 244, 247);
        public static readonly Color Muted = Color.FromArgb(150, 157, 173);
        public static readonly Color Accent = Color.FromArgb(178, 164, 255);
        public static readonly Color AccentHover = Color.FromArgb(197, 186, 255);
        public static readonly Color AccentSoft = Color.FromArgb(54, 48, 78);
        public static readonly Color Border = Color.FromArgb(54, 58, 69);
        public static readonly Color DangerColor = Color.FromArgb(247, 153, 166);

        sealed class Fonts : IDisposable
        {
            public readonly Font Regular = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            public readonly Font Strong = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
            bool disposed;
            public void Dispose() { if (!disposed) { disposed = true; Regular.Dispose(); Strong.Dispose(); } }
        }
        sealed class ControlStyle
        {
            public Fonts Fonts;
            public bool HooksAttached, Navigation, InNavigation;
            public string ButtonRole;
        }
        static readonly ConditionalWeakTable<Control, Fonts> RootFonts = new ConditionalWeakTable<Control, Fonts>();
        static readonly ConditionalWeakTable<Control, ControlStyle> Styles = new ConditionalWeakTable<Control, ControlStyle>();
        static readonly ToolStripRenderer StripRenderer = new FlatStripRenderer();
        static ControlStyle NewStyle(Control ignored) { return new ControlStyle(); }
        static ControlStyle StyleFor(Control control) { return Styles.GetValue(control, NewStyle); }

        // DWM only styles this application's own window. Unsupported attributes
        // are optional; the native frame and its normal window controls remain.
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        [DllImport("dwmapi.dll", ExactSpelling = true)]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        static void StyleWindowFrame(Form form)
        {
            try
            {
                int dark = 1, background = ColorTranslator.ToWin32(Background), foreground = ColorTranslator.ToWin32(Foreground);
                DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
                DwmSetWindowAttribute(form.Handle, 35, ref background, 4);
                DwmSetWindowAttribute(form.Handle, 36, ref foreground, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public static void Apply(Control root)
        {
            if (root == null) throw new ArgumentNullException("root");
            Fonts fonts = RootFonts.GetValue(root, delegate(Control owner)
            {
                Fonts created = new Fonts();
                owner.Disposed += delegate { created.Dispose(); };
                return created;
            });
            root.Font = fonts.Regular;
            ApplyControl(root, fonts, false);
        }

        public static void Primary(Button button) { SetButtonRole(button, "primary"); }
        public static void Secondary(Button button) { SetButtonRole(button, "secondary"); }
        public static void Danger(Button button) { SetButtonRole(button, "danger"); }
        public static void Navigation(Control panel)
        {
            if (panel == null) throw new ArgumentNullException("panel");
            StyleFor(panel).Navigation = true;
            Apply(panel);
        }

        static void SetButtonRole(Button button, string role)
        {
            if (button == null) throw new ArgumentNullException("button");
            ControlStyle style = StyleFor(button);
            if (style.ButtonRole == role) return;
            style.ButtonRole = role;
            StyleButton(button, style);
        }

        static void ApplyControl(Control control, Fonts fonts, bool parentNavigation)
        {
            if (control.IsDisposed) return;
            ControlStyle style = StyleFor(control);
            style.Fonts = fonts;
            string tag = control.Tag as string;
            style.InNavigation = parentNavigation || style.Navigation || tag == "navigation" || tag == "sidebar";
            bool navigation = style.InNavigation;
            control.ForeColor = tag == "muted" ? Muted : navigation ? Color.White : Foreground;

            Form form = control as Form;
            TabPage page = control as TabPage;
            if (form != null) control.BackColor = Background;
            else if (page != null)
            {
                control.BackColor = Surface;
                if (page.Padding == Padding.Empty) page.Padding = new Padding(12);
            }
            else if (control is SleekCard) control.BackColor = Surface;
            else if (control is Panel || control is GroupBox)
                control.BackColor = navigation ? NavigationBackground : control.Parent == null ? Background : control.Parent.BackColor;
            Label label = control as Label;
            // Preserve deliberately larger headings created by the layout.
            if (label == null || label.Font.SizeInPoints <= 11) control.Font = fonts.Regular;
            var link = control as LinkLabel;
            if (link != null) { link.LinkColor = Muted; link.ActiveLinkColor = Accent; link.VisitedLinkColor = Accent; link.LinkBehavior = LinkBehavior.HoverUnderline; }

            Button button = control as Button;
            if (button != null) StyleButton(button, style);
            TextBoxBase text = control as TextBoxBase;
            if (text != null)
            {
                text.BorderStyle = BorderStyle.FixedSingle;
                text.BackColor = text.ReadOnly ? SurfaceAlt : Surface;
                text.ForeColor = Foreground;
            }
            ComboBox combo = control as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = SurfaceAlt; combo.ForeColor = Foreground; }
            NumericUpDown number = control as NumericUpDown;
            if (number != null) { number.BorderStyle = BorderStyle.FixedSingle; number.BackColor = Surface; number.ForeColor = Foreground; }
            ListBox list = control as ListBox;
            if (list != null) { list.BorderStyle = BorderStyle.FixedSingle; list.BackColor = Surface; list.ForeColor = Foreground; }
            CheckBox check = control as CheckBox;
            if (check != null) { check.FlatStyle = FlatStyle.Flat; check.FlatAppearance.BorderColor = Border; check.FlatAppearance.CheckedBackColor = AccentSoft; }
            RadioButton radio = control as RadioButton;
            if (radio != null) { radio.FlatStyle = FlatStyle.Flat; radio.FlatAppearance.BorderColor = Border; radio.FlatAppearance.CheckedBackColor = AccentSoft; }

            DataGridView grid = control as DataGridView;
            if (grid != null) StyleGrid(grid, fonts);
            TabControl tabs = control as TabControl;
            if (tabs != null)
            {
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabs.SizeMode = TabSizeMode.Fixed;
                tabs.ItemSize = new Size(164, 38);
                tabs.Padding = new Point(16, 6);
            }
            ToolStrip strip = control as ToolStrip;
            if (strip != null)
            {
                bool dark = strip is MenuStrip || navigation;
                strip.BackColor = dark ? NavigationBackground : Surface;
                strip.ForeColor = dark ? Color.White : Foreground;
                strip.Renderer = StripRenderer;
                strip.GripStyle = ToolStripGripStyle.Hidden;
                strip.Padding = new Padding(8, 4, 8, 4);
                StyleItems(strip.Items, fonts, dark);
            }

            if (!style.HooksAttached)
            {
                style.HooksAttached = true;
                if (form != null)
                {
                    form.HandleCreated += delegate { StyleWindowFrame(form); };
                    if (form.IsHandleCreated) StyleWindowFrame(form);
                }
                control.ControlAdded += delegate(object sender, ControlEventArgs e)
                {
                    ControlStyle current = StyleFor((Control)sender);
                    if (current.Fonts != null) ApplyControl(e.Control, current.Fonts, current.InNavigation);
                };
                if (tabs != null) tabs.DrawItem += DrawTab;
            }
            foreach (Control child in control.Controls) ApplyControl(child, fonts, navigation);
        }

        static void StyleButton(Button button, ControlStyle style)
        {
            string role = style.ButtonRole ?? button.Tag as string;
            var sleek = button as SleekButton;
            if (sleek != null) sleek.Appearance = role;
            if (role == null && button.Text.IndexOf("Controller AUS", StringComparison.OrdinalIgnoreCase) >= 0) role = "danger";
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = AccentSoft;
            button.FlatAppearance.MouseDownBackColor = SurfaceAlt;
            button.BackColor = style.InNavigation ? NavigationBackground : SurfaceAlt;
            button.ForeColor = style.InNavigation ? Color.White : Foreground;
            button.Padding = sleek != null && sleek.IconOnly ? Padding.Empty : new Padding(10, 4, 10, 4);
            button.Margin = new Padding(4, 4, 8, 4);
            button.MinimumSize = new Size(button.MinimumSize.Width, 32);
            if (button.Height < 32) button.Height = 32;
            if (style.Fonts != null) button.Font = style.Fonts.Strong;
            if (role == "primary")
            {
                button.BackColor = Accent; button.ForeColor = Background;
                button.FlatAppearance.BorderColor = Accent;
                button.FlatAppearance.MouseOverBackColor = AccentHover;
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(149, 135, 223);
            }
            else if (role == "danger")
            {
                button.BackColor = Color.FromArgb(45, 30, 37); button.ForeColor = DangerColor;
                button.FlatAppearance.BorderColor = Color.FromArgb(85, 48, 61);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(63, 35, 45);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 40, 51);
            }
            if (sleek != null) { button.FlatAppearance.BorderSize = 0; button.Invalidate(); }
        }

        static void StyleGrid(DataGridView grid, Fonts fonts)
        {
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.GridColor = Border;
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle.Font = fonts.Regular;
            grid.DefaultCellStyle.BackColor = Surface;
            grid.DefaultCellStyle.ForeColor = Foreground;
            grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Foreground;
            grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.Font = fonts.Strong;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Foreground;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 5, 8, 5);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 36;
            grid.RowTemplate.Height = 30;
            grid.RowTemplate.MinimumHeight = 30;
            foreach (DataGridViewRow row in grid.Rows) { row.MinimumHeight = 30; if (row.Height < 30) row.Height = 30; }
        }

        static void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = (TabControl)sender;
            if (e.Index < 0 || e.Index >= tabs.TabCount) return;
            ControlStyle style = StyleFor(tabs);
            bool selected = tabs.SelectedIndex == e.Index;
            Rectangle area = tabs.GetTabRect(e.Index);
            using (SolidBrush brush = new SolidBrush(selected ? Surface : Background)) e.Graphics.FillRectangle(brush, area);
            Rectangle textArea = Rectangle.Inflate(area, -10, -2);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, style.Fonts == null ? tabs.Font : style.Fonts.Strong, textArea,
                selected ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected)
                using (SolidBrush brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, area.Left + 10, area.Bottom - 3, Math.Max(1, area.Width - 20), 3);
            if ((e.State & DrawItemState.Focus) != 0 && tabs.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(area, -5, -5));
        }

        static void StyleItems(ToolStripItemCollection items, Fonts fonts, bool dark)
        {
            foreach (ToolStripItem item in items)
            {
                item.Font = fonts.Regular;
                item.ForeColor = dark ? Color.White : Foreground;
                item.Padding = new Padding(10, 5, 10, 5);
                ToolStripDropDownItem menu = item as ToolStripDropDownItem;
                if (menu != null) StyleItems(menu.DropDownItems, fonts, false);
            }
        }
        sealed class FlatStripRenderer : ToolStripProfessionalRenderer
        {
            public FlatStripRenderer() : base(new FlatStripColors()) { RoundedEdges = false; }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed) return;
                bool dropdown = e.Item.Owner is ToolStripDropDown;
                using (SolidBrush brush = new SolidBrush(dropdown ? AccentSoft : Accent)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            }
        }
        sealed class FlatStripColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Surface; } }
            public override Color ToolStripGradientBegin { get { return Surface; } }
            public override Color ToolStripGradientMiddle { get { return Surface; } }
            public override Color ToolStripGradientEnd { get { return Surface; } }
            public override Color MenuStripGradientBegin { get { return NavigationBackground; } }
            public override Color MenuStripGradientEnd { get { return NavigationBackground; } }
        }
    }

    // Compatibility for callers that prefer the shorter helper name.
    public static class Theme
    {
        public static void Apply(Control root) { ModernTheme.Apply(root); }
        public static void Primary(Button button) { ModernTheme.Primary(button); }
        public static void Secondary(Button button) { ModernTheme.Secondary(button); }
        public static void Danger(Button button) { ModernTheme.Danger(button); }
        public static void Navigation(Control panel) { ModernTheme.Navigation(panel); }
    }
}
