using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static string ShapeChoiceValue(object item)
        { return item == null ? null : (string)item.GetType().GetProperty("Value").GetValue(item, null); }

        static void CheckCurveShapePicker(MainForm form, string artifacts)
        {
            int started = assertions;
            Profile original = Current(form); string originalLanguage = UiText.Language;
            string controller = Field<MultiControllerSession>(form, "runtime").SelectedControllerId;
            Profile fixture = ControllerRouting.Add(original, "shape-other", "Other shape", ControllerKind.Xbox360);
            fixture.Bindings = new List<Binding> {
                new Binding { KeyIndex = 9, ControllerId = controller, Target = OutputTarget.LeftXNegative, Processing = new SignalSettings { Curve = CurveKind.Linear, TopDeadzone = .1, Scale = .6 } },
                new Binding { KeyIndex = 14, ControllerId = controller, Target = OutputTarget.LeftYPositive, Processing = new SignalSettings { Curve = CurveKind.Exponential, Exponent = 2.5, BottomDeadzone = .2 } },
                new Binding { KeyIndex = 21, ControllerId = controller, Target = OutputTarget.A },
                new Binding { KeyIndex = 9, ControllerId = "shape-other", Target = OutputTarget.B }
            };
            try
            {
                Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "advanced");
                var picker = Field<SleekComboBox>(form, "curveShape");
                Check(!Field<DataGridView>(form, "settings").Rows.Cast<DataGridViewRow>().Any(row => (string)row.Tag == "Curve"),
                    "Curve shape is not squeezed into the narrow numeric value column.");
                Check(!Field<ComboBox>(form, "preset").Visible && picker.Parent != null,
                    "One visible shape selector replaces the duplicate dropdown; complete presets remain in their menu.");
                Check(ShapeChoiceValue(picker.SelectedItem) == "Gemischt", "The full-width shape picker preserves mixed selected shapes until explicitly changed.");
                string[] scope = Current(form).Bindings.Where(binding => binding.ControllerId == controller && (binding.KeyIndex == 9 || binding.KeyIndex == 14)).Select(binding => binding.BindingId).ToArray();
                Profile before = Current(form);
                foreach (string language in new[] { "en", "de" })
                {
                    Call(form, "SwitchLanguage", language);
                    Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
                    foreach (Size size in new[] { DefaultClientSize, new Size(SupportedMinimumSize.Width - chrome.Width, SupportedMinimumSize.Height - chrome.Height) })
                    {
                        SetPreviewClientSize(form, size); DetailMode(form, "advanced");
                        VisibleInside(form, picker, "full-width shape picker/" + language + "/" + size);
                        Check(picker.Width >= 180, "The translated curve choices retain enough width at the minimum window size.");
                        foreach (object item in picker.Items)
                        {
                            string label = picker.GetItemText(item);
                            int width = TextRenderer.MeasureText(label, picker.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                            Check(width <= picker.ClientSize.Width - 36, "The selected shape can be read without truncation: " + label);
                        }
                        var graph = Field<CurveCanvas>(form, "curve"); var grid = Field<DataGridView>(form, "settings");
                        int availableWidth = graph.Parent.ClientSize.Width;
                        bool beside = availableWidth >= 380;
                        int curveWidth = beside ? availableWidth - 184 - 12 : availableWidth;
                        var thresholds = Field<Control>(form, "keyBehaviorPanel");
                        Check(graph.Width == graph.Height && graph.Width == curveWidth,
                            "The square curve fills all remaining width beside the vertical sliders, or the full width when stacked.");
                        Check(beside ? thresholds.Width == 184 && graph.Left == thresholds.Right + 12 : graph.Left == 0 && thresholds.Top == graph.Bottom + 8,
                            "The curve and compact threshold controls keep their intended horizontal or stacked separation.");
                        RectangleF plot = (RectangleF)typeof(CurveCanvas).GetProperty("Plot", Private).GetValue(graph, null);
                        var view = Field<Button>(graph, "viewButton");
                        Check(plot.Top - view.Bottom >= 17 && plot.Top - 6 - view.Bottom >= 11,
                            "The Response/Edit shape button has visible clearance above both the plot and the taller range handles.");
                        if (size == DefaultClientSize)
                            Check(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height * 4,
                                "At the normal window size at least four complete curve-setting rows fit below the plot.");
                    }
                    Equal(Json(before), Json(Current(form)), "Opening and translating the mixed picker changes no mapping.");
                }
                Call(form, "SwitchLanguage", "en"); SetPreviewClientSize(form, DefaultClientSize);
                picker.SelectedItem = picker.Items.Cast<object>().Single(item => ShapeChoiceValue(item) == "Bezier"); Pump(form);
                Profile converted = KeyEditing.ApplyProperty(before, scope, "Curve", "Bezier");
                foreach (Binding binding in converted.Bindings.Where(binding => scope.Contains(binding.BindingId)))
                    binding.Processing.CustomPoints = CurveBezierEditing.Create(before.Bindings.Single(source => source.BindingId == binding.BindingId).Processing);
                Equal(Json(converted), Json(Current(form)),
                    "Choosing Bezier preserves each selected mapping's previous shape and leaves other processing settings/controllers unchanged.");
                Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "One undo restores the whole mixed-shape selection.");
                picker.SelectedItem = picker.Items.Cast<object>().Single(item => ShapeChoiceValue(item) == "Smoothstep"); Pump(form);
                Check(ShapeChoiceValue(picker.SelectedItem) == "Smoothstep", "Refreshing after a real shape choice retains that choice.");
                CapturePreview(form, artifacts, "curve-shape-picker-en");
                CheckEditablePresetCurves(form, artifacts);
            }
            finally { Call(form, "Commit", original); Call(form, "SwitchLanguage", originalLanguage); SelectKeys(form, 14); DetailMode(form, null); }
            Console.WriteLine("CURVE SHAPE PICKER PASS: " + (assertions - started) + " assertions; full-width translated choices, scoped changes, undo and compact plot layout.");
        }
        static void CheckEditablePresetCurves(MainForm form, string artifacts)
        {
            Profile original = Current(form);
            string controller = Field<MultiControllerSession>(form, "runtime").SelectedControllerId;
            var presets = Field<ComboBox>(form, "preset"); object selectedPreset = presets.SelectedItem;
            try
            {
                foreach (string name in new[] { "Linear", "Soft", "Aggressiv", "Racing", "Präzise Bewegung" })
                {
                    presets.SelectedItem = name;
                    var presetValue = (SignalPreset)Call(form, "SelectedPreset");
                    string presetBefore = SignalPresetJson.Serialize(presetValue);
                    Profile fixture = ControllerRouting.Add(original, "bezier-other", "Unchanged Bezier", ControllerKind.Xbox360);
                    fixture.Bindings = new List<Binding> {
                        new Binding { KeyIndex = 9, ControllerId = controller, Target = OutputTarget.LeftTrigger },
                        new Binding { KeyIndex = 14, ControllerId = controller, Target = OutputTarget.RightTrigger },
                        new Binding { KeyIndex = 21, ControllerId = controller, Target = OutputTarget.A },
                        new Binding { KeyIndex = 9, ControllerId = "bezier-other", Target = OutputTarget.B }
                    };
                    string[] scope = fixture.Bindings.Take(2).Select(binding => binding.BindingId).ToArray();
                    fixture = ProfileEditing.ApplySettings(fixture, scope, presetValue.Settings);
                    fixture.Bindings[1].Processing.Scale = .73;
                    Call(form, "Commit", fixture); SelectKeys(form, 9, 14); SelectBindings(form, scope[0]); DetailMode(form, "advanced");
                    var canvas = Field<CurveCanvas>(form, "curve"); canvas.ShapeEditing = false;
                    var toggle = Field<Button>(canvas, "viewButton");
                    Profile before = Current(form); toggle.PerformClick(); Pump(form);
                    Check(canvas.ShapeEditing, "A preset can be opened in the actual shape editor: " + name);
                    Equal(Json(before), Json(Current(form)), "Opening a preset's editable shape does not change mappings or history: " + name);
                    Check(canvas.Settings.Curve == presetValue.Settings.Curve, "The saved response remains analytic until a real edit.");
                    var editable = (SignalSettings)typeof(CurveCanvas).GetMethod("EditableShape", Private).Invoke(canvas, null);
                    Check(editable.Curve == CurveKind.Bezier && editable.CustomPoints.Count >= 2,
                        "Every built-in preset exposes actual editable Bezier knots and handles: " + name);
                    Check(Field<int>(canvas, "selectedPoint") >= 0, "Shape view immediately displays handles for one selected knot.");
                    Point endpoint = CurveLocation(canvas, 0, 0);
                    CurveCanvasMouse(canvas, "OnMouseDown", endpoint, MouseButtons.Left); CurveCanvasMouse(canvas, "OnMouseUp", endpoint, MouseButtons.Left);
                    Equal(Json(before), Json(Current(form)), "Selecting an endpoint is a view action, not a preset conversion.");
                    CurvePoint handle = BezierCurve.GetHandle(editable.CustomPoints, 0, true);
                    Point pickup = CurveLocation(canvas, handle.X, handle.Y), moved = new Point(pickup.X, pickup.Y - 9);
                    CurveCanvasMouse(canvas, "OnMouseDown", pickup, MouseButtons.Left);
                    Check(canvas.IsEditing, "The fitted preset's visible handle can be grabbed: " + name);
                    CurveCanvasMouse(canvas, "OnMouseMove", moved, MouseButtons.Left);
                    Equal(Json(before), Json(Current(form)), "An edited preset remains a local preview until release.");
                    canvas.CancelEdit(); Equal(Json(before), Json(Current(form)), "Canceling leaves the preset mapping byte-for-byte unchanged.");
                    CurveCanvasMouse(canvas, "OnMouseDown", pickup, MouseButtons.Left); CurveCanvasMouse(canvas, "OnMouseMove", moved, MouseButtons.Left);
                    CurveCanvasMouse(canvas, "OnMouseUp", moved, MouseButtons.Left); Pump(form);
                    Profile actual = Current(form), expected = before;
                    SignalSettings edited = actual.Bindings.Single(binding => binding.BindingId == scope[0]).Processing;
                    Check(edited.Curve == CurveKind.Bezier && !CurvePointEditing.Same(edited.CustomPoints, editable.CustomPoints),
                        "The first actual handle edit becomes a custom Bezier based on the displayed preset.");
                    foreach (Binding binding in expected.Bindings.Where(binding => scope.Contains(binding.BindingId)))
                    { binding.Processing.Curve = CurveKind.Bezier; binding.Processing.CustomPoints = edited.CustomPoints.Select(point => new CurvePoint(point.X, point.Y, point.Tangent)).ToList(); }
                    Equal(Json(expected), Json(actual), "Preset editing reaches all selected mappings and preserves other processing settings/controllers.");
                    Equal(presetBefore, SignalPresetJson.Serialize((SignalPreset)Call(form, "SelectedPreset")), "Editing a preset instance never overwrites the preset definition.");
                    if (name == "Präzise Bewegung") CapturePreview(form, artifacts, "curve-editable-preset-en");
                    Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "One undo restores the original analytic preset on every selected mapping.");
                }
                CheckExtremeCurveFitGuard(form);
            }
            finally { presets.SelectedItem = selectedPreset; Call(form, "Commit", original); }
        }
        static void CheckExtremeCurveFitGuard(MainForm form)
        {
            Profile fixture = Current(form);
            fixture.Bindings[0].Processing.Curve = CurveKind.Exponential;
            fixture.Bindings[0].Processing.Exponent = .01;
            string[] scope = fixture.Bindings.Take(2).Select(binding => binding.BindingId).ToArray();
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); SelectBindings(form, scope[0]); DetailMode(form, "advanced");
            var canvas = Field<CurveCanvas>(form, "curve"); canvas.ShapeEditing = false;
            var toggle = Field<Button>(canvas, "viewButton");
            Check(!toggle.Enabled && Field<bool>(canvas, "shapeFitUnavailable"), "An inaccurate extreme fit disables only the shape-edit action.");
            Check(toggle.AccessibleDescription.IndexOf("curvature", StringComparison.OrdinalIgnoreCase) >= 0,
                "The disabled action explains how to make the shape editable.");
            string before = Json(Current(form));
            canvas.Settings = canvas.Settings; canvas.ShapeEditing = true; canvas.Refresh(); Pump(form);
            Check(!canvas.ShapeEditing && canvas.Settings.Exponent == .01, "A repeated refresh or direct edit-mode request cannot replace the extreme curve or throw.");
            double[] response = CurveCanvasSamples(canvas);
            double[] expected = CurveResponsePreview.Create(canvas.Settings, response.Length - 1).Press;
            Check(response.Length == expected.Length && response.Zip(expected, (actual, wanted) => Math.Abs(actual - wanted) < 1e-12).All(value => value),
                "The original effective response remains visible when an accurate editable fit is unavailable.");
            bool rejected = false;
            try { Call(form, "PrepareCurveShape", scope, "Bezier"); }
            catch (InvalidOperationException error) { rejected = error.InnerException != null && error.InnerException.Message.IndexOf("accurate", StringComparison.OrdinalIgnoreCase) >= 0; }
            Check(rejected, "An explicit dropdown conversion returns a clear localized error to the ordinary action handler.");
            Equal(before, Json(Current(form)), "Failure to fit one selected mapping leaves the complete multi-key selection unchanged.");
            fixture.Bindings[0].Processing.Exponent = 2;
            Call(form, "Commit", fixture); Pump(form);
            Check(toggle.Enabled && !Field<bool>(canvas, "shapeFitUnavailable"), "Reducing curvature invalidates the refusal cache and restores normal editing.");
        }
    }
}
