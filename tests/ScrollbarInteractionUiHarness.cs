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
        [DllImport("user32.dll", ExactSpelling = true)] static extern IntPtr GetCapture();
        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true)] static extern bool InteractionWindowVisible(IntPtr window);
        sealed class InteractionPanel : Panel { public void RecreateNativeHandle() { RecreateHandle(); } }
        sealed class InteractionGrid : DataGridView { public void RecreateNativeHandle() { RecreateHandle(); } }
        sealed class InteractionFocusBar : VScrollBar { public void RecreateNativeHandle() { RecreateHandle(); } }

        // Installed before ModernTheme, below the app's input owner. An input
        // message seen here would reach the original native scrollbar painter.
        // This catches a white flash even when a later app repaint hides it.
        sealed class ScrollDeliveryProbe : NativeWindow, IDisposable
        {
            public bool Track;
            public int MouseMessagesPassed;
            readonly bool standalone;
            public ScrollDeliveryProbe(IntPtr window, bool isStandalone) { standalone = isStandalone; AssignHandle(window); }
            protected override void WndProc(ref Message message)
            {
                int kind = message.Msg;
                bool nonClient = kind == 0x00A0 || kind == 0x00A1 || kind == 0x00A2 || kind == 0x02A2;
                bool capturedClient = (standalone || GetCapture() == Handle) && (kind == 0x0200 || kind == 0x0201 || kind == 0x0202 || kind == 0x02A3);
                if (Track && (nonClient || capturedClient)) MouseMessagesPassed++;
                base.WndProc(ref message);
            }
            public void Dispose() { ReleaseHandle(); }
        }

        sealed class ScrollInteractionCase
        {
            public string Name;
            public IntPtr Window;
            public bool Standalone, Vertical = true;
            public Func<int> Position;
            public ScrollDeliveryProbe Delivery;
        }
        static readonly HashSet<string> interactionImages = new HashSet<string>();
        static List<string> interactionFailures;

        static void InteractionCheck(bool value, string description)
        {
            assertions++;
            if (!value) { interactionFailures.Add(description); Console.WriteLine("SCROLL INTERACTION FAILURE: " + description); }
        }
        static IntPtr InteractionPoint(Point point)
        { return new IntPtr((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16)); }
        static ScrollbarDrawing.ScrollInfo InteractionGeometry(ScrollInteractionCase value)
        {
            ScrollbarDrawing.ScrollInfo info;
            Check(ScrollbarDrawing.Read(value.Window, value.Standalone ? 0xFFFFFFFC : value.Vertical ? 0xFFFFFFFB : 0xFFFFFFFA, out info), value.Name + " exposes its actual visible native scrollbar geometry.");
            Check(info.ThumbEnd > info.ThumbStart, value.Name + " has a draggable thumb with a nonempty scroll range.");
            return info;
        }
        static Point InteractionThumb(ScrollInteractionCase value, ScrollbarDrawing.ScrollInfo info)
        {
            return value.Vertical
                ? new Point((info.Area.Left + info.Area.Right) / 2, info.Area.Top + (info.ThumbStart + info.ThumbEnd) / 2)
                : new Point(info.Area.Left + (info.ThumbStart + info.ThumbEnd) / 2, (info.Area.Top + info.Area.Bottom) / 2);
        }
        static Point InteractionClient(ScrollInteractionCase value, Point screen)
        {
            Point origin = Point.Empty; Check(NativeControlPaint.ClientToScreen(value.Window, ref origin), value.Name + " exposes its own client origin.");
            return new Point(screen.X - origin.X, screen.Y - origin.Y);
        }
        static void InteractionDown(ScrollInteractionCase value, Point screen)
        {
            ThemeSend(value.Window, value.Standalone ? 0x0201 : 0x00A1,
                value.Standalone ? (IntPtr)1 : value.Vertical ? (IntPtr)7 : (IntPtr)6,
                InteractionPoint(value.Standalone ? InteractionClient(value, screen) : screen));
        }
        static void InteractionMove(ScrollInteractionCase value, Point screen)
        { ThemeSend(value.Window, 0x0200, (IntPtr)1, InteractionPoint(InteractionClient(value, screen))); }
        static void InteractionUp(ScrollInteractionCase value, Point screen)
        { ThemeSend(value.Window, 0x0202, IntPtr.Zero, InteractionPoint(InteractionClient(value, screen))); }

        static void CheckInteractionPixels(ScrollInteractionCase value, string artifacts, string stage)
        {
            ScrollbarDrawing.ScrollInfo info = InteractionGeometry(value);
            NativeControlPaint.Rect window; Check(NativeControlPaint.GetWindowRect(value.Window, out window), value.Name + " exposes native window bounds.");
            Rectangle area = info.Area.Bounds;
            using (Bitmap image = new Bitmap(area.Width, area.Height))
            {
                // Existing pixels only, including while the thumb is still
                // captured. Never invoke WM_PRINT/Refresh/Update to obtain them.
                IntPtr source = NativeControlPaint.GetWindowDC(value.Window);
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    IntPtr destination = graphics.GetHdc();
                    try { GdiFlush(); Check(source != IntPtr.Zero && BitBlt(destination, 0, 0, image.Width, image.Height, source, area.Left - window.Left, area.Top - window.Top, 0x00CC0020), value.Name + "/" + stage + " copies its existing scrollbar pixels (visible=" + InteractionWindowVisible(value.Window) + ", capture=" + GetCapture() + ", HWND=" + value.Window + ")."); }
                    finally { graphics.ReleaseHdc(destination); if (source != IntPtr.Zero) NativeControlPaint.ReleaseDC(value.Window, source); }
                }
                int bright = 0, painted = 0, count = image.Width * image.Height;
                int[] palette = { ModernTheme.Surface.ToArgb() & 0xFFFFFF, ModernTheme.SurfaceAlt.ToArgb() & 0xFFFFFF,
                    ModernTheme.Border.ToArgb() & 0xFFFFFF, ModernTheme.Background.ToArgb() & 0xFFFFFF };
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    if (pixel.R > 205 && pixel.G > 205 && pixel.B > 205) bright++;
                    if (Array.IndexOf(palette, pixel.ToArgb() & 0xFFFFFF) >= 0) painted++;
                }
                InteractionCheck(painted >= count / 4, value.Name + "/" + stage + " has drawn app-palette pixels, not a clipped/empty capture (" + painted + "/" + count + ").");
                InteractionCheck(bright < count / 8, value.Name + "/" + stage + " stays dark during interaction (" + bright + "/" + count + " bright).");
                int tintedThumb = 0;
                int extent = value.Vertical ? image.Height : image.Width;
                for (int coordinate = info.ArrowSize + 1; coordinate < extent - info.ArrowSize - 1; coordinate++)
                {
                    Color pixel = value.Vertical ? image.GetPixel(image.Width / 2, coordinate) : image.GetPixel(coordinate, image.Height / 2);
                    if (pixel.B > 120 && pixel.R > 60 && pixel.B > pixel.R + 15 && pixel.B > pixel.G + 15) tintedThumb++;
                }
                InteractionCheck(tintedThumb > 0, value.Name + "/" + stage + " keeps a visible app-colored thumb inside its track.");
                if ((bright >= count / 8 || painted < count / 4 || tintedThumb == 0 || stage == "drag-3") && interactionImages.Add(value.Name + "-" + stage))
                    image.Save(Path.Combine(artifacts, "preview-scroll-interaction-" + value.Name + "-" + stage + ".png"));
            }
        }

        static bool CheckContinuousScrollbarHover(ScrollInteractionCase value, string artifacts)
        {
            ScrollbarDrawing.ScrollInfo info = InteractionGeometry(value);
            Point thumb = InteractionThumb(value, info);
            int before = value.Delivery == null ? 0 : value.Delivery.MouseMessagesPassed;
            if (value.Delivery != null) value.Delivery.Track = true;
            for (int step = 0; step < 12; step++)
            {
                Point point = thumb;
                if (value.Vertical) point.Y += step % 3 - 1; else point.X += step % 3 - 1;
                ThemeSend(value.Window, value.Standalone ? 0x0200 : 0x00A0,
                    value.Standalone ? IntPtr.Zero : value.Vertical ? (IntPtr)7 : (IntPtr)6,
                    InteractionPoint(value.Standalone ? InteractionClient(value, point) : point));
                CheckInteractionPixels(value, artifacts, "hover-" + step);
                // Allow delayed native hover/fade work to run between samples.
                System.Threading.Thread.Sleep(12); Application.DoEvents();
                CheckInteractionPixels(value, artifacts, "hover-queued-" + step);
            }
            ThemeSend(value.Window, value.Standalone ? 0x02A3 : 0x02A2, IntPtr.Zero, IntPtr.Zero);
            CheckInteractionPixels(value, artifacts, "leave");
            bool owned = value.Delivery == null || value.Delivery.MouseMessagesPassed == before;
            InteractionCheck(owned, value.Name + " consumes scrollbar hover/leave before the native procedure can draw its own hover/fade frames.");
            // The old implementation fails here. Do not enter its native modal
            // tracking loop and then accidentally judge a post-drag repaint.
            return owned;
        }

        static bool CheckOwnedScrollbarDrag(ScrollInteractionCase value, string artifacts)
        {
            ScrollbarDrawing.ScrollInfo info = InteractionGeometry(value);
            Point press = InteractionThumb(value, info);
            Point release = press;
            int initial = value.Position();
            IntPtr previousCapture = GetCapture();
            int before = value.Delivery == null ? 0 : value.Delivery.MouseMessagesPassed;
            InteractionDown(value, press);
            bool captured = GetCapture() == value.Window;
            InteractionCheck(captured, value.Name + " begins its own drag and returns immediately with mouse capture held.");
            if (!captured) return false;
            try
            {
                CheckInteractionPixels(value, artifacts, "pressed");
                int previous = initial;
                for (int step = 1; step <= 4; step++)
                {
                    int length = value.Vertical ? info.Area.Bottom - info.Area.Top : info.Area.Right - info.Area.Left;
                    int offset = info.ArrowSize + (length - 2 * info.ArrowSize) * step / 5;
                    Point moved = value.Vertical ? new Point(press.X, info.Area.Top + offset) : new Point(info.Area.Left + offset, press.Y);
                    release = moved;
                    InteractionMove(value, moved);
                    CheckInteractionPixels(value, artifacts, "drag-" + step);
                    int actual = value.Position();
                    InteractionCheck(actual >= previous, value.Name + " advances monotonically while the thumb remains held at drag step " + step + ".");
                    InteractionCheck(GetCapture() == value.Window, value.Name + " retains its drag capture through step " + step + ".");
                    previous = actual;
                    Application.DoEvents(); CheckInteractionPixels(value, artifacts, "drag-queued-" + step);
                }
                InteractionCheck(value.Position() > initial, value.Name + " changes real content/value while dragging, before button-up.");
            }
            finally { InteractionUp(value, release); }
            InteractionCheck(GetCapture() == previousCapture, value.Name + " restores the previous capture at button-up (including an already open popup).");
            InteractionCheck(value.Delivery == null || value.Delivery.MouseMessagesPassed == before, value.Name + " handles down/move/up without forwarding them to native tracking.");
            CheckInteractionPixels(value, artifacts, "released");
            return true;
        }

        static void CheckScrollbarCaptureLoss(ScrollInteractionCase value, Form host, string artifacts)
        {
            Point press = InteractionThumb(value, InteractionGeometry(value));
            InteractionDown(value, press); Check(GetCapture() == value.Window, value.Name + " starts the capture-loss fixture with its own drag.");
            host.Capture = true;
            int stopped = value.Position();
            Point moved = new Point(press.X + (value.Vertical ? 0 : 30), press.Y + (value.Vertical ? 30 : 0));
            InteractionMove(value, moved);
            InteractionCheck(value.Position() == stopped && GetCapture() == host.Handle, value.Name + " cancels dragging when another owned control takes capture.");
            host.Capture = false;
            CheckInteractionPixels(value, artifacts, "capture-lost");
        }

        static ScrollInteractionCase InteractionCase(string name, Control control, bool standalone, bool vertical, Func<int> position, ScrollDeliveryProbe delivery)
        { return new ScrollInteractionCase { Name = name, Window = control.Handle, Standalone = standalone, Vertical = vertical, Position = position, Delivery = delivery }; }

        static void PumpInteractionFor(int milliseconds, Action sample)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int nextSample = 0;
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                System.Threading.Thread.Sleep(15); Application.DoEvents();
                if (sample != null && watch.ElapsedMilliseconds >= nextSample) { sample(); nextSample += 100; }
            }
        }

        static Point InteractionForwardArrow(ScrollInteractionCase value)
        {
            ScrollbarDrawing.ScrollInfo info = InteractionGeometry(value);
            return value.Vertical
                ? new Point((info.Area.Left + info.Area.Right) / 2, info.Area.Bottom - Math.Max(1, info.ArrowSize / 2))
                : new Point(info.Area.Right - Math.Max(1, info.ArrowSize / 2), (info.Area.Top + info.Area.Bottom) / 2);
        }

        static void CheckScrollbarRepeating(ScrollInteractionCase value, InteractionPanel panel, Form host, string artifacts)
        {
            panel.AutoScrollPosition = Point.Empty; NativeSurfaceTheme.RefreshLayout(panel);
            Point arrow = InteractionForwardArrow(value);
            int initial = value.Position();
            InteractionDown(value, arrow);
            int first = value.Position();
            InteractionCheck(GetCapture() == value.Window && first > initial, "Holding a scrollbar arrow applies one immediate step and retains capture.");
            PumpInteractionFor(570, delegate { CheckInteractionPixels(value, artifacts, "arrow-held"); });
            InteractionCheck(value.Position() > first, "The held arrow repeats after its initial delay.");
            InteractionUp(value, arrow);
            int released = value.Position();
            PumpInteractionFor(170, null);
            InteractionCheck(value.Position() == released && GetCapture() != value.Window, "Mouse-up stops arrow repetition and releases capture.");

            panel.AutoScrollPosition = Point.Empty; NativeSurfaceTheme.RefreshLayout(panel);
            ScrollbarDrawing.ScrollInfo info = InteractionGeometry(value);
            Point page = new Point((info.Area.Left + info.Area.Right) / 2, info.Area.Top + (info.Area.Bottom - info.Area.Top) * 3 / 4);
            InteractionDown(value, page);
            first = value.Position();
            InteractionCheck(first >= panel.VerticalScroll.LargeChange, "Clicking the scrollbar track applies a real page step.");
            PumpInteractionFor(570, delegate { CheckInteractionPixels(value, artifacts, "page-held"); });
            InteractionCheck(value.Position() > first, "Holding the track repeats page steps while its pointer remains beyond the thumb.");
            InteractionUp(value, page);

            arrow = InteractionForwardArrow(value); InteractionDown(value, arrow);
            host.Capture = true; int canceled = value.Position();
            PumpInteractionFor(510, null);
            InteractionCheck(value.Position() == canceled && GetCapture() == host.Handle, "Capture loss stops a held arrow before its repeat timer can act.");
            host.Capture = false;

            InteractionDown(value, arrow); panel.Enabled = false; int disabled = value.Position();
            PumpInteractionFor(510, null);
            InteractionCheck(value.Position() == disabled && GetCapture() != value.Window, "Disabling a held scrollbar stops its timer and releases its capture.");
            panel.Enabled = true;
            CheckInteractionPixels(value, artifacts, "repeat-stopped");
        }

        static void CheckScrollbarMutationAndHiding(Form host, string artifacts)
        {
            using (VScrollBar bar = new VScrollBar { Bounds = new Rectangle(456, 342, 22, 145), Minimum = 0, Maximum = 1000, LargeChange = 30, SmallChange = 2 })
            using (InteractionPanel panel = new InteractionPanel { Bounds = new Rectangle(488, 342, 188, 145), AutoScroll = true, AutoScrollMinSize = new Size(0, 2000) })
            {
                host.Controls.Add(bar); host.Controls.Add(panel); bar.BringToFront(); panel.BringToFront(); host.Refresh(); Application.DoEvents();
                var mutable = InteractionCase("range-mutated", bar, true, true, delegate { return bar.Value; }, null);
                bool changed = false;
                bar.Scroll += delegate(object sender, ScrollEventArgs args) {
                    if (args.Type == ScrollEventType.ThumbTrack && !changed)
                    { changed = true; bar.Maximum = 50; bar.LargeChange = 10; args.NewValue = 17; }
                };
                Point thumb = InteractionThumb(mutable, InteractionGeometry(mutable));
                InteractionDown(mutable, thumb);
                Point moved = new Point(thumb.X, thumb.Y + 40);
                InteractionMove(mutable, moved);
                InteractionCheck(changed && bar.Value == 17 && bar.Maximum == 50, "A scroll handler can change the range and adjust NewValue during dragging.");
                CheckInteractionPixels(mutable, artifacts, "handler-adjusted-range");
                InteractionUp(mutable, moved);
                InteractionCheck(bar.Value >= bar.Minimum && bar.Value <= bar.Maximum - bar.LargeChange + 1, "Committing after a range change keeps the native value inside the new range.");

                var hidden = InteractionCase("hidden-in-scroll", panel, false, true, delegate { return -panel.AutoScrollPosition.Y; }, null);
                bool hide = true;
                panel.Scroll += delegate { if (hide) { hide = false; panel.Visible = false; } };
                IntPtr original = panel.Handle;
                InteractionDown(hidden, InteractionForwardArrow(hidden));
                InteractionCheck(!hide && !panel.Visible && !InteractionWindowVisible(original), "A scroll handler that hides its viewport remains hidden when the redraw transaction completes.");
                InteractionCheck(GetCapture() != original, "Hiding a viewport from its scroll handler releases the in-progress pointer capture.");
                PumpInteractionFor(430, null);
                panel.Visible = true; NativeSurfaceTheme.RefreshLayout(panel); Application.DoEvents();
                CheckInteractionPixels(hidden, artifacts, "reshown-after-handler");

                bool recreated = false;
                panel.Scroll += delegate { if (!recreated) { recreated = true; panel.RecreateNativeHandle(); } };
                original = panel.Handle;
                InteractionDown(hidden, InteractionForwardArrow(hidden));
                InteractionCheck(recreated && panel.IsHandleCreated && panel.Visible && InteractionWindowVisible(panel.Handle), "Handle recreation inside Scroll leaves the replacement viewport visible and redraw-enabled.");
                InteractionCheck(GetCapture() != original, "Recreating the scrolling handle cancels the old drag capture.");
                hidden.Window = panel.Handle; CheckInteractionPixels(hidden, artifacts, "recreated-in-handler");
                InteractionDown(hidden, InteractionForwardArrow(hidden)); InteractionUp(hidden, InteractionForwardArrow(hidden));
                CheckInteractionPixels(hidden, artifacts, "recreated-input-again");
            }
        }

        static void CheckScrollbarClickFocus(Form host, InteractionGrid grid, string artifacts)
        {
            using (Button focus = new SleekButton { Text = "Synthetic focus target", Bounds = new Rectangle(488, 450, 188, 36) })
            {
                host.Controls.Add(focus); focus.BringToFront();
                for (int orientation = 0; orientation < 2; orientation++)
                    foreach (bool tabStop in new[] { false, true })
                    using (ScrollBar bar = orientation == 0 ? (ScrollBar)new VScrollBar() : new HScrollBar())
                    {
                        bar.Bounds = orientation == 0 ? new Rectangle(456, 342, 22, 145) : new Rectangle(456, 342, 220, 22);
                        bar.Minimum = 0; bar.Maximum = 1000; bar.LargeChange = 30; bar.TabStop = tabStop;
                        host.Controls.Add(bar); bar.BringToFront(); host.Refresh(); Application.DoEvents();
                        Check(focus.Focus() && focus.Focused, "The click-focus fixture begins with focus on its own unrelated button.");
                        bool gotFocus = false; bar.GotFocus += delegate { gotFocus = true; };
                        var value = InteractionCase("focus-" + orientation + "-" + tabStop, bar, true, orientation == 0, delegate { return bar.Value; }, null);
                        Point thumb = InteractionThumb(value, InteractionGeometry(value));
                        InteractionDown(value, thumb);
                        InteractionCheck(gotFocus == tabStop && bar.Focused == tabStop && focus.Focused != tabStop,
                            "A " + (orientation == 0 ? "vertical" : "horizontal") + " scrollbar takes click focus exactly when TabStop is true.");
                        InteractionCheck(GetCapture() == bar.Handle, "Click-focus policy preserves normal scrollbar drag capture.");
                        CheckInteractionPixels(value, artifacts, "focus-pressed");
                        InteractionUp(value, thumb);
                    }

                foreach (ScrollBar bar in grid.Controls.OfType<ScrollBar>().Where(control => control.Visible))
                {
                    Check(!bar.TabStop && grid.Focus(), "The real grid-owned scrollbar has TabStop=false and the grid can own focus.");
                    var value = InteractionCase("grid-click-focus-" + (bar is VScrollBar), bar, true, bar is VScrollBar, delegate { return bar.Value; }, null);
                    Point thumb = InteractionThumb(value, InteractionGeometry(value));
                    InteractionDown(value, thumb);
                    InteractionCheck(grid.Focused && !bar.Focused, "Dragging a real grid scrollbar preserves focus on its grid.");
                    InteractionUp(value, thumb);
                }

                using (InteractionFocusBar bar = new InteractionFocusBar { Bounds = new Rectangle(456, 342, 22, 145), Minimum = 0, Maximum = 1000, LargeChange = 30, TabStop = true })
                {
                    host.Controls.Add(bar); bar.BringToFront(); host.Refresh(); Application.DoEvents();
                    Check(focus.Focus() && focus.Focused, "The focus-mutation fixture begins on its own button.");
                    bool changed = false;
                    bar.GotFocus += delegate {
                        if (changed) return; changed = true;
                        bar.RecreateNativeHandle();
                    };
                    IntPtr original = bar.Handle;
                    var value = InteractionCase("focus-handler-recreate", bar, true, true, delegate { return bar.Value; }, null);
                    Point thumb = InteractionThumb(value, InteractionGeometry(value));
                    InteractionDown(value, thumb);
                    InteractionCheck(changed && GetCapture() != original && (bar.IsDisposed || !bar.IsHandleCreated || GetCapture() != bar.Handle),
                        "A focus handler that recreates the scrollbar cancels the old click before it takes drag capture.");
                    Check(bar.IsHandleCreated && bar.Visible, "Recreation in GotFocus keeps the replacement scrollbar alive.");
                    Application.DoEvents(); value.Window = bar.Handle;
                    CheckInteractionPixels(value, artifacts, "focus-recreated");
                    thumb = InteractionThumb(value, InteractionGeometry(value));
                    InteractionDown(value, thumb);
                    InteractionCheck(GetCapture() == bar.Handle, "The replacement scrollbar can accept a fresh click after GotFocus recreated its handle.");
                    InteractionUp(value, thumb);
                }
            }
        }

        static void RunScrollbarInteraction(MainForm unusedPreview, string artifacts)
        {
            int started = assertions;
            interactionFailures = new List<string>(); interactionImages.Clear();
            using (Form host = new ThemeDcHost { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(24, 24), ClientSize = new Size(690, 510), FormBorderStyle = FormBorderStyle.None, TopMost = true })
            using (InteractionPanel panel = new InteractionPanel { Bounds = new Rectangle(12, 12, 210, 264), AutoScroll = true, AutoScrollMinSize = new Size(150000, 170000) })
            using (InteractionGrid grid = new InteractionGrid { Bounds = new Rectangle(234, 12, 442, 264), AllowUserToAddRows = false, RowHeadersVisible = false, ScrollBars = ScrollBars.Both })
            using (ListBox list = new ListBox { Bounds = new Rectangle(12, 290, 210, 200) })
            using (TextBox text = new TextBox { Bounds = new Rectangle(234, 290, 210, 200), Multiline = true, ScrollBars = ScrollBars.Vertical })
            using (ComboBox combo = new ComboBox { Bounds = new Rectangle(456, 290, 220, 30), DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed, MaxDropDownItems = 8, IntegralHeight = true })
            {
                grid.Columns.Add("Setting", "Setting"); grid.Columns.Add("Value", "Value"); grid.Columns[0].Width = 520; grid.Columns[1].Width = 260;
                for (int row = 0; row < 200; row++) { grid.Rows.Add("Synthetic setting " + row, row); list.Items.Add("Synthetic option " + row); combo.Items.Add("Synthetic option " + row); }
                text.Lines = Enumerable.Range(0, 300).Select(i => "Synthetic line " + i).ToArray();
                list.SelectedIndex = 2; combo.SelectedIndex = 3;
                host.Controls.AddRange(new Control[] { panel, grid, list, text, combo }); host.Show(); Application.DoEvents();
                VScrollBar verticalGrid = grid.Controls.OfType<VScrollBar>().Single(bar => bar.Visible);
                HScrollBar horizontalGrid = grid.Controls.OfType<HScrollBar>().Single(bar => bar.Visible);
                NativeControlPaint.ComboInfo comboInfo = new NativeControlPaint.ComboInfo(); comboInfo.Size = Marshal.SizeOf(typeof(NativeControlPaint.ComboInfo));
                Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref comboInfo), "The synthetic combo exposes only its own popup HWND.");
                var probes = new List<ScrollDeliveryProbe>();
                Func<IntPtr, bool, ScrollDeliveryProbe> probe = delegate(IntPtr window, bool standalone) { var created = new ScrollDeliveryProbe(window, standalone); probes.Add(created); return created; };
                try
                {
                    ScrollDeliveryProbe panelProbe = probe(panel.Handle, false), gridVProbe = probe(verticalGrid.Handle, true), gridHProbe = probe(horizontalGrid.Handle, true),
                        listProbe = probe(list.Handle, false), textProbe = probe(text.Handle, false), popupProbe = probe(comboInfo.List, false);
                    var unthemed = InteractionCase("unthemed-negative-control", panel, false, true, delegate { return -panel.AutoScrollPosition.Y; }, panelProbe);
                    panelProbe.Track = true;
                    Point unthemedThumb = InteractionThumb(unthemed, InteractionGeometry(unthemed));
                    ThemeSend(panel.Handle, 0x00A0, (IntPtr)7, InteractionPoint(unthemedThumb));
                    Check(panelProbe.MouseMessagesPassed > 0, "The negative control proves the delivery probe observes native hover before the app takes ownership.");
                    panelProbe.Track = false;
                    ModernTheme.Apply(host); host.Refresh(); Application.DoEvents();
                    int panelScrollEvents = 0, gridScrollEvents = 0;
                    panel.Scroll += delegate { panelScrollEvents++; }; grid.Scroll += delegate { gridScrollEvents++; };
                    var cases = new[] {
                        InteractionCase("panel-vertical-32bit", panel, false, true, delegate { return -panel.AutoScrollPosition.Y; }, panelProbe),
                        InteractionCase("panel-horizontal-32bit", panel, false, false, delegate { return -panel.AutoScrollPosition.X; }, panelProbe),
                        InteractionCase("grid-vertical", verticalGrid, true, true, delegate { return grid.FirstDisplayedScrollingRowIndex; }, gridVProbe),
                        InteractionCase("grid-horizontal", horizontalGrid, true, false, delegate { return grid.HorizontalScrollingOffset; }, gridHProbe),
                        InteractionCase("list-vertical", list, false, true, delegate { return list.TopIndex; }, listProbe),
                        InteractionCase("text-vertical", text, false, true, delegate { return ThemeSend(text.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32(); }, textProbe)
                    };
                    foreach (ScrollInteractionCase value in cases)
                    {
                        if (!CheckContinuousScrollbarHover(value, artifacts)) continue;
                        if (CheckOwnedScrollbarDrag(value, artifacts)) CheckScrollbarCaptureLoss(value, host, artifacts);
                    }
                    InteractionCheck(-panel.AutoScrollPosition.Y > 65535 && -panel.AutoScrollPosition.X > 65535, "Both panel axes retain full 32-bit drag positions beyond the 16-bit WM_SCROLL payload.");
                    InteractionCheck(panelScrollEvents > 0 && gridScrollEvents > 0, "Dragging preserves real panel and grid scroll notifications.");
                    InteractionCheck(list.SelectedIndex == 2, "Dragging the list scrollbar leaves its selected item unchanged.");

                    int disabledPosition = -panel.AutoScrollPosition.Y;
                    panel.Enabled = false;
                    ScrollInteractionCase disabled = cases[0];
                    Point disabledThumb = InteractionThumb(disabled, InteractionGeometry(disabled));
                    InteractionDown(disabled, disabledThumb); InteractionMove(disabled, new Point(disabledThumb.X, disabledThumb.Y + 24)); InteractionUp(disabled, disabledThumb);
                    InteractionCheck(-panel.AutoScrollPosition.Y == disabledPosition && GetCapture() != panel.Handle, "A disabled scrollbar cannot start a drag or move its content.");
                    panel.Enabled = true;

                    // Native handle recreation must retain ownership without
                    // test hooks or manually reattaching the production theme.
                    panel.RecreateNativeHandle(); Application.DoEvents();
                    panel.AutoScrollPosition = Point.Empty; NativeSurfaceTheme.RefreshLayout(panel);
                    ScrollInteractionCase recreated = InteractionCase("panel-recreated", panel, false, true, delegate { return -panel.AutoScrollPosition.Y; }, null);
                    CheckOwnedScrollbarDrag(recreated, artifacts);

                    combo.DroppedDown = true; Application.DoEvents();
                    Check(combo.DroppedDown, "The synthetic combo opens its own scrolling popup.");
                    Check(NativeControlPaint.GetComboBoxInfo(combo.Handle, ref comboInfo), "The opened combo exposes its current popup HWND.");
                    Check(popupProbe.Handle == comboInfo.List, "The popup delivery probe remains below the app input owner on the live popup handle.");
                    var popup = new ScrollInteractionCase { Name = "combo-popup-vertical", Window = comboInfo.List, Position = delegate { return ThemeSend(comboInfo.List, 0x018E, IntPtr.Zero, IntPtr.Zero).ToInt32(); }, Delivery = popupProbe };
                    if (CheckContinuousScrollbarHover(popup, artifacts)) CheckOwnedScrollbarDrag(popup, artifacts);
                    InteractionCheck(combo.SelectedIndex == 3, "Dragging the popup scrollbar does not commit a different combo selection.");
                    combo.DroppedDown = false;

                    CheckScrollbarClickFocus(host, grid, artifacts);
                    CheckScrollbarRepeating(recreated, panel, host, artifacts);
                    CheckScrollbarMutationAndHiding(host, artifacts);

                    bool closedFromScroll = false;
                    panel.Scroll += delegate { closedFromScroll = true; host.Close(); };
                    InteractionDown(recreated, InteractionForwardArrow(recreated));
                    InteractionCheck(closedFromScroll && host.IsDisposed && GetCapture() != recreated.Window, "Closing the owned test window from Scroll safely ends painting, capture and repetition.");
                }
                finally { if (!host.IsDisposed) host.Capture = false; foreach (ScrollDeliveryProbe delivery in probes) delivery.Dispose(); if (!host.IsDisposed) host.Close(); }
            }
            Check(interactionFailures.Count == 0, "All scrollbar interaction cases retain one painter and actual input semantics throughout hover and captured dragging.");
            Console.WriteLine("SCROLL INTERACTION PASS: " + (assertions - started) + " assertions; hover ownership, live dragging, 32-bit ranges, grid/list/text/popup content, capture loss and recreation.");
        }
    }
}
