using System;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static void PrecisionMouse(Control control, string name, Point point)
        {
            control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
        }
        static Point PrecisionPoint(Point origin, bool vertical, int along, int across)
        { return new Point(origin.X + (vertical ? across : along), origin.Y + (vertical ? along : across)); }
        static double PrecisionProperty(Control control, string name)
        { return Convert.ToDouble(control.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control, null), CultureInfo.InvariantCulture); }
        static int PrecisionPosition(Control control, double value)
        { return (int)Math.Round(Convert.ToDouble(control.GetType().GetMethod("Position", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { value }), CultureInfo.InvariantCulture)); }

        static void CheckFinePointerControl(Control control, bool vertical, Func<double> read, Action reset, Func<Point> pickup, string name)
        {
            reset(); Point start = pickup(); double original = read();
            PrecisionMouse(control, "OnMouseDown", start);
            Point direct = PrecisionPoint(start, vertical, 30, 0);
            PrecisionMouse(control, "OnMouseMove", direct); double coarse = Math.Abs(read() - original);
            PrecisionMouse(control, "OnMouseUp", direct);
            Check(coarse > 0, name + ": ordinary dragging changes the selected handle.");

            reset(); start = pickup(); original = read();
            int across = (int)Math.Round(152 * Math.Max(1, control.Font.Height / 15.0));
            Point away = PrecisionPoint(start, vertical, 0, across);
            PrecisionMouse(control, "OnMouseDown", start);
            PrecisionMouse(control, "OnMouseMove", away);
            Check(read() == original, name + ": moving perpendicular to the track alone keeps the exact value.");
            Point fine = PrecisionPoint(start, vertical, 30, across);
            PrecisionMouse(control, "OnMouseMove", fine); double edited = read(), fineChange = Math.Abs(edited - original);
            Check(fineChange > 0 && fineChange < coarse * .3, name + ": the same travel far from the track makes a smaller, usable adjustment.");
            Point back = PrecisionPoint(start, vertical, 30, 0);
            PrecisionMouse(control, "OnMouseMove", back);
            Check(read() == edited, name + ": returning to the track does not jump toward the physical pointer.");
            PrecisionMouse(control, "OnMouseUp", back);
            Check(read() == edited, name + ": releasing at the same point preserves the final fine value.");

            reset(); start = pickup(); original = read();
            across = (int)Math.Round(664 * Math.Max(1, control.Font.Height / 15.0));
            PrecisionMouse(control, "OnMouseDown", start);
            PrecisionMouse(control, "OnMouseMove", PrecisionPoint(start, vertical, 0, across));
            for (int i = 1; i <= 100; i++)
                PrecisionMouse(control, "OnMouseMove", PrecisionPoint(start, vertical, i, across));
            Check(read() != original, name + ": repeated sub-display increments accumulate instead of rounding back to zero each time.");
            edited = read(); PrecisionMouse(control, "OnMouseUp", PrecisionPoint(start, vertical, 100, across));
            Check(read() == edited, name + ": a fine gesture keeps its final accumulated value after commit.");

            reset(); start = pickup(); original = read();
            across = (int)Math.Round(152 * Math.Max(1, control.Font.Height / 15.0));
            PrecisionMouse(control, "OnMouseDown", start);
            PrecisionMouse(control, "OnMouseMove", PrecisionPoint(start, vertical, 20, across));
            double beforeRelease = read();
            PrecisionMouse(control, "OnMouseUp", PrecisionPoint(start, vertical, 30, across));
            Check(read() > beforeRelease && Math.Abs((read() - original) - fineChange) < 1e-9,
                name + ": mouse-up contributes its final fine delta even without a preceding move event.");

            reset(); start = pickup(); original = read();
            PrecisionMouse(control, "OnMouseDown", start);
            PrecisionMouse(control, "OnMouseMove", PrecisionPoint(start, vertical, 0, across));
            PrecisionMouse(control, "OnMouseUp", PrecisionPoint(start, vertical, 0, across));
            Check(read() == original, name + ": an orthogonal-only completed gesture retains the exact original value.");
        }

        static void RunSliderPrecisionUi(MainForm form)
        {
            int started = assertions;
            // These controls belong exclusively to the isolated preview form.
            // Native focus/capture never target the user's mapper or desktop.
            using (var host = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Surface })
            using (var pressure = new PressureRangeSlider { Bounds = new Rectangle(20, 20, 380, 64) })
            using (var threshold = new InputThresholdSlider { Bounds = new Rectangle(20, 120, 90, 180) })
            using (var rail = new CurveRangeRail { Bounds = new Rectangle(160, 120, 22, 180) })
            using (var grid = new CurveSettingsGrid { Bounds = new Rectangle(240, 120, 420, 90), ColumnHeadersVisible = false,
                RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToResizeRows = false })
            {
                form.Controls.Add(host); host.BringToFront();
                host.Controls.AddRange(new Control[] { pressure, threshold, rail, grid });
                var column = new DataGridViewColumn(new CurveSettingSliderCell()) { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true };
                grid.Columns.Add(column); grid.Rows.Add(.25); grid.Rows[0].Height = 32;
                var cell = (CurveSettingSliderCell)grid.Rows[0].Cells[0];
                cell.Range = new CurveSettingRange { Property = "MinOutput", Minimum = 0, Maximum = 1, Step = .001, FirstValue = .25, Available = true };
                grid.CurrentCell = cell; Pump(form);
                int pressureCommits = 0, thresholdCommits = 0, railCommits = 0, gridCommits = 0;
                pressure.ValueCommitted += delegate { pressureCommits++; };
                threshold.Committed += delegate { thresholdCommits++; };
                rail.Committed += delegate { railCommits++; };
                grid.SliderCommitted += delegate { gridCommits++; };

                CheckFinePointerControl(pressure, false, delegate { return pressure.SelectedMinimum; },
                    delegate { pressure.SetRange(0, 1000, 200, 800); },
                    delegate { return new Point(PrecisionPosition(pressure, pressure.SelectedMinimum), (int)Math.Round(PrecisionProperty(pressure, "TrackY"))); }, "Pressure range");
                CheckFinePointerControl(threshold, true, delegate { return threshold.Value; },
                    delegate { threshold.SetValue(25, false); },
                    delegate { return new Point((int)Math.Round(PrecisionProperty(threshold, "TrackX")), PrecisionPosition(threshold, threshold.Value)); }, "Actuation/release");
                CheckFinePointerControl(rail, true, delegate { return rail.ValueAt(0); },
                    delegate { rail.Configure(0, .25, false, 0, .7); rail.Configure(1, .75, false, .3, 1); },
                    delegate { return new Point(rail.Width / 2, PrecisionPosition(rail, rail.ValueAt(0))); }, "Curve range");
                CheckFinePointerControl(grid, false, delegate { return Convert.ToDouble(cell.Value, CultureInfo.InvariantCulture); },
                    delegate { cell.Value = .25; }, delegate {
                        Rectangle track = CurveSettingSliderCell.Track(grid.GetCellDisplayRectangle(0, 0, false));
                        return new Point((int)Math.Round(track.Left + track.Width * .25), track.Top);
                    }, "Curve settings");
                Check(pressureCommits == 4 && thresholdCommits == 4 && railCommits == 4 && gridCommits == 4,
                    "Every control emits one completed edit per changed drag, never for previews or an orthogonal-only gesture.");
                form.Controls.Remove(host);
            }
            Console.WriteLine("SLIDER PRECISION UI PASS: " + (assertions - started) + " assertions; all four real control gestures and completed edits.");
        }
    }
}
