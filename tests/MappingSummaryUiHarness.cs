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
        static DataGridViewRow MappingSummaryRow(MainForm form, string id)
        { return Field<DataGridView>(form, "bindings").Rows.Cast<DataGridViewRow>().Single(row => (string)row.Tag == id); }

        static void CheckMappingOutline(MainForm form, string id, string label, string artifacts)
        {
            var grid = Field<DataGridView>(form, "bindings");
            SelectBindings(form, id); Field<ComboBox>(form, "targets").Focus(); Pump(form);
            DataGridViewRow row = MappingSummaryRow(form, id);
            // Previous suites intentionally leave the minimum window size.
            // Programmatic selection does not scroll the second row into view;
            // inspect a fully displayed row, as a user would see before clicking.
            grid.FirstDisplayedScrollingRowIndex = row.Index; Pump(form);
            Rectangle area = grid.GetRowDisplayRectangle(row.Index, false);
            Check(area.Top >= grid.ColumnHeadersHeight && area.Bottom <= grid.ClientSize.Height,
                label + ": the row under pixel inspection is fully visible.");
            using (var selected = new Bitmap(grid.Width, grid.Height))
            using (var unselected = new Bitmap(grid.Width, grid.Height))
            {
                grid.DrawToBitmap(selected, grid.ClientRectangle);
                grid.ClearSelection(); Pump(form); grid.DrawToBitmap(unselected, grid.ClientRectangle);
                int horizontal = 0, vertical = 0;
                for (int x = Math.Max(2, area.Left + 2); x < Math.Min(grid.Width - 2, area.Right - 2); x++)
                {
                    Color a = selected.GetPixel(x, area.Top + 1), b = unselected.GetPixel(x, area.Top + 1);
                    if (a.R > 170 && a.B > 200 && a.R > b.R + 40) horizontal++;
                }
                for (int y = area.Top + 3; y < Math.Min(grid.Height - 2, area.Bottom - 3); y++)
                {
                    Color a = selected.GetPixel(area.Left + 1, y), b = unselected.GetPixel(area.Left + 1, y);
                    if (a.R > 170 && a.B > 200 && a.R > b.R + 40) vertical++;
                }
                selected.Save(System.IO.Path.Combine(artifacts, "summary-outline-" + label.Replace(' ', '-') + "-selected.png"), System.Drawing.Imaging.ImageFormat.Png);
                unselected.Save(System.IO.Path.Combine(artifacts, "summary-outline-" + label.Replace(' ', '-') + "-unselected.png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("Mapping outline " + label + ": row=" + area + ", grid=" + grid.ClientSize + ", selected horizontal=" + horizontal + ", vertical=" + vertical);
                Check(horizontal > area.Width / 2 && vertical > area.Height / 2, label + ": unfocused selected mapping has a visible perimeter, not only a changed fill.");
            }
            SelectBindings(form, id);
        }

        static void CheckMappingSummaries(MainForm form, string artifacts)
        {
            int started = assertions;
            Profile original = Current(form); string language = UiText.Language;
            int[] originalKeys = (int[])Call(form, "SelectedKeys"); string[] originalBindings = (string[])Call(form, "SelectedBindings");
            string originalMode = Field<string>(form, "detailsMode");
            Size originalSize = form.Size;
            var runtime = Field<MultiControllerSession>(form, "runtime"); string selectedController = runtime.SelectedControllerId;
            var standard = new Binding { BindingId = "summary-default", ControllerId = selectedController, KeyIndex = 14, Target = OutputTarget.A };
            var changed = new Binding { BindingId = "summary-custom", ControllerId = selectedController, KeyIndex = 14, Target = OutputTarget.B,
                Processing = new SignalSettings { Curve = CurveKind.Exponential, Exponent = 3, TopDeadzone = .04, BottomDeadzone = .02,
                    MinOutput = .12, MaxOutput = .88, Scale = .8, Hysteresis = .01, SmoothingTimeConstant = .016, ButtonThreshold = .65, OutputDeadzone = .03 } };
            Profile fixture = Current(form); fixture.Bindings = new List<Binding> { standard, changed };
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .18, PressMovement = .04, ReleaseMovement = .03, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.LastPressed },
                new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed }
            };
            try
            {
                Call(form, "Commit", fixture); SelectKeys(form, 14); DetailMode(form, null);
                var grid = Field<DataGridView>(form, "bindings");
                Check(grid.Columns[3].Name == "value" && grid.Columns[4].Name == "response", "The response summary preserves the hot live-value column index.");
                foreach (string requestedLanguage in new[] { "en", "de" })
                {
                    Call(form, "SwitchLanguage", requestedLanguage); SelectKeys(form, 14); DetailMode(form, null);
                    string baseline = Convert.ToString(MappingSummaryRow(form, standard.BindingId).Cells["response"].Value);
                    var cell = MappingSummaryRow(form, changed.BindingId).Cells["response"];
                    Equal("Linear", baseline, "An untouched output remains visibly distinct from custom response settings.");
                    Check(Convert.ToString(cell.Value).Contains("DZ 4% / 2%") && !Convert.ToString(cell.Value).Contains("Rapid"), "The output summary describes its own response without attributing physical RT to one output.");
                    foreach (string value in new[] { "3", "4% / 2%", "12%–88%", "16 ms", "65%", "3%" })
                        Check(cell.ToolTipText.Contains(value), "The complete output tooltip retains customization: " + value);
                    string keyText = Field<Label>(form, "selectionStatus").Text;
                    Check(keyText.Contains("Rapid Trigger") && keyText.Contains("18%") && keyText.Contains("SOCD"), "Physical RT, actuation and SOCD remain a separate shared-key summary.");
                    string keyTip = Field<ToolTip>(form, "keyCardTips").GetToolTip(Field<Label>(form, "selectionStatus"));
                    Check(keyTip.Contains(requestedLanguage == "en" ? "all mapped outputs" : "alle zugeordneten Ausgänge"), "Physical behavior explicitly names its shared scope.");
                    Check(grid.Columns["response"].HeaderText == (requestedLanguage == "en" ? "Response" : "Reaktion"), "Response headers follow the current language.");
                    object sameText = cell.Value;
                    for (int i = 0; i < 10; i++) Call(form, "UpdateKeyCard");
                    Check(Object.ReferenceEquals(sameText, cell.Value), "Unchanged live-card refreshes reuse summary text instead of rebuilding it.");
                    Call(form, "RefreshBindings");
                    Check(Convert.ToString(MappingSummaryRow(form, changed.BindingId).Cells["response"].Value).Contains("DZ"), "Rebuilding rows with unchanged history still fills their summaries.");
                    CheckMappingOutline(form, changed.BindingId, requestedLanguage + " multiple rows", artifacts);
                    CapturePreview(form, artifacts, "mapping-summary-" + requestedLanguage);
                }
                SelectBindings(form, changed.BindingId); Edit(form, "TopDeadzone", "0.07");
                Check(Convert.ToString(MappingSummaryRow(form, changed.BindingId).Cells["response"].Value).Contains("7%"), "Actual property editing refreshes the corresponding mapping summary.");
                Call(form, "Undo"); Pump(form);
                Check(Convert.ToString(MappingSummaryRow(form, changed.BindingId).Cells["response"].Value).Contains("4%"), "Undo restores the prior summary without changing the physical key behavior.");
                DetailMode(form, "input"); Field<NumericUpDown>(form, "actuationPoint").Value = 22; DetailMode(form, null);
                Check(Field<Label>(form, "selectionStatus").Text.Contains("22%"), "Leaving the behavior editor commits its draft and immediately refreshes the shared-key summary.");
                Call(form, "Undo"); Pump(form);

                Profile sole = Current(form); sole.Bindings.RemoveAll(binding => binding.BindingId != changed.BindingId);
                Call(form, "Commit", sole); SelectKeys(form, 14); DetailMode(form, null);
                Check(grid.Rows.Count == 1 && grid.Rows[0].Selected, "A sole mapping remains selected.");
                CheckMappingOutline(form, changed.BindingId, "sole row", artifacts);
                Rectangle keyboardBounds = Relative(form, Field<VisualKeyboard>(form, "keyboard"));
                Call(form, "UpdateKeyCard"); Pump(form);
                Check(Relative(form, Field<VisualKeyboard>(form, "keyboard")) == keyboardBounds, "Summary refresh never resizes the keyboard.");
                form.Size = form.MinimumSize; Pump(form);
                Check(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height, "The minimum window retains one complete mapping row beneath the header.");
                Check(grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width) <= grid.ClientSize.Width,
                    "Summary columns fit the minimum sidebar without a horizontal scrollbar.");
                CapturePreview(form, artifacts, "mapping-summary-minimum");
                AssertPassive(form);
            }
            finally
            {
                form.Size = originalSize; runtime.SelectedControllerId = selectedController;
                Call(form, "Commit", original); Call(form, "SwitchLanguage", language); SelectKeys(form, originalKeys); SelectBindings(form, originalBindings); DetailMode(form, originalMode);
            }
            Equal(Json(original), Json(Current(form)), "Mapping summary checks preserve the original synthetic profile.");
            Console.WriteLine("MAPPING SUMMARY PASS: " + (assertions - started) + " assertions; output/key scope, localization, live cache, edits/undo, sole selection outline and minimum layout.");
        }
    }
}
