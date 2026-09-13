using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static object SliderCall(CurveSettingsGrid grid, string name, params object[] arguments)
        { return typeof(CurveSettingsGrid).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(grid, arguments); }
        static void CheckCurveSettingsSliders(MainForm form, string artifacts)
        {
            int started = assertions;
            Profile original = Current(form);
            Profile fixture = ControllerRouting.Add(original, "curve-slider-other", "Other", ControllerKind.Xbox360);
            fixture.Bindings = new List<Binding> {
                new Binding { KeyIndex = 9, Target = OutputTarget.LeftXNegative, Processing = new SignalSettings { TopDeadzone = .05, BottomDeadzone = .2, Hysteresis = .02, Scale = .3 } },
                new Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive, Processing = new SignalSettings { TopDeadzone = .1, BottomDeadzone = .1, Hysteresis = .05, Scale = .6 } },
                new Binding { KeyIndex = 21, Target = OutputTarget.A },
                new Binding { KeyIndex = 9, ControllerId = "curve-slider-other", Target = OutputTarget.B }
            };
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "advanced");
            string[] scope = Current(form).Bindings.Where(b => (b.KeyIndex == 9 || b.KeyIndex == 14) && b.ControllerId == ControllerRouting.DefaultControllerId).Select(b => b.BindingId).ToArray();
            SelectBindings(form, scope[0]);
            var grid = (CurveSettingsGrid)Field<DataGridView>(form, "settings");
            var canvas = Field<CurveCanvas>(form, "curve");
            Check(grid.Columns.Count == 3 && grid.Columns[1].Name == "value" && grid.Columns[2].DisplayIndex == 1,
                "Every continuous setting has a slider beside the retained numeric editor.");
            Check(grid.Rows.Cast<DataGridViewRow>().Count(r => ((CurveSettingSliderCell)r.Cells[2]).Range != null) == 10,
                "All ten continuous curve-tab settings offer sliders.");
            foreach (DataGridViewRow row in grid.Rows)
            {
                Check(row.Cells.Cast<DataGridViewCell>().All(c => c.ToolTipText.Length > 20 && c.ToolTipText.Length <= QuietToolTip.MaximumTextLength && c.ToolTipText.IndexOf('\n') < 0),
                    "Every setting offers a concise single-paragraph hover reminder.");
                Check(row.Cells.Cast<DataGridViewCell>().All(c => c.AccessibilityObject.Description.Length > 180 && c.AccessibilityObject.Description.Contains("Esc")),
                    "Full setting effects, gesture help and selection scope remain in each cell's accessible description.");
                Check(row.Height == Math.Max(32, grid.Font.Height + 14), "Curve setting rows have compact, font-safe height without a duplicate slider readout.");
                var cell = (CurveSettingSliderCell)row.Cells[2]; if (cell.Range == null) continue;
                foreach (double boundary in new[] { cell.Range.Minimum, cell.Range.Maximum })
                {
                    Profile bounded = KeyEditing.ApplyProperty(Current(form), scope, cell.Range.Property, boundary.ToString("R", CultureInfo.InvariantCulture));
                    Check(MappingValidation.ValidateProfile(bounded).Count == 0, "Slider limits preserve every selected mapping's dependent bounds: " + cell.Range.Property);
                }
            }
            Check(!grid.ShowCellToolTips, "The grid's intrusive native tooltip is replaced by delayed compact help.");
            string untouched = Json(Current(form));
            SignalSettings originalResponse = canvas.Settings;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var cell = (CurveSettingSliderCell)row.Cells[2]; if (cell.Range == null) continue;
                double next = cell.Range.Minimum + (cell.Range.Maximum - cell.Range.Minimum) * .6;
                SliderCall(grid, "BeginSlider", cell, false); SliderCall(grid, "Preview", next);
                Check(!canvas.ShapeEditing && canvas.ActiveSetting == cell.Range.Property, "Every slider shows its live effective response or relevant guide: " + cell.Range.Property);
                Check(Math.Abs((double)typeof(SignalSettings).GetField(cell.Range.Property).GetValue(canvas.Settings) - next) < 1e-10,
                    "All numeric slider drafts reach the graph, not only exponent: " + cell.Range.Property);
                double[] samples = CurveCanvasSamples(canvas);
                double[] expectedSamples = CurveResponsePreview.Create(canvas.Settings, samples.Length - 1).Press;
                Check(samples.SequenceEqual(expectedSamples), "Painted response uses every processing stage for " + cell.Range.Property);
                Equal(untouched, Json(Current(form)), "Response previews do not mutate runtime settings: " + cell.Range.Property);
                grid.CancelSlider();
                Check(CurveResponsePreview.Same(originalResponse, canvas.Settings), "Cancel restores every graph setting: " + cell.Range.Property);
            }
            var scale = (CurveSettingSliderCell)PropertyRow(form, "Scale").Cells[2];
            Check(scale.Value == null && PropertyText(form, "Scale") == "Gemischt", "Mixed settings do not display a fabricated shared slider value.");
            Profile before = Current(form), expected = KeyEditing.ApplyProperty(before, scope, "Scale", "0.91");
            SliderCall(grid, "BeginSlider", scale, false);
            for (int i = 1; i <= 91; i++) SliderCall(grid, "Preview", i / 100.0);
            Equal(Json(before), Json(Current(form)), "Ninety-one slider previews leave the profile/runtime configuration unchanged.");
            Check(PropertyText(form, "Scale") == "0.91", "Numeric readout follows the local slider draft.");
            SliderCall(grid, "FinishSlider"); Pump(form);
            Equal(Json(expected), Json(Current(form)), "One completed slider gesture updates all selected keys and preserves other keys/controllers.");
            Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "One undo restores the whole slider gesture, including its original mixed values.");

            scale = (CurveSettingSliderCell)PropertyRow(form, "Scale").Cells[2];
            SliderCall(grid, "BeginSlider", scale, false); SliderCall(grid, "Preview", .88); grid.CancelSlider();
            Equal(Json(before), Json(Current(form)), "Cancellation never writes a slider draft.");
            Check(scale.Value == null && PropertyText(form, "Scale") == "Gemischt", "Cancellation restores both the mixed slider and numeric placeholder.");
            grid.CurrentCell = scale;
            for (int i = 0; i < 6; i++) SliderCall(grid, "OnKeyDown", new KeyEventArgs(Keys.Right));
            Equal(Json(before), Json(Current(form)), "Keyboard auto-repeat remains a local slider gesture until release.");
            SliderCall(grid, "OnKeyUp", new KeyEventArgs(Keys.Right)); Pump(form);
            expected = KeyEditing.ApplyProperty(before, scope, "Scale", "0.36");
            Equal(Json(expected), Json(Current(form)), "Arrow-key release commits exactly the selected setting across the full selected-key scope.");
            Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "A repeated-key slider gesture also takes one undo.");
            CheckCurveNumericNavigation(form, scope);

            scale = (CurveSettingSliderCell)PropertyRow(form, "Scale").Cells[2];
            SliderCall(grid, "BeginSlider", scale, false); SliderCall(grid, "Preview", .77);
            SelectKeys(form, 21);
            Equal(Json(before), Json(Current(form)), "Changing keyboard selection discards an in-progress slider draft.");
            Check(!grid.SliderEditing, "A stale gesture cannot apply to the next key selection.");
            CheckCurveResponseViews(form, artifacts);
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null);
            Console.WriteLine("CURVE SLIDERS PASS: " + (assertions - started) + " assertions; shared bounds, local previews, atomic undo, mixed values and cancellation.");
        }
        static void CheckCurveNumericNavigation(MainForm form, string[] scope)
        {
            var grid = (CurveSettingsGrid)Field<DataGridView>(form, "settings");
            DataGridViewRow[] rows = grid.Rows.Cast<DataGridViewRow>().ToArray();
            var source = PropertyRow(form, "SmoothingTimeConstant").Cells[1];
            var destination = PropertyRow(form, "ButtonThreshold").Cells[1];
            grid.CurrentCell = source;
            grid.FirstDisplayedScrollingRowIndex = source.RowIndex;
            Pump(form);
            int firstVisible = grid.FirstDisplayedScrollingRowIndex;
            Check(firstVisible > 0, "Numeric navigation is tested in the scrolled curve-settings list.");
            int additions = 0, removals = 0, ended = 0;
            var errors = new List<Exception>();
            DataGridViewRowsAddedEventHandler added = delegate { additions++; };
            DataGridViewRowsRemovedEventHandler removed = delegate { removals++; };
            DataGridViewCellEventHandler editEnded = delegate(object sender, DataGridViewCellEventArgs e) { if (e.ColumnIndex == 1) ended++; };
            DataGridViewDataErrorEventHandler failed = delegate(object sender, DataGridViewDataErrorEventArgs e) { errors.Add(e.Exception); e.ThrowException = true; };
            grid.RowsAdded += added; grid.RowsRemoved += removed; grid.CellEndEdit += editEnded; grid.DataError += failed;
            Profile before = Current(form);
            try
            {
                Check(grid.BeginEdit(true), "The real DataGridView numeric editor begins an edit.");
                var editor = grid.EditingControl as TextBox;
                Check(editor != null && editor is IDataGridViewEditingControl, "The fixture uses the grid's genuine numeric text editing control.");
                editor.Text = "0.047";
                Rectangle target = grid.GetCellDisplayRectangle(destination.ColumnIndex, destination.RowIndex, false);
                Check(grid.ClientRectangle.Contains(target), "The next numeric cell is fully visible for the owned-handle click.");
                var point = (IntPtr)((target.Left + 6) | ((target.Top + target.Height / 2) << 16));
                // Real owned-window mouse messages end the edit inside the
                // framework's SetCurrentCellAddressCore transition. Calling
                // EditProperty directly would miss the reported reentrancy.
                ThemeSend(grid.Handle, 0x0201, (IntPtr)1, point);
                ThemeSend(grid.Handle, 0x0202, IntPtr.Zero, point);
                Pump(form);
                Profile expected = KeyEditing.ApplyProperty(before, scope, "SmoothingTimeConstant", "0.047");
                Equal(Json(expected), Json(Current(form)), "Clicking another numeric cell commits once to all selected keys and preserves unrelated settings.");
                Check(ended == 1 && errors.Count == 0, "The real edit/navigation transition finishes once without DataGridView errors.");
                Check(Object.ReferenceEquals(grid.CurrentCell, destination), "The clicked destination cell remains current after the profile refresh.");
                Check(grid.FirstDisplayedScrollingRowIndex == firstVisible, "Committing a numeric edit keeps the scrolled list position.");
                Check(additions == 0 && removals == 0 && rows.SequenceEqual(grid.Rows.Cast<DataGridViewRow>()),
                    "A numeric commit never replaces rows inside the grid's current-cell transition.");
                Call(form, "Undo"); Pump(form);
                Equal(Json(before), Json(Current(form)), "One undo restores the complete numeric edit made while navigating cells.");
                Check(Object.ReferenceEquals(grid.CurrentCell, destination) && grid.FirstDisplayedScrollingRowIndex == firstVisible,
                    "Undo retains the user's current numeric cell and scroll position.");
                Check(additions == 0 && removals == 0 && rows.SequenceEqual(grid.Rows.Cast<DataGridViewRow>()), "Undo refreshes existing curve rows in place.");
                grid.CurrentCell = source; Check(grid.BeginEdit(true), "A second real numeric edit can start after navigation and undo.");
                ((TextBox)grid.EditingControl).Text = "0.093"; grid.CancelEdit();
                Equal(Json(before), Json(Current(form)), "Canceling a numeric draft leaves the selected-key settings unchanged.");
                Check(grid.BeginEdit(true), "A numeric draft can start before changing keyboard selection.");
                ((TextBox)grid.EditingControl).Text = "0.083";
                SelectKeys(form, 21);
                Equal(Json(before), Json(Current(form)), "An unfinished numeric draft cannot apply to the newly selected key.");
                string targetValue = before.Bindings.Single(b => b.KeyIndex == 21).Processing.SmoothingTimeConstant.ToString(CultureInfo.InvariantCulture);
                string remainingEditor = grid.EditingControl == null ? "<none>" : grid.EditingControl.GetType().Name + ":" + grid.EditingControl.Text;
                Check(!grid.IsCurrentCellInEditMode, "Changing selected keys closes the canceled numeric draft even though rows are reused. " +
                    "[editor=" + remainingEditor + "; cell=" + source.Value + "; expected=" + targetValue + "; context=" + Field<string>(form, "curveSettingsContext") + "]");
                Equal(targetValue, Convert.ToString(source.Value, CultureInfo.InvariantCulture), "The reused numeric cell displays only the new selection's committed value.");
                Check(grid.BeginEdit(true), "The new key selection can immediately open its own numeric editor.");
                Equal(targetValue, grid.EditingControl.Text, "The next editor starts with the new key's value, not the canceled selection's value.");
                grid.EndEdit();
                Equal(Json(before), Json(Current(form)), "Leaving the new editor unchanged cannot leak either prior numeric draft into the new key.");
                SelectKeys(form, 9, 14); SelectBindings(form, scope[0]);
                Check(additions == 0 && removals == 0 && rows.SequenceEqual(grid.Rows.Cast<DataGridViewRow>()), "Keyboard selection changes also preserve the settings row instances.");
            }
            finally
            {
                grid.RowsAdded -= added; grid.RowsRemoved -= removed; grid.CellEndEdit -= editEnded; grid.DataError -= failed;
                if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
            }
        }
        static double[] CurveCanvasSamples(CurveCanvas canvas)
        { return (double[])typeof(CurveCanvas).GetMethod("CurveSamples", Private).Invoke(canvas, new object[] { canvas.Settings }); }
        static void CurveCanvasMouse(CurveCanvas canvas, string method, Point point, MouseButtons buttons)
        { typeof(CurveCanvas).GetMethod(method, Private).Invoke(canvas, new object[] { new MouseEventArgs(buttons, 1, point.X, point.Y, 0) }); }
        static Point CurveLocation(CurveCanvas canvas, double x, double y)
        {
            RectangleF plot = (RectangleF)typeof(CurveCanvas).GetProperty("Plot", Private).GetValue(canvas, null);
            return new Point((int)Math.Round(plot.Left + x * plot.Width), (int)Math.Round(plot.Bottom - y * plot.Height));
        }
        static void CheckCurveResponseViews(MainForm form, string artifacts)
        {
            Profile fixture = Current(form);
            string[] scope = fixture.Bindings.Where(b => (b.KeyIndex == 9 || b.KeyIndex == 14) && b.ControllerId == ControllerRouting.DefaultControllerId).Select(b => b.BindingId).ToArray();
            foreach (Binding binding in fixture.Bindings.Where(b => scope.Contains(b.BindingId)))
                binding.Processing = new SignalSettings { Curve = CurveKind.Bezier, TopDeadzone = .1, BottomDeadzone = .15, MinOutput = .12, MaxOutput = .85,
                    Hysteresis = .08, Scale = .9, OutputDeadzone = .04, SmoothingTimeConstant = .03,
                    CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.4, .3), new CurvePoint(1, 1) } };
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); SelectBindings(form, scope[0]); DetailMode(form, "advanced");
            var canvas = Field<CurveCanvas>(form, "curve"); canvas.ShapeEditing = false; canvas.ActiveSetting = "Hysteresis";
            var toggle = Field<Button>(canvas, "viewButton");
            Check(toggle.Visible && toggle.Enabled && toggle is SleekButton && canvas.ClientRectangle.Contains(toggle.Bounds), "The real styled Edit shape button remains visible inside the compact graph.");
            Profile before = Current(form); Point node = CurveLocation(canvas, .4, .3);
            CurveCanvasMouse(canvas, "OnMouseDown", node, MouseButtons.Left); CurveCanvasMouse(canvas, "OnMouseUp", node, MouseButtons.Left);
            Check(!canvas.IsEditing, "The response chart has no misleading draggable sample dots.");
            Equal(Json(before), Json(Current(form)), "Clicking an output guide never creates a curve point.");
            CapturePreview(form, artifacts, "curve-response-settings");
            toggle.PerformClick();
            Check(canvas.ShapeEditing && !canvas.IsEditing, "Edit shape explicitly switches to the real point-coordinate editor.");
            CurveCanvasMouse(canvas, "OnMouseDown", node, MouseButtons.Left);
            Check(canvas.IsEditing, "A visible interior Bézier point can be grabbed directly in the shape editor.");
            var moved = new Point(node.X + 7, node.Y - 8);
            CurveCanvasMouse(canvas, "OnMouseMove", moved, MouseButtons.Left);
            Equal(Json(before), Json(Current(form)), "Moving a visible curve point is still an uncommitted local gesture.");
            CapturePreview(form, artifacts, "curve-shape-edit");
            CurveCanvasMouse(canvas, "OnMouseUp", moved, MouseButtons.Left); Pump(form);
            Profile changed = Current(form), expected = before;
            Binding edited = changed.Bindings.Single(b => b.BindingId == scope[0]);
            Check(edited.Processing.CustomPoints[1].X != .4 && edited.Processing.CustomPoints[1].Y != .3, "The dragged point's two coordinates change with its visible position.");
            foreach (Binding binding in expected.Bindings.Where(b => scope.Contains(b.BindingId)))
            { binding.Processing.Curve = edited.Processing.Curve; binding.Processing.CustomPoints = edited.Processing.CustomPoints.Select(p => new CurvePoint(p.X, p.Y, p.Tangent)).ToList(); }
            Equal(Json(expected), Json(changed), "Real point dragging updates all selected mappings while preserving non-curve settings and unrelated outputs.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "One undo restores the entire real point gesture.");
            canvas.ShapeEditing = false;
        }
    }
}
