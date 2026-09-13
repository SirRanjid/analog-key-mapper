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
                        Check(graph.Width == graph.Height && graph.Width <= 232, "The compact curve keeps square bounds and leaves space for settings.");
                        if (size == DefaultClientSize)
                            Check(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height * 4,
                                "At the normal window size at least four complete curve-setting rows fit below the plot.");
                    }
                    Equal(Json(before), Json(Current(form)), "Opening and translating the mixed picker changes no mapping.");
                }
                Call(form, "SwitchLanguage", "en"); SetPreviewClientSize(form, DefaultClientSize);
                picker.SelectedItem = picker.Items.Cast<object>().Single(item => ShapeChoiceValue(item) == "Bezier"); Pump(form);
                Equal(Json(KeyEditing.ApplyProperty(before, scope, "Curve", "Bezier")), Json(Current(form)),
                    "The real full-width picker applies only the chosen shape to all selected keys on this controller.");
                Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "One undo restores the whole mixed-shape selection.");
                picker.SelectedItem = picker.Items.Cast<object>().Single(item => ShapeChoiceValue(item) == "Smoothstep"); Pump(form);
                Check(ShapeChoiceValue(picker.SelectedItem) == "Smoothstep", "Refreshing after a real shape choice retains that choice.");
                CapturePreview(form, artifacts, "curve-shape-picker-en");
            }
            finally { Call(form, "Commit", original); Call(form, "SwitchLanguage", originalLanguage); SelectKeys(form, 14); DetailMode(form, null); }
            Console.WriteLine("CURVE SHAPE PICKER PASS: " + (assertions - started) + " assertions; full-width translated choices, scoped changes, undo and compact plot layout.");
        }
    }
}
