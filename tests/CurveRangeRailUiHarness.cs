using System;
using System.Collections;
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
        static object RailCall(CurveRangeRail rail, string method, params object[] arguments)
        { return typeof(CurveRangeRail).GetMethod(method, Private).Invoke(rail, arguments); }
        static Point RailPoint(CurveRangeRail rail, double value)
        { return new Point(rail.Width / 2, (int)Math.Round((float)RailCall(rail, "Position", value))); }
        static void RailMouse(CurveRangeRail rail, string method, Point point)
        { RailCall(rail, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); }
        static void RailKey(CurveRangeRail rail, string method, Keys key)
        { RailCall(rail, method, new KeyEventArgs(key)); }
        static string RailProperty(bool input, int handle)
        { return input ? handle == 0 ? "TopDeadzone" : "BottomDeadzone" : handle == 0 ? "MinOutput" : "MaxOutput"; }
        static double RailPropertyNumber(bool input, int handle, double value) { return input && handle == 1 ? 1 - value : value; }
        static string RuntimeProfiles(MainForm form)
        {
            var values = new List<string>(); var runtime = Field<MultiControllerSession>(form, "runtime");
            foreach (DictionaryEntry entry in Field<IDictionary>(runtime, "slots"))
            {
                object session = entry.Value.GetType().GetField("Session").GetValue(entry.Value);
                MappingSession inner = Field<MappingSession>(session, "inner");
                values.Add(entry.Key + "\n" + Json(Field<Profile>(inner, "profile")));
            }
            return string.Join("\n", values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
        static void RunCurveRangeRailUi(MainForm form, string artifacts)
        {
            int started = assertions; Profile original = Current(form);
            Size originalSize = form.ClientSize;
            Profile fixture = ControllerRouting.Add(original, "rail-ui-other", "Unselected controller", ControllerKind.Xbox360);
            fixture.Bindings = new List<Binding> {
                new Binding { KeyIndex = 9, Target = OutputTarget.LeftXNegative, Processing = new SignalSettings { MinOutput = .15, MaxOutput = .85, TopDeadzone = .08, BottomDeadzone = .14, Hysteresis = .04 } },
                new Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive, Processing = new SignalSettings { MinOutput = .25, MaxOutput = .75, TopDeadzone = .12, BottomDeadzone = .08, Hysteresis = .06 } },
                new Binding { KeyIndex = 21, Target = OutputTarget.A },
                new Binding { KeyIndex = 9, ControllerId = "rail-ui-other", Target = OutputTarget.B }
            };
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "advanced");
            string[] scope = Current(form).Bindings.Where(binding => (binding.KeyIndex == 9 || binding.KeyIndex == 14) && binding.ControllerId == ControllerRouting.DefaultControllerId).Select(binding => binding.BindingId).ToArray();
            SelectBindings(form, scope[0]);
            var canvas = Field<CurveCanvas>(form, "curve");
            var input = Field<CurveRangeRail>(form, "curveInputRange"); var output = Field<CurveRangeRail>(form, "curveOutputRange");
            Check(input.Parent == canvas && output.Parent == canvas && !input.Reversed && output.Reversed,
                "Input and output rails use distinct pressure/output directions beside the same square plot.");
            foreach (var rail in new[] { input, output })
            {
                bool isInput = rail == input;
                Check(rail.AccessibleRole == AccessibleRole.Slider && rail.AccessibleDescription.Contains("Esc"), "Range rails explain their completed-gesture controls.");
                for (int handle = 0; handle < 2; handle++)
                {
                    string property = RailProperty(isInput, handle);
                    Profile before = Current(form); string runtimeBefore = RuntimeProfiles(form);
                    Check(rail.MixedAt(handle), "The two selected keys initially retain a mixed " + property + " value.");
                    Point thumb = RailPoint(rail, rail.ValueAt(handle));
                    RailMouse(rail, "OnMouseDown", thumb); RailMouse(rail, "OnMouseUp", thumb); Pump(form);
                    Equal(Json(before), Json(Current(form)), "A click without motion on a mixed handle leaves every mapping unchanged: " + property);
                    Check(rail.MixedAt(handle), "A simple click cannot collapse mixed values: " + property);

                    int commits = 0; Action<int, double> counted = delegate { commits++; }; rail.Committed += counted;
                    double target = isInput ? handle == 0 ? .26 : .70 : handle == 0 ? .36 : .64;
                    RailMouse(rail, "OnMouseDown", thumb); Point moved = RailPoint(rail, target); RailMouse(rail, "OnMouseMove", moved);
                    Check(rail.IsEditing && rail.ActiveHandle == handle, "Pointer pickup selects the actual visible handle: " + property);
                    double value = rail.ValueAt(handle), propertyValue = RailPropertyNumber(isInput, handle, value);
                    Check(Math.Abs(value - target) <= 1.1 / Math.Max(1, rail.Height - 12) + .001, "Visible pointer movement sets the expected rail position: " + property);
                    Check(Math.Abs((double)typeof(SignalSettings).GetField(property).GetValue(canvas.Settings) - propertyValue) < 1e-10,
                        "Range movement updates the matching full-response setting: " + property);
                    Equal(Json(before), Json(Current(form)), "Range mouse drafts do not alter profile state: " + property);
                    Equal(runtimeBefore, RuntimeProfiles(form), "Range mouse drafts do not reconfigure any runtime route: " + property);
                    Profile expected = KeyEditing.ApplyProperty(before, scope, property, propertyValue.ToString("R", CultureInfo.InvariantCulture));
                    RailMouse(rail, "OnMouseUp", moved); Pump(form); rail.Committed -= counted;
                    Check(commits == 1 && !rail.IsEditing, "One mouse gesture emits exactly one completed edit: " + property);
                    Equal(Json(expected), Json(Current(form)), "The completed range changes both selected keys and preserves other keys/controllers: " + property);
                    Point exactThumb = RailPoint(rail, rail.ValueAt(handle));
                    RailMouse(rail, "OnMouseDown", exactThumb); RailMouse(rail, "OnMouseUp", exactThumb); Pump(form);
                    Equal(Json(expected), Json(Current(form)), "A click on a non-mixed fractional thumb does not round or move it: " + property);
                    Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "A range mouse gesture and subsequent no-op click take one undo: " + property);

                    rail.Focus(); RailKey(rail, "OnKeyDown", handle == 0 ? Keys.Left : Keys.Right);
                    double originalValue = rail.ValueAt(handle);
                    Keys direction = handle == 0 ? rail.Reversed ? Keys.Up : Keys.Down : rail.Reversed ? Keys.Down : Keys.Up;
                    for (int repeat = 0; repeat < 5; repeat++) RailKey(rail, "OnKeyDown", direction);
                    Check(Math.Abs(rail.ValueAt(handle) - (originalValue + (handle == 0 ? .005 : -.005))) < 1e-10,
                        "Arrow keys move the selected handle in its documented physical direction: " + property);
                    Equal(Json(before), Json(Current(form)), "Keyboard repeat remains a local gesture until key release: " + property);
                    Equal(runtimeBefore, RuntimeProfiles(form), "Keyboard range drafts leave runtime configuration unchanged: " + property);
                    expected = KeyEditing.ApplyProperty(before, scope, property, RailPropertyNumber(isInput, handle, rail.ValueAt(handle)).ToString("R", CultureInfo.InvariantCulture));
                    RailKey(rail, "OnKeyUp", direction); Pump(form);
                    Equal(Json(expected), Json(Current(form)), "Keyboard release commits the selected range field for the complete scope: " + property);
                    Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "One undo restores repeated keyboard range movement: " + property);

                    thumb = RailPoint(rail, rail.ValueAt(handle)); RailMouse(rail, "OnMouseDown", thumb); RailMouse(rail, "OnMouseMove", moved);
                    RailKey(rail, "OnKeyDown", Keys.Escape);
                    Check(!rail.IsEditing && rail.MixedAt(handle) && Math.Abs(rail.ValueAt(handle) - originalValue) < 1e-10,
                        "Escape restores the exact original mixed range handle: " + property);
                    Equal(Json(before), Json(Current(form)), "Escape never applies a range draft: " + property);
                    foreach (Keys boundary in new[] { Keys.Home, Keys.End })
                    {
                        RailKey(rail, "OnKeyDown", handle == 0 ? Keys.Left : Keys.Right); RailKey(rail, "OnKeyDown", boundary);
                        expected = KeyEditing.ApplyProperty(before, scope, property, RailPropertyNumber(isInput, handle, rail.ValueAt(handle)).ToString("R", CultureInfo.InvariantCulture));
                        Check(MappingValidation.ValidateProfile(expected).Count == 0, "Shared rail bounds preserve all output/deadzone/hysteresis constraints: " + property + "/" + boundary);
                        RailKey(rail, "OnKeyUp", boundary); Pump(form); Equal(Json(expected), Json(Current(form)), "Boundary key applies a valid shared range: " + property);
                        Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "Boundary edit remains one undo: " + property);
                    }
                }
            }
            Profile selectionBefore = Current(form); Point selectedThumb = RailPoint(input, input.ValueAt(0));
            RailMouse(input, "OnMouseDown", selectedThumb); RailMouse(input, "OnMouseMove", RailPoint(input, .3)); SelectKeys(form, 21);
            Check(!input.IsEditing, "Changing selected keys cancels the old range gesture.");
            RailMouse(input, "OnMouseUp", RailPoint(input, .3)); Equal(Json(selectionBefore), Json(Current(form)), "A stale mouse release cannot write the old range into the next key.");
            SelectKeys(form, 9, 14); SelectBindings(form, scope[0]); DetailMode(form, "advanced");
            foreach (Size size in new[] { DefaultClientSize, new Size(SupportedMinimumSize.Width - (form.Width - form.ClientSize.Width), SupportedMinimumSize.Height - (form.Height - form.ClientSize.Height)) })
            {
                SetPreviewClientSize(form, size); Pump(form);
                // The narrow layout deliberately stacks the plot, thresholds
                // and grid. Test that users can reach the plot after working
                // below it, not that every section fits in one viewport.
                var numericSettings = Field<DataGridView>(form, "settings");
                RevealCurveSetting(form, numericSettings); numericSettings.Focus(); Pump(form);
                VisibleInside(form, numericSettings, "range-rail/reachable-numeric-settings/" + size);
                if (size != DefaultClientSize)
                    Check(Field<Panel>(form, "curveEditorScroll").AutoScrollPosition.Y < 0,
                        "The minimum fixture actually visits lower settings before returning to the range handles.");
                RevealCurveSetting(form, canvas);
                Check(input.Focus(), "A range handle accepts keyboard focus after returning from lower settings."); Pump(form);
                VisibleInside(form, canvas, "range-rail/reachable-whole-curve/" + size);
                RectangleF plot = (RectangleF)typeof(CurveCanvas).GetProperty("Plot", Private).GetValue(canvas, null);
                Check(canvas.Width == canvas.Height && Math.Abs(plot.Width - plot.Height) < .01, "The response control and plot remain square with both rails at " + size);
                foreach (var rail in new[] { input, output })
                {
                    VisibleInside(form, rail, "range-rail/" + rail.AccessibleName + "/" + size);
                    Check(canvas.ClientRectangle.Contains(rail.Bounds), "Each rail fits inside the existing graph margins.");
                    for (int handle = 0; handle < 2; handle++)
                    {
                        Point point = RailPoint(rail, rail.ValueAt(handle));
                        Check(rail.ClientRectangle.Contains(new Rectangle(point.X - 6, point.Y - 3, 13, 7)), "Every visible range grip is fully contained and can be picked up.");
                    }
                }
                // Native focus scrolling must reveal the entire graph without
                // a preceding manual reveal, including its title/mode button.
                foreach (Control focusTarget in new Control[] { output, Field<Button>(canvas, "viewButton"), input })
                {
                    RevealCurveSetting(form, numericSettings); numericSettings.Focus(); Pump(form);
                    Check(focusTarget.Focus(), "A graph child accepts focus directly from the numeric settings."); Pump(form);
                    VisibleInside(form, canvas, "range-rail/focus-reveals-whole-curve/" + focusTarget.AccessibleName + "/" + size);
                    VisibleInside(form, focusTarget, "range-rail/focused-child/" + focusTarget.AccessibleName + "/" + size);
                }
                CapturePreview(form, artifacts, "curve-range-rails-" + (size == DefaultClientSize ? "normal" : "minimum"));
            }
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null); SetPreviewClientSize(form, originalSize); Pump(form);
            Console.WriteLine("CURVE RANGE RAILS PASS: " + (assertions - started) + " assertions; real mouse/keyboard handles, shared bounds, runtime isolation, mixed cancellation and atomic undo.");
        }
    }
}
