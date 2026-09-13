using System;
using System.Collections.Generic;
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
        static void CheckCurveSettingsSliders(MainForm form)
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
            Check(grid.Columns.Count == 3 && grid.Columns[1].Name == "value" && grid.Columns[2].DisplayIndex == 1,
                "Every continuous setting has a slider beside the retained numeric editor.");
            Check(grid.Rows.Cast<DataGridViewRow>().Count(r => ((CurveSettingSliderCell)r.Cells[2]).Range != null) == 10,
                "All ten continuous curve-tab settings offer sliders.");
            foreach (DataGridViewRow row in grid.Rows)
            {
                Check(row.Cells.Cast<DataGridViewCell>().All(c => c.ToolTipText.Length > 180), "Every setting label, value and slider explains its effect and selection scope.");
                var cell = (CurveSettingSliderCell)row.Cells[2]; if (cell.Range == null) continue;
                foreach (double boundary in new[] { cell.Range.Minimum, cell.Range.Maximum })
                {
                    Profile bounded = KeyEditing.ApplyProperty(Current(form), scope, cell.Range.Property, boundary.ToString("R", CultureInfo.InvariantCulture));
                    Check(MappingValidation.ValidateProfile(bounded).Count == 0, "Slider limits preserve every selected mapping's dependent bounds: " + cell.Range.Property);
                }
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

            scale = (CurveSettingSliderCell)PropertyRow(form, "Scale").Cells[2];
            SliderCall(grid, "BeginSlider", scale, false); SliderCall(grid, "Preview", .77);
            SelectKeys(form, 21);
            Equal(Json(before), Json(Current(form)), "Changing keyboard selection discards an in-progress slider draft.");
            Check(!grid.SliderEditing, "A stale gesture cannot apply to the next key selection.");
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null);
            Console.WriteLine("CURVE SLIDERS PASS: " + (assertions - started) + " assertions; shared bounds, local previews, atomic undo, mixed values and cancellation.");
        }
    }
}
