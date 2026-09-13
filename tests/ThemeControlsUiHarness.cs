using System;
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

        static Bitmap ThemeCapture(Control control)
        {
            Bitmap bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            return bitmap;
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
            Check(area.Width > 2 && area.Height > 2, description + " has a visible native surface.");
            int bright = 0, count = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                { Color pixel = bitmap.GetPixel(x, y); count++; if (pixel.R > 205 && pixel.G > 205 && pixel.B > 205) bright++; }
            Check(bright < count / 8, description + " uses the dark palette instead of a native white surface (bright " + bright + "/" + count + ").");
        }
        static void CheckThemeScrollSurface(Control control, bool standalone, bool vertical, string description)
        {
            ScrollbarDrawing.ScrollInfo info;
            Check(ScrollbarDrawing.Read(control.Handle, standalone ? 0xFFFFFFFC : vertical ? 0xFFFFFFFB : 0xFFFFFFFA, out info), description + " is a real visible native scrollbar.");
            NativeControlPaint.Rect window;
            Check(NativeControlPaint.GetWindowRect(control.Handle, out window), description + " exposes its real window bounds.");
            Rectangle area = info.Area.Bounds; area.Offset(-window.Left, -window.Top);
            using (Bitmap bitmap = ThemeCapture(control)) CheckThemeSurface(bitmap, area, description);
        }

        // CI-only synthetic windows: no physical input, hardware or global input
        // injection. Messages target only handles created in this fixture.
        static void RunNativeThemeControls(string artifacts)
        {
            int started = assertions;
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), ClientSize = new Size(760, 570) })
            using (ThemeCombo combo = new ThemeCombo { Left = 18, Top = 18, Width = 320 })
            using (SleekNumericUpDown number = new SleekNumericUpDown { Left = 360, Top = 18, Width = 160,
                Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = .5M, Value = 25 })
            using (Panel scroll = new Panel { Left = 18, Top = 75, Width = 220, Height = 210, AutoScroll = true, AutoScrollMinSize = new Size(600, 850) })
            using (ListBox list = new ListBox { Left = 258, Top = 75, Width = 220, Height = 210 })
            using (TextBox text = new TextBox { Left = 498, Top = 75, Width = 240, Height = 210, Multiline = true, ScrollBars = ScrollBars.Vertical })
            using (DataGridView grid = new DataGridView { Left = 18, Top = 310, Width = 720, Height = 230,
                AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill })
            {
                for (int i = 0; i < 80; i++) { combo.Items.Add("Item " + i); list.Items.Add("Item " + i); text.AppendText("Line " + i + "\r\n"); }
                combo.SelectedIndex = 0;
                grid.Columns.Add("Name", "Name"); grid.Columns.Add("Value", "Value");
                for (int i = 0; i < 80; i++) grid.Rows.Add("Item " + i, i);
                host.Controls.AddRange(new Control[] { combo, number, scroll, list, text, grid });
                ModernTheme.Apply(host); host.Show(); host.PerformLayout(); Application.DoEvents();
                Check(combo.AccessibilityObject.Role == AccessibleRole.ComboBox, "The themed picker retains its native accessible combo role.");
                Check(number.AccessibilityObject.Role == AccessibleRole.SpinButton, "The themed number retains its native accessible spin-button role.");
                Check(number.Controls.Count == 2 && number.Controls.OfType<TextBoxBase>().Count() == 1,
                    "The number keeps its original edit and button children, with no input overlay.");
                Control spinner = number.Controls.Cast<Control>().Single(child => !(child is TextBoxBase));
                TextBoxBase edit = number.Controls.OfType<TextBoxBase>().Single();
                Check(edit.BorderStyle == BorderStyle.None, "The native numeric edit does not gain a second frame from recursive theming.");
                int edits = 0; number.ValueChanged += delegate { edits++; };
                ModernTheme.Apply(host); ModernTheme.Apply(host);
                number.Focus(); ThemeSend(edit.Handle, 0x0100, (IntPtr)Keys.Up, IntPtr.Zero); Application.DoEvents();
                Check(number.Value == 25.5M && edits == 1, "A native numeric Up key applies the decimal increment once after repeated theme application.");
                ThemeSend(spinner.Handle, 0x0201, (IntPtr)1, (IntPtr)(2 | (2 << 16)));
                ThemeSend(spinner.Handle, 0x0202, IntPtr.Zero, (IntPtr)(2 | (2 << 16))); Application.DoEvents();
                Check(number.Value == 26M && edits == 2, "The real themed spinner up button still changes the value exactly once.");
                number.Value = number.Maximum; number.UpButton();
                Check(number.Value == number.Maximum, "The native numeric maximum remains enforced.");
                number.Value = number.Minimum; number.DownButton();
                Check(number.Value == number.Minimum, "The native numeric minimum remains enforced.");
                using (Bitmap image = ThemeCapture(number))
                { CheckThemeSurface(image, spinner.Bounds, "Numeric spinner arrows"); image.Save(Path.Combine(artifacts, "preview-theme-number.png")); }
                number.Enabled = false;
                using (Bitmap image = ThemeCapture(number)) CheckThemeSurface(image, spinner.Bounds, "Disabled numeric spinner arrows");
                number.Enabled = true;

                combo.Focus(); ThemeSend(combo.Handle, 0x0100, (IntPtr)Keys.Down, IntPtr.Zero); Application.DoEvents();
                Check(combo.SelectedIndex == 1, "The themed picker preserves native closed arrow navigation.");
                using (Bitmap image = ThemeCapture(combo))
                {
                    NativeControlPaint.ComboInfo info = new NativeControlPaint.ComboInfo(); info.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                    Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref info), "The combo retains native edit, arrow and popup handles.");
                    CheckThemeSurface(image, info.Button.Bounds, "Combo arrow"); image.Save(Path.Combine(artifacts, "preview-theme-combo.png"));
                }
                ThemeSend(combo.Handle, 0x0100, (IntPtr)Keys.F4, IntPtr.Zero); Application.DoEvents();
                Check(combo.DroppedDown, "Native F4 still opens the original drop-down popup.");
                NativeControlPaint.ComboInfo popup = new NativeControlPaint.ComboInfo(); popup.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref popup) && popup.List != IntPtr.Zero, "The popup remains a real native list window.");
                ScrollbarDrawing.ScrollInfo popupScroll;
                Check(ScrollbarDrawing.Read(popup.List, 0xFFFFFFFB, out popupScroll), "Long native drop-downs expose their original vertical scrollbar.");
                using (Bitmap image = ThemeCapturePopup(popup.List))
                {
                    NativeControlPaint.Rect bounds; NativeControlPaint.GetWindowRect(popup.List, out bounds);
                    Rectangle area = popupScroll.Area.Bounds; area.Offset(-bounds.Left, -bounds.Top);
                    CheckThemeSurface(image, area, "Open combo popup scrollbar");
                    image.Save(Path.Combine(artifacts, "preview-theme-popup.png"));
                }
                combo.DroppedDown = false;
                combo.RecreateNativeHandle(); Application.DoEvents();
                Check(combo.Items.Count == 80 && combo.SelectedIndex == 1, "Theme attachment survives native combo handle recreation without changing its items or value.");

                ThemeSend(scroll.Handle, 0x0115, (IntPtr)3, IntPtr.Zero); Application.DoEvents();
                Check(scroll.AutoScrollPosition.Y < 0, "A native page-down scroll still moves the panel content.");
                ThemeSend(scroll.Handle, 0x0114, (IntPtr)1, IntPtr.Zero); Application.DoEvents();
                Check(scroll.AutoScrollPosition.X < 0, "A native horizontal line scroll still moves the panel content.");
                int first = list.TopIndex;
                ThemeSend(list.Handle, 0x0115, (IntPtr)3, IntPtr.Zero); Application.DoEvents();
                Check(list.TopIndex > first, "The native list page-down message preserves scrolling.");
                CheckThemeScrollSurface(scroll, false, true, "Panel vertical scrollbar");
                CheckThemeScrollSurface(scroll, false, false, "Panel horizontal scrollbar");
                CheckThemeScrollSurface(list, false, true, "List scrollbar");
                CheckThemeScrollSurface(text, false, true, "Multiline text scrollbar");
                VScrollBar gridScroll = grid.Controls.OfType<VScrollBar>().Single(control => control.Visible);
                Check(gridScroll.AccessibilityObject.Role == AccessibleRole.ScrollBar, "The grid scrollbar retains its native accessible scrollbar role.");
                CheckThemeScrollSurface(gridScroll, true, true, "Grid scrollbar");
                grid.FirstDisplayedScrollingRowIndex = 25; Application.DoEvents();
                Check(grid.FirstDisplayedScrollingRowIndex == 25, "The styled grid retains its scroll-position contract.");
                using (Bitmap image = ThemeCapture(host)) image.Save(Path.Combine(artifacts, "preview-theme-controls.png"));
                host.Close();
            }
            Console.WriteLine("NATIVE THEME PASS: " + (assertions - started) + " assertions; real native arrows, numeric editing, popup lifecycle, accessibility and scrollbar surfaces.");
        }
    }
}
