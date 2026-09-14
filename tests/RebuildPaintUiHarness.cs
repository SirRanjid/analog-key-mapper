using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static List<string> rebuildPaintFailures;

        static void RebuildPaintCheck(bool valid, string description)
        {
            assertions++;
            if (!valid) { rebuildPaintFailures.Add(description); Console.WriteLine("REBUILD PAINT FAILURE: " + description); }
        }

        static void CheckRebuiltSurface(Bitmap bitmap, Rectangle area, string description)
        {
            area.Intersect(new Rectangle(Point.Empty, bitmap.Size));
            RebuildPaintCheck(area.Width > 2 && area.Height > 2, description + " exposes a visible native drawing area.");
            int bright = 0, painted = 0, count = area.Width * area.Height;
            int[] surfaces = { ModernTheme.Background.ToArgb() & 0xFFFFFF, ModernTheme.Surface.ToArgb() & 0xFFFFFF,
                ModernTheme.SurfaceAlt.ToArgb() & 0xFFFFFF, ModernTheme.Border.ToArgb() & 0xFFFFFF };
            for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 205 && pixel.G > 205 && pixel.B > 205) bright++;
                if (Array.IndexOf(surfaces, pixel.ToArgb() & 0xFFFFFF) >= 0) painted++;
            }
            RebuildPaintCheck(count > 0 && painted >= count / 4, description + " contains existing app palette pixels, not an empty capture (" + painted + "/" + count + ").");
            RebuildPaintCheck(count > 0 && bright < count / 8, description + " has no native light surface (bright " + bright + "/" + count + ").");
        }

        static void CheckRebuiltFrame(Panel panel, string artifacts, string description)
        {
            Check(panel.Visible && panel.IsHandleCreated, description + " checks the actual visible application viewport.");
            ScrollbarDrawing.ScrollInfo info;
            Check(ScrollbarDrawing.Read(panel.Handle, 0xFFFFFFFB, out info), description + " has a real vertical native scrollbar.");
            NativeControlPaint.Rect window;
            Check(NativeControlPaint.GetWindowRect(panel.Handle, out window), description + " has native window bounds.");
            Rectangle area = info.Area.Bounds; area.Offset(-window.Left, -window.Top);
            // This is deliberately not DrawToBitmap/WM_PRINT/Refresh. Those
            // repaint the surface and concealed the original rebuild bug.
            using (Bitmap bitmap = ThemeCaptureExistingDc(panel))
            {
                int before = rebuildPaintFailures.Count;
                CheckRebuiltSurface(bitmap, area, description);
                if (rebuildPaintFailures.Count != before || description == "keys-final" || description == "curve-final")
                    bitmap.Save(Path.Combine(artifacts, "preview-rebuild-" + description + ".png"));
            }
        }

        static void CheckRebuiltKeyChildren(MainForm form, string description)
        {
            foreach (string name in new[] { "targets", "pressureScaleMaximum", "addTargetButton" })
            {
                Control control = Field<Control>(form, name);
                Check(control.Visible && control.IsHandleCreated, description + " checks the existing " + name + " child window.");
                using (Bitmap bitmap = ThemeCaptureExistingDc(control))
                    CheckRebuiltSurface(bitmap, new Rectangle(Point.Empty, bitmap.Size), description + "/" + name);
            }
        }

        static void ObserveRebuild(MainForm form, Panel panel, string artifacts, string name, Action change, bool keyChildren)
        {
            change();
            CheckRebuiltFrame(panel, artifacts, name + "-immediate");
            if (keyChildren) CheckRebuiltKeyChildren(form, name + "-immediate");
            // Ordinary queued application messages are allowed to run, but no
            // synthetic Layout/Update/Refresh is allowed to repair the result.
            Application.DoEvents();
            CheckRebuiltFrame(panel, artifacts, name + "-queued");
            if (keyChildren) CheckRebuiltKeyChildren(form, name + "-queued");
        }

        static void PlaceRebuildViewport(MainForm form, Panel panel)
        {
            Point point = panel.PointToScreen(Point.Empty);
            form.Location = new Point(form.Left + 24 - point.X, form.Top + 24 - point.Y);
            Application.DoEvents(); form.Update();
        }

        static void RunRebuildPainting(MainForm form, string artifacts)
        {
            int started = assertions;
            rebuildPaintFailures = new List<string>();
            Point originalLocation = form.Location;
            Size originalClientSize = form.ClientSize;
            bool originalTopMost = form.TopMost;
            try
            {
                // Only this freshly created preview window is made drawable;
                // its keyboard/runtime remain passive and the user's app is
                // never opened, captured or sent messages.
                AssertPassive(form);
                Call(form, "ShowPage", "mapping");
                SelectKeys(form, 14);
                Call(form, "SetDetailMode", null, true, false);
                form.TopMost = true;
                Panel keyPanel = Field<Panel>(form, "keyCardScroll");
                PlaceRebuildViewport(form, keyPanel);
                CheckRebuiltFrame(keyPanel, artifacts, "keys-baseline");
                ObserveRebuild(form, keyPanel, artifacts, "keys-same-tab", delegate { Call(form, "SetDetailMode", null, true, false); }, true);
                ObserveRebuild(form, keyPanel, artifacts, "keys-return-tab", delegate {
                    Call(form, "SetDetailMode", "advanced", true, false);
                    Call(form, "SetDetailMode", null, true, false);
                }, true);
                ObserveRebuild(form, keyPanel, artifacts, "keys-reveal-bottom", delegate {
                    Call(form, "ScrollKeySettingsTo", Field<Control>(form, "keySocdPanel"));
                }, false);
                Check(keyPanel.AutoScrollPosition.Y < 0, "Revealing the lower key settings still moves the real native viewport.");
                ObserveRebuild(form, keyPanel, artifacts, "keys-reveal-top", delegate {
                    Call(form, "ScrollKeySettingsTo", Field<Control>(form, "keyTitle"));
                }, true);
                Check(keyPanel.AutoScrollPosition == Point.Empty, "Revealing the key title still returns to the top.");
                ObserveRebuild(form, keyPanel, artifacts, "keys-reset-zero", delegate {
                    keyPanel.AutoScrollPosition = Point.Empty;
                    NativeSurfaceTheme.RefreshLayout(keyPanel);
                }, true);
                ObserveRebuild(form, keyPanel, artifacts, "keys-offset", delegate {
                    keyPanel.AutoScrollPosition = new Point(0, 35);
                    Point actual = keyPanel.AutoScrollPosition;
                    NativeSurfaceTheme.RefreshLayout(keyPanel);
                    Check(keyPanel.AutoScrollPosition == actual && actual.Y < 0, "Completing a managed scroll preserves the actual native position.");
                }, false);
                ObserveRebuild(form, keyPanel, artifacts, "keys-reset-from-offset", delegate { Call(form, "SetDetailMode", null, true, false); }, true);
                CheckRebuiltFrame(keyPanel, artifacts, "keys-final");

                // At a narrower supported window the curve settings exceed
                // their viewport, so the frame probe has a real scroll range.
                form.ClientSize = new Size(1320, 750);
                Call(form, "SetDetailMode", "advanced", true, false);
                Panel curvePanel = Field<Panel>(form, "curveEditorScroll");
                PlaceRebuildViewport(form, curvePanel);
                CheckRebuiltFrame(curvePanel, artifacts, "curve-baseline");
                ObserveRebuild(form, curvePanel, artifacts, "curve-same-tab", delegate { Call(form, "SetDetailMode", "advanced", true, false); }, false);
                ObserveRebuild(form, curvePanel, artifacts, "curve-return-tab", delegate {
                    Call(form, "SetDetailMode", null, true, false);
                    Call(form, "SetDetailMode", "advanced", true, false);
                }, false);
                ObserveRebuild(form, curvePanel, artifacts, "curve-reveal-settings", delegate {
                    Call(form, "ScrollCurveSettingsTo", Field<Control>(form, "settings"));
                }, false);
                Check(curvePanel.AutoScrollPosition.Y < 0, "Revealing curve settings keeps native scrolling functional.");
                ObserveRebuild(form, curvePanel, artifacts, "curve-reset", delegate { Call(form, "SetDetailMode", "advanced", true, false); }, false);
                Check(curvePanel.AutoScrollPosition == Point.Empty, "Returning to Curve restores its top viewport position.");
                CheckRebuiltFrame(curvePanel, artifacts, "curve-final");
                AssertPassive(form);
                Check(rebuildPaintFailures.Count == 0, "Actual app rebuilds and managed scrolling retain themed pixels before and after queued paints.");
                Console.WriteLine("REBUILD PAINT PASS: " + (assertions - started) + " assertions; real key/curve viewports, native frame and child control pixels, scroll semantics and passive preview.");
            }
            finally { form.TopMost = originalTopMost; form.Location = originalLocation; form.ClientSize = originalClientSize; }
        }
    }
}
