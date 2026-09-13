using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        static extern IntPtr ThemeSend(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        sealed class ThemeCombo : SleekComboBox
        { public void RecreateNativeHandle() { RecreateHandle(); } }
        sealed class ThemeDcHost : Form
        { protected override bool ShowWithoutActivation { get { return true; } } }
        [StructLayout(LayoutKind.Sequential)]
        struct ThemeNativeScrollInfo
        {
            public int Size;
            public uint Mask;
            public int Minimum, Maximum;
            public uint Page;
            public int Position, TrackPosition;
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int SetScrollInfo(IntPtr window, int bar, ref ThemeNativeScrollInfo info, bool redraw);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool GetScrollInfo(IntPtr window, int bar, ref ThemeNativeScrollInfo info);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int SetScrollPos(IntPtr window, int bar, int position, bool redraw);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool SetScrollRange(IntPtr window, int bar, int minimum, int maximum, bool redraw);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern bool GdiFlush();
        static List<string> themeFailures;
        static void ThemeCheck(bool value, string description)
        {
            assertions++;
            if (value) return;
            themeFailures.Add(description);
            Console.WriteLine("NATIVE THEME FAILURE: " + description);
        }

        static Bitmap ThemeCapture(Control control)
        {
            Bitmap bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            return bitmap;
        }
        static Bitmap ThemeCaptureExistingDc(Control control)
        {
            // No WM_PAINT/WM_PRINT/Update/DoEvents here: read only the pixels
            // already drawn to our own test window at this exact message edge.
            Bitmap bitmap = new Bitmap(control.Width, control.Height);
            IntPtr source = NativeControlPaint.GetWindowDC(control.Handle);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr destination = graphics.GetHdc();
                try { GdiFlush(); Check(source != IntPtr.Zero && BitBlt(destination, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, 0x00CC0020), "The direct native DC probe copies existing pixels without causing a paint."); }
                finally { graphics.ReleaseHdc(destination); if (source != IntPtr.Zero) NativeControlPaint.ReleaseDC(control.Handle, source); }
            }
            return bitmap;
        }
        static int ThemeBrightPixels(Bitmap bitmap)
        {
            int bright = 0;
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            { Color pixel = bitmap.GetPixel(x, y); if (pixel.R > 205 && pixel.G > 205 && pixel.B > 205) bright++; }
            return bright;
        }
        static void CheckThemeExistingFrame(Control control, bool vertical, string description)
        {
            ScrollbarDrawing.ScrollInfo info;
            ThemeCheck(ScrollbarDrawing.Read(control.Handle, vertical ? 0xFFFFFFFB : 0xFFFFFFFA, out info), description + " has a real native scrollbar.");
            NativeControlPaint.Rect window;
            Check(NativeControlPaint.GetWindowRect(control.Handle, out window), description + " has native window bounds.");
            Rectangle area = info.Area.Bounds; area.Offset(-window.Left, -window.Top);
            using (Bitmap image = ThemeCaptureExistingDc(control)) CheckThemeSurface(image, area, description);
        }
        static void RunImmediateScrollDrawing(string artifacts)
        {
            // CI-only, app-owned, nonactivating window. Offscreen windows have
            // clipped DCs, so this small fixture must have drawable pixels.
            // Never a screen capture or input injection into another app.
            using (Form host = new ThemeDcHost { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(24, 24), ClientSize = new Size(260, 260), BackColor = ModernTheme.Surface,
                TopMost = true, FormBorderStyle = FormBorderStyle.None })
            using (VScrollBar themed = new VScrollBar { Left = 15, Top = 12, Height = 232, Width = 22, Minimum = 0, Maximum = 1000, LargeChange = 40 })
            using (VScrollBar baseline = new VScrollBar { Left = 60, Top = 12, Height = 232, Width = 22, Minimum = 0, Maximum = 1000, LargeChange = 40 })
            using (Panel panel = new Panel { Left = 110, Top = 12, Width = 130, Height = 232, AutoScroll = true, AutoScrollMinSize = new Size(400, 1000) })
            {
                host.Controls.AddRange(new Control[] { themed, baseline, panel });
                ModernTheme.Apply(themed); ModernTheme.Apply(panel); host.Show(); host.Update(); themed.Refresh(); baseline.Refresh(); panel.Refresh();
                IntPtr dc = NativeControlPaint.GetWindowDC(baseline.Handle);
                try { using (Graphics graphics = Graphics.FromHdc(dc)) graphics.Clear(ModernTheme.SurfaceAlt); GdiFlush(); }
                finally { NativeControlPaint.ReleaseDC(baseline.Handle, dc); }
                using (Bitmap image = ThemeCaptureExistingDc(baseline))
                    CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Seeded baseline direct DC");
                var change = new ThemeNativeScrollInfo { Size = Marshal.SizeOf(typeof(ThemeNativeScrollInfo)), Mask = 7, Minimum = 0, Maximum = 1000, Page = 40, Position = 400 };
                SetScrollInfo(baseline.Handle, 2, ref change, true);
                using (Bitmap image = ThemeCaptureExistingDc(baseline))
                    ThemeCheck(ThemeBrightPixels(image) > image.Width * image.Height / 8,
                        "The unthemed baseline demonstrates a synchronous light redraw, so this probe can detect the reported flash.");
                for (int i = 0; i < 8; i++)
                {
                    change.Maximum = 1000 + i * 50; change.Page = (uint)(40 + i); change.Position = i * 73 + 10;
                    int actual = SetScrollInfo(themed.Handle, 2, ref change, true);
                    var read = new ThemeNativeScrollInfo { Size = Marshal.SizeOf(typeof(ThemeNativeScrollInfo)), Mask = 7 };
                    ThemeCheck(GetScrollInfo(themed.Handle, 2, ref read) && actual == change.Position && read.Position == change.Position && read.Maximum == change.Maximum && read.Page == change.Page,
                        "The immediate-redraw guard preserves the actual native position/range/page and return value at step " + i + ".");
                    using (Bitmap image = ThemeCaptureExistingDc(themed))
                        CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Existing scrollbar DC immediately after SetScrollInfo " + i);
                    SetScrollPos(themed.Handle, 2, change.Position + 4, true);
                    using (Bitmap image = ThemeCaptureExistingDc(themed))
                        CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Existing scrollbar DC immediately after SetScrollPos " + i);
                    SetScrollRange(themed.Handle, 2, 0, change.Maximum + 20, true);
                    using (Bitmap image = ThemeCaptureExistingDc(themed))
                        CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Existing scrollbar DC immediately after SetScrollRange " + i);
                    themed.Update();
                    using (Bitmap image = ThemeCaptureExistingDc(themed))
                    {
                        CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Updated scrollbar DC " + i);
                        if (i == 7) image.Save(Path.Combine(artifacts, "preview-theme-scrollbar-live-dc.png"));
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    int beforeY = panel.AutoScrollPosition.Y;
                    ThemeSend(panel.Handle, 0x0115, (IntPtr)3, IntPtr.Zero);
                    ThemeCheck(panel.AutoScrollPosition.Y < beforeY, "The themed AutoScroll panel still handles native page-down input at step " + i + ".");
                    CheckThemeExistingFrame(panel, true, "AutoScroll frame immediately after native page-down " + i);
                    ThemeSend(panel.Handle, 0x020A, new IntPtr(unchecked((int)0xFF880000)), IntPtr.Zero);
                    CheckThemeExistingFrame(panel, true, "AutoScroll frame immediately after native wheel " + i);
                    ThemeSend(panel.Handle, 0x0114, (IntPtr)1, IntPtr.Zero);
                    CheckThemeExistingFrame(panel, false, "AutoScroll frame immediately after horizontal line scroll " + i);
                    panel.Height = 226 - i * 6;
                    CheckThemeExistingFrame(panel, true, "AutoScroll frame immediately after size change " + i);
                    // A managed layout is a separate redraw edge; do not pump
                    // messages or force paint between it and the direct probe.
                    Point position = panel.AutoScrollPosition;
                    panel.PerformLayout();
                    CheckThemeExistingFrame(panel, true, "AutoScroll frame immediately after layout " + i);
                    CheckThemeExistingFrame(panel, false, "Horizontal AutoScroll frame immediately after layout " + i);
                    ThemeCheck(panel.AutoScrollPosition == position, "The synchronous frame pass preserves the layout's native scroll position at step " + i + ".");
                    panel.AutoScrollMinSize = new Size(400 + i * 20, 1100 + i * 150);
                    CheckThemeExistingFrame(panel, true, "AutoScroll frame immediately after content range change " + i);
                    CheckThemeExistingFrame(panel, false, "Horizontal AutoScroll frame immediately after content range change " + i);
                }
                host.Close();
            }
        }
        static Bitmap ThemeCapturePopup(IntPtr window)
        {
            NativeControlPaint.Rect bounds;
            Check(NativeControlPaint.GetWindowRect(window, out bounds), "The owned popup has native bounds for its paint capture.");
            Bitmap bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { ThemeSend(window, 0x0317, dc, (IntPtr)30); }
                finally { graphics.ReleaseHdc(dc); }
            }
            return bitmap;
        }
        static void CheckThemeSurface(Bitmap bitmap, Rectangle area, string description)
        {
            area.Intersect(new Rectangle(Point.Empty, bitmap.Size));
            ThemeCheck(area.Width > 2 && area.Height > 2, description + " has a visible native surface (capture area " + area + ").");
            int bright = 0, painted = 0, count = 0;
            int[] surfaces = { ModernTheme.Background.ToArgb() & 0xFFFFFF, ModernTheme.Surface.ToArgb() & 0xFFFFFF,
                ModernTheme.SurfaceAlt.ToArgb() & 0xFFFFFF, ModernTheme.Border.ToArgb() & 0xFFFFFF };
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y); count++;
                    if (pixel.R > 205 && pixel.G > 205 && pixel.B > 205) bright++;
                    // Native GDI printing need not preserve alpha. Require real
                    // palette coverage so an unpainted black bitmap cannot pass.
                    if (Array.IndexOf(surfaces, pixel.ToArgb() & 0xFFFFFF) >= 0) painted++;
                }
            ThemeCheck(painted >= count / 4, description + " contains painted theme pixels (palette " + painted + "/" + count + ").");
            ThemeCheck(bright < count / 8, description + " uses the dark palette instead of a native white surface (bright " + bright + "/" + count + ").");
        }
        static void CheckThemeScrollSurface(Control control, bool standalone, bool vertical, string description)
        {
            // An offscreen window need not receive an onscreen paint first.
            // The real WM_PRINT path initializes the native scrollbar geometry
            // just as the first visible paint does; inspect it after that paint.
            using (Bitmap bitmap = ThemeCapture(control))
            {
                ScrollbarDrawing.ScrollInfo info;
                bool visible = ScrollbarDrawing.Read(control.Handle, standalone ? 0xFFFFFFFC : vertical ? 0xFFFFFFFB : 0xFFFFFFFA, out info);
                ThemeCheck(visible, description + " is a real visible native scrollbar (state 0x" + info.State.ToString("X") + "; bounds " + info.Area.Bounds + ").");
                if (!visible) return;
                NativeControlPaint.Rect window;
                Check(NativeControlPaint.GetWindowRect(control.Handle, out window), description + " exposes its real window bounds.");
                Rectangle area = info.Area.Bounds; area.Offset(-window.Left, -window.Top);
                if (standalone)
                {
                    ThemeCheck(area.Size == control.ClientSize, description + " uses its real native client dimensions (native " + area.Size + "; client " + control.ClientSize + ").");
                    ThemeCheck(info.ThumbEnd > info.ThumbStart, description + " exposes an actual native thumb (start " + info.ThumbStart + "; end " + info.ThumbEnd + ").");
                }
                CheckThemeSurface(bitmap, area, description);
            }
        }

        // CI-only synthetic windows: no physical input, hardware or global input
        // injection. Messages target only handles created in this fixture.
        static void RunNativeThemeControls(string artifacts)
        {
            int started = assertions;
            themeFailures = new List<string>();
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), ClientSize = new Size(760, 570) })
            using (ThemeCombo combo = new ThemeCombo { Left = 18, Top = 18, Width = 320 })
            using (SleekNumericUpDown number = new SleekNumericUpDown { Left = 360, Top = 18, Width = 160,
                Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = .5M, Value = 25 })
            using (NumericUpDown baselineNumber = new NumericUpDown { Minimum = 0, Maximum = 100,
                DecimalPlaces = 1, Increment = .5M, Value = 25 })
            using (Panel scroll = new Panel { Left = 18, Top = 75, Width = 220, Height = 210, AutoScroll = true, AutoScrollMinSize = new Size(600, 850) })
            using (ListBox list = new ListBox { Left = 258, Top = 75, Width = 220, Height = 210 })
            using (TextBox text = new TextBox { Left = 498, Top = 75, Width = 240, Height = 210, Multiline = true, ScrollBars = ScrollBars.Vertical })
            using (DataGridView grid = new DataGridView { Left = 18, Top = 310, Width = 720, Height = 230,
                AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill })
            {
                for (int i = 0; i < 80; i++) { combo.Items.Add("Item " + i); list.Items.Add("Item " + i); text.AppendText("Line " + i + "\r\n"); }
                combo.SelectedIndex = 0;
                grid.Columns.Add("Name", "Name"); grid.Columns.Add("Value", "Value");
                var pickerColumn = new DataGridViewComboBoxColumn { Name = "Choice", HeaderText = "Choice", FlatStyle = FlatStyle.Flat };
                for (int i = 0; i < 80; i++) pickerColumn.Items.Add("Choice " + i);
                grid.Columns.Add(pickerColumn);
                for (int i = 0; i < 80; i++) grid.Rows.Add("Item " + i, i, "Choice " + i);
                host.Controls.AddRange(new Control[] { combo, number, scroll, list, text, grid });
                ModernTheme.Apply(host); host.Show(); host.PerformLayout(); Application.DoEvents();
                ThemeCheck(combo.AccessibilityObject.Role == AccessibleRole.ComboBox, "The themed picker retains its native accessible combo role (actual " + combo.AccessibilityObject.Role + ").");
                // Framework versions and compatibility switches can report
                // different parent roles. Compare with the genuine unthemed
                // framework control, then verify its native edit/arrow tree.
                baselineNumber.CreateControl();
                AccessibleObject baselineAccessible = baselineNumber.AccessibilityObject;
                AccessibleObject numberAccessible = number.AccessibilityObject;
                ThemeCheck(numberAccessible.GetType() == baselineAccessible.GetType() && numberAccessible.Role == baselineAccessible.Role,
                    "The themed number retains the original framework accessible provider and role (expected " + baselineAccessible.GetType().FullName +
                    "/" + baselineAccessible.Role + "; actual " + numberAccessible.GetType().FullName + "/" + numberAccessible.Role + ").");
                ThemeCheck(numberAccessible.GetChildCount() == 2 && numberAccessible.GetChildCount() == baselineAccessible.GetChildCount(),
                    "The original numeric accessible edit/button tree remains intact (expected " + baselineAccessible.GetChildCount() +
                    "; actual " + numberAccessible.GetChildCount() + ").");
                Check(number.Controls.Count == 2 && number.Controls.OfType<TextBoxBase>().Count() == 1,
                    "The number keeps its original edit and button children, with no input overlay.");
                Control spinner = number.Controls.Cast<Control>().Single(child => !(child is TextBoxBase));
                TextBoxBase edit = number.Controls.OfType<TextBoxBase>().Single();
                TextBoxBase baselineEdit = baselineNumber.Controls.OfType<TextBoxBase>().Single();
                Control baselineSpinner = baselineNumber.Controls.Cast<Control>().Single(child => !(child is TextBoxBase));
                ThemeCheck(edit.AccessibilityObject.GetType() == baselineEdit.AccessibilityObject.GetType() && edit.AccessibilityObject.Role == baselineEdit.AccessibilityObject.Role,
                    "The original numeric edit provider and role remain intact (expected " + baselineEdit.AccessibilityObject.Role +
                    "; actual " + edit.AccessibilityObject.Role + ").");
                AccessibleObject spinAccessible = spinner.AccessibilityObject;
                ThemeCheck(spinAccessible.GetType() == baselineSpinner.AccessibilityObject.GetType() && spinAccessible.Role == AccessibleRole.SpinButton,
                    "The actual numeric arrow child retains its original SpinButton provider (actual " + spinAccessible.GetType().FullName + "/" + spinAccessible.Role + ").");
                ThemeCheck(spinAccessible.GetChildCount() == 2, "The numeric arrow child exposes two native accessible buttons (actual " + spinAccessible.GetChildCount() + ").");
                for (int direction = 0; direction < 2; direction++)
                {
                    AccessibleObject arrow = spinAccessible.GetChild(direction);
                    AccessibleObject baselineArrow = baselineSpinner.AccessibilityObject.GetChild(direction);
                    ThemeCheck(arrow != null && baselineArrow != null && arrow.GetType() == baselineArrow.GetType() && arrow.Role == AccessibleRole.PushButton,
                        "Numeric arrow " + direction + " retains its native PushButton provider (actual " + (arrow == null ? "missing" : arrow.GetType().FullName + "/" + arrow.Role) + ").");
                }
                ThemeCheck(edit.BorderStyle == BorderStyle.None, "The native numeric edit does not gain a second frame from recursive theming (actual " + edit.BorderStyle + ").");
                int edits = 0; number.ValueChanged += delegate { edits++; };
                ModernTheme.Apply(host); ModernTheme.Apply(host);
                number.Focus(); ThemeSend(edit.Handle, 0x0100, (IntPtr)Keys.Up, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(number.Value == 25.5M && edits == 1, "A native numeric Up key applies the decimal increment once after repeated theme application (value " + number.Value + "; edits " + edits + ").");
                ThemeSend(spinner.Handle, 0x0201, (IntPtr)1, (IntPtr)(2 | (2 << 16)));
                ThemeSend(spinner.Handle, 0x0202, IntPtr.Zero, (IntPtr)(2 | (2 << 16))); Application.DoEvents();
                ThemeCheck(number.Value == 26M && edits == 2, "The real themed spinner up button still changes the value exactly once (value " + number.Value + "; edits " + edits + ").");
                number.Value = number.Maximum; number.UpButton();
                ThemeCheck(number.Value == number.Maximum, "The native numeric maximum remains enforced (value " + number.Value + "; maximum " + number.Maximum + ").");
                number.Value = number.Minimum; number.DownButton();
                ThemeCheck(number.Value == number.Minimum, "The native numeric minimum remains enforced (value " + number.Value + "; minimum " + number.Minimum + ").");
                using (Bitmap image = ThemeCapture(number))
                { CheckThemeSurface(image, spinner.Bounds, "Numeric spinner arrows"); image.Save(Path.Combine(artifacts, "preview-theme-number.png")); }
                number.Enabled = false;
                using (Bitmap image = ThemeCapture(number)) CheckThemeSurface(image, spinner.Bounds, "Disabled numeric spinner arrows");
                number.Enabled = true;

                combo.Focus(); ThemeSend(combo.Handle, 0x0100, (IntPtr)Keys.Down, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(combo.SelectedIndex == 1, "The themed picker preserves native closed arrow navigation (actual index " + combo.SelectedIndex + ").");
                using (Bitmap image = ThemeCapture(combo))
                {
                    NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo(); info.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                    Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref info), "The combo retains native edit, arrow and popup handles.");
                    CheckThemeSurface(image, info.Button.Bounds, "Combo arrow"); image.Save(Path.Combine(artifacts, "preview-theme-combo.png"));
                }
                ThemeSend(combo.Handle, 0x0100, (IntPtr)Keys.F4, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(combo.DroppedDown, "Native F4 still opens the original drop-down popup (actual " + combo.DroppedDown + ").");
                NativeControlPaint.ComboInfo popup = new NativeControlPaint.ComboInfo(); popup.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref popup) && popup.List != IntPtr.Zero, "The popup remains a real native list window.");
                ScrollbarDrawing.ScrollInfo popupScroll;
                bool popupScrollbarVisible = ScrollbarDrawing.Read(popup.List, 0xFFFFFFFB, out popupScroll);
                ThemeCheck(popupScrollbarVisible, "Long native drop-downs expose their original vertical scrollbar (state 0x" + popupScroll.State.ToString("X") + "; bounds " + popupScroll.Area.Bounds + ").");
                using (Bitmap image = ThemeCapturePopup(popup.List))
                {
                    NativeControlPaint.Rect bounds; NativeControlPaint.GetWindowRect(popup.List, out bounds);
                    Rectangle area = popupScroll.Area.Bounds; area.Offset(-bounds.Left, -bounds.Top);
                    CheckThemeSurface(image, area, "Open combo popup scrollbar");
                    image.Save(Path.Combine(artifacts, "preview-theme-popup.png"));
                }
                combo.DroppedDown = false;
                combo.RecreateNativeHandle(); Application.DoEvents();
                ThemeCheck(combo.Items.Count == 80 && combo.SelectedIndex == 1, "Theme attachment survives native combo handle recreation without changing its items or value (items " + combo.Items.Count + "; index " + combo.SelectedIndex + ").");

                using (Bitmap image = ThemeCapture(grid))
                {
                    Rectangle cell = grid.GetCellDisplayRectangle(2, 0, false);
                    Rectangle arrow = new Rectangle(cell.Right - SystemInformation.VerticalScrollBarWidth - 7, cell.Top + 5, SystemInformation.VerticalScrollBarWidth, cell.Height - 10);
                    CheckThemeSurface(image, arrow, "Unedited grid picker arrow");
                    image.Save(Path.Combine(artifacts, "preview-theme-grid-combo-cell.png"));
                }
                ComboBox firstEditor = null;
                for (int row = 0; row < 2; row++)
                {
                    grid.CurrentCell = grid.Rows[row].Cells[2];
                    Check(grid.BeginEdit(true), "The native grid combo enters editing for row " + row + ".");
                    ComboBox editor = grid.EditingControl as ComboBox;
                    Check(editor != null && editor is IDataGridViewEditingControl, "The picker retains the grid's real editing control contract.");
                    if (row == 0) firstEditor = editor;
                    else ThemeCheck(Object.ReferenceEquals(firstEditor, editor), "The fixture exercises a recycled grid editing combo.");
                    ThemeCheck(editor.DrawMode == DrawMode.OwnerDrawFixed && editor.AccessibilityObject.Role == AccessibleRole.ComboBox,
                        "The recycled grid editor uses shared picker drawing with its native accessible role.");
                    using (Bitmap image = ThemeCapture(editor))
                    {
                        NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo(); info.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                        Check(NativeControlPaint.GetComboBoxInfo(editor.Handle, ref info), "The grid editor still exposes its own native arrow and list.");
                        CheckThemeSurface(image, info.Button.Bounds, "Grid editing combo arrow " + row);
                    }
                    ThemeSend(editor.Handle, 0x0100, (IntPtr)Keys.F4, IntPtr.Zero); Application.DoEvents();
                    ThemeCheck(editor.DroppedDown, "Native F4 opens the grid editor's themed popup.");
                    NativeControlPaint.ComboInfo infoPopup = new NativeControlPaint.ComboInfo(); infoPopup.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                    Check(NativeControlPaint.GetComboBoxInfo(editor.Handle, ref infoPopup), "The editing popup has a real owned native handle.");
                    using (Bitmap image = ThemeCapturePopup(infoPopup.List))
                    {
                        CheckThemeSurface(image, new Rectangle(3, 3, image.Width - 6, image.Height - 6), "Grid editing combo popup " + row);
                        if (row == 0) image.Save(Path.Combine(artifacts, "preview-theme-grid-combo-popup.png"));
                    }
                    editor.DroppedDown = false; editor.SelectedIndex = 4 + row;
                    grid.NotifyCurrentCellDirty(true); grid.EndEdit();
                    ThemeCheck(Convert.ToString(grid.Rows[row].Cells[2].Value) == "Choice " + (4 + row),
                        "The native grid edit commits exactly the chosen value after theme reuse.");
                }

                ThemeSend(scroll.Handle, 0x0115, (IntPtr)3, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(scroll.AutoScrollPosition.Y < 0, "A native page-down scroll still moves the panel content (actual " + scroll.AutoScrollPosition + ").");
                ThemeSend(scroll.Handle, 0x0114, (IntPtr)1, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(scroll.AutoScrollPosition.X < 0, "A native horizontal line scroll still moves the panel content (actual " + scroll.AutoScrollPosition + ").");
                int first = list.TopIndex;
                ThemeSend(list.Handle, 0x0115, (IntPtr)3, IntPtr.Zero); Application.DoEvents();
                ThemeCheck(list.TopIndex > first, "The native list page-down message preserves scrolling (before " + first + "; after " + list.TopIndex + ").");
                CheckThemeScrollSurface(scroll, false, true, "Panel vertical scrollbar");
                CheckThemeScrollSurface(scroll, false, false, "Panel horizontal scrollbar");
                CheckThemeScrollSurface(list, false, true, "List scrollbar");
                CheckThemeScrollSurface(text, false, true, "Multiline text scrollbar");
                VScrollBar gridScroll = grid.Controls.OfType<VScrollBar>().Single(control => control.Visible);
                ThemeCheck(gridScroll.AccessibilityObject.Role == AccessibleRole.ScrollBar, "The grid scrollbar retains its native accessible scrollbar role (actual " + gridScroll.AccessibilityObject.Role + ").");
                CheckThemeScrollSurface(gridScroll, true, true, "Grid scrollbar");
                ScrollbarDrawing.ScrollInfo gridBefore;
                bool gridBeforeReady = ScrollbarDrawing.Read(gridScroll.Handle, 0xFFFFFFFC, out gridBefore);
                grid.FirstDisplayedScrollingRowIndex = 25; Application.DoEvents();
                ThemeCheck(grid.FirstDisplayedScrollingRowIndex == 25, "The styled grid retains its scroll-position contract (actual " + grid.FirstDisplayedScrollingRowIndex + ").");
                using (Bitmap image = ThemeCapture(gridScroll))
                {
                    ScrollbarDrawing.ScrollInfo gridAfter;
                    bool gridAfterReady = ScrollbarDrawing.Read(gridScroll.Handle, 0xFFFFFFFC, out gridAfter);
                    ThemeCheck(gridBeforeReady && gridAfterReady && gridAfter.ThumbStart > gridBefore.ThumbStart,
                        "The painted grid thumb follows its real native scroll position (before " + gridBefore.ThumbStart + "; after " + gridAfter.ThumbStart + ").");
                    CheckThemeSurface(image, new Rectangle(Point.Empty, image.Size), "Scrolled grid scrollbar");
                    image.Save(Path.Combine(artifacts, "preview-theme-grid-scrollbar.png"));
                }
                using (Bitmap image = ThemeCapture(host)) image.Save(Path.Combine(artifacts, "preview-theme-controls.png"));
                host.Close();
            }
            RunImmediateScrollDrawing(artifacts);
            Check(themeFailures.Count == 0, "Native theme failures: " + String.Join(" | ", themeFailures.ToArray()));
            Console.WriteLine("NATIVE THEME PASS: " + (assertions - started) + " assertions; real native arrows, numeric editing, popup lifecycle, accessibility and scrollbar surfaces.");
        }
    }
}
