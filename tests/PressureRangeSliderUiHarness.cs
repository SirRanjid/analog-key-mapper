using System;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
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
