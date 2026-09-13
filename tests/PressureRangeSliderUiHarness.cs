using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static void CheckPressureMarkerPainting(PressureRangeSlider slider)
        {
            double? saved = slider.MeasuredValue;
            var dirty = new List<Rectangle>();
            InvalidateEventHandler changed = delegate(object sender, InvalidateEventArgs e) { dirty.Add(e.InvalidRect); };
            MethodInfo paint = typeof(PressureRangeSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            slider.MeasuredValue = null;
            using (var retained = new Bitmap(slider.Width, slider.Height))
            {
                PaintPressureRegion(slider, paint, retained, slider.ClientRectangle);
                slider.Invalidated += changed;
                try
                {
                    double span = slider.RangeMaximum - slider.RangeMinimum;
                    foreach (double? sample in new double?[] { 0, span / 3, span / 2, span, span + 100, null, span / 4, null })
                    {
                        dirty.Clear(); slider.MeasuredValue = sample;
                        foreach (Rectangle area in dirty)
                        {
                            Check(area.Top > slider.Font.Height && area.Width < 16 && area.Height < slider.Height / 2,
                                "Moving the pressure tick repaints only its old/new marker area, below the range labels.");
                            PaintPressureRegion(slider, paint, retained, area);
                        }
                        using (var complete = new Bitmap(slider.Width, slider.Height))
                        {
                            PaintPressureRegion(slider, paint, complete, slider.ClientRectangle);
                            bool equal = true;
                            for (int y = 0; y < retained.Height && equal; y++)
                                for (int x = 0; x < retained.Width; x++)
                                    if (retained.GetPixel(x, y) != complete.GetPixel(x, y)) { equal = false; break; }
                            Check(equal, "Partial pressure updates exactly match a full redraw, including clearing the old tick.");
                        }
                    }
                    slider.MeasuredValue = span / 2; dirty.Clear();
                    slider.MeasuredValue = span / 2;
                    slider.MeasuredValue = span / 2 + 0.000001;
                    Check(dirty.Count == 0, "Equal values and subpixel pressure changes do not request another paint.");
                }
                finally { slider.Invalidated -= changed; slider.MeasuredValue = saved; }
            }
        }
        static void PaintPressureRegion(PressureRangeSlider slider, MethodInfo paint, Bitmap image, Rectangle area)
        {
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.SetClip(area);
                paint.Invoke(slider, new object[] { new PaintEventArgs(graphics, area) });
            }
        }

        // This contract check does not create a native control handle, show a
        // window, read input devices or alter focus. Gesture/layout checks run
        // separately with the regular synthetic UI fixture.
        static void CheckPressureRangeSliderContract()
        {
            using (var slider = new PressureRangeSlider())
            {
                int edits = 0, commits = 0;
                slider.ValueChanged += delegate { edits++; };
                slider.ValueCommitted += delegate { commits++; };
                slider.SetRange(0, 600, 10, 550);
                slider.MeasuredValue = 350;
                slider.MeasuredValue = null;
                Check(edits == 0 && commits == 0, "Loading a pressure range and live readings never writes settings.");
                Check(slider.RangeMaximum == 600 && slider.SelectedMinimum == 10 && slider.SelectedMaximum == 550,
                    "A different keyboard scale keeps its configured minimum and maximum.");

                MethodInfo keyDown = typeof(PressureRangeSlider).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var right = new KeyEventArgs(Keys.Right); keyDown.Invoke(slider, new object[] { right });
                Check(slider.SelectedMinimum == 11 && slider.SelectedMaximum == 550 && edits == 1 && commits == 1 && right.Handled,
                    "An arrow edits and commits only the focused minimum handle by one raw unit.");
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.Shift | Keys.Right) });
                Check(slider.SelectedMinimum == 21, "Shift and an arrow provide a predictable ten-unit adjustment.");
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.End) });
                Check(slider.SelectedMinimum == 549 && slider.SelectedMaximum == 550,
                    "The minimum stops before the maximum instead of crossing or collapsing the range.");
                int unchangedEdits = edits, unchangedCommits = commits;
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.Right) });
                Check(edits == unchangedEdits && commits == unchangedCommits, "A clamped keyboard action produces no redundant write.");
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.Home) });
                Check(slider.SelectedMinimum == 0, "Home reaches the global lower bound exactly.");

                // Choosing the other logical handle directly avoids making a
                // real focus transition in this otherwise headless check.
                typeof(PressureRangeSlider).GetField("activeHandle", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(slider, 1);
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.Home) });
                Check(slider.SelectedMaximum == 1, "The maximum stops after the minimum with a valid nonzero range.");
                keyDown.Invoke(slider, new object[] { new KeyEventArgs(Keys.End) });
                Check(slider.SelectedMaximum == 600, "The maximum can reach an expanded keyboard scale.");

                bool rejected = false;
                try { slider.SetRange(0, 65536, 0, 385); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && slider.RangeMaximum == 600 && slider.SelectedMaximum == 600,
                    "An invalid hardware scale cannot leave the control partially updated.");
                rejected = false;
                try { slider.SetRange(0, 600, 300, 300); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "A collapsed range is rejected before it can affect normalization.");
                rejected = false;
                try { slider.MeasuredValue = Double.NaN; } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "A nonfinite sample cannot enter marker geometry.");
                slider.SetRange(0, 65535, 0, 65535);
                Check(slider.SelectedMaximum == 65535 && !slider.IsDragging && !slider.IsHandleCreated,
                    "The full sensor range works without creating a window or touching hardware.");
            }
        }
    }
}
