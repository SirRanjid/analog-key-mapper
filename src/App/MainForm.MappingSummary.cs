using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        static readonly Color MappingSelectionOutline = Color.FromArgb(207, 220, 255);
        object mappingSummarySnapshot;
        string mappingSummaryController, mappingSummaryLanguage;

        void InitializeMappingSummaryGrid()
        {
            // Append rather than reorder the live-value cell (index 3).
            var column = new DataGridViewTextBoxColumn { Name = "response", HeaderText = Tr("Reaktion", "Response"),
                MinimumWidth = 80, FillWeight = 95, SortMode = DataGridViewColumnSortMode.NotSortable };
            bindings.Columns.Add(column); column.DisplayIndex = 2;
            bindings.Columns[0].MinimumWidth = 30; bindings.Columns[0].FillWeight = 28;
            bindings.Columns[1].MinimumWidth = 60; bindings.Columns[1].FillWeight = 70;
            bindings.Columns[1].HeaderText = Tr("Ziel", "Target");
            foreach (DataGridViewColumn item in bindings.Columns) item.HeaderCell.Style.Padding = new Padding(4, 5, 4, 5);
            // Compact padding keeps the extra information usable in the narrow
            // sidebar; the existing card and minimum row height remain intact.
            bindings.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) {
                e.CellStyle.Padding = new Padding(4, 4, 4, 4);
            };
            bindings.RowPostPaint += PaintMappingSelection;
            bindings.SelectionChanged += delegate { bindings.Invalidate(); };
        }

        void PaintMappingSelection(object sender, DataGridViewRowPostPaintEventArgs e)
        {
            if (e.RowIndex < 0 || !bindings.Rows[e.RowIndex].Selected) return;
            // Every selected row has its own persistent outline, including a
            // sole row and selection whose focus moved to another control.
            Rectangle bounds = Rectangle.Intersect(e.RowBounds, bindings.ClientRectangle);
            bounds.Inflate(-1, -1);
            if (bounds.Width <= 1 || bounds.Height <= 1) return;
            using (var outline = new Pen(MappingSelectionOutline, 2))
                e.Graphics.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        static string SummaryNumber(double value)
        { return value.ToString("0.##", UiText.Language == "de" ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture); }
        static string SummaryPercent(double value) { return SummaryNumber(value * 100) + "%"; }
        static string CurveSummaryName(CurveKind curve)
        {
            switch (curve)
            {
                case CurveKind.Exponential: return Tr("Exponentiell", "Exponential");
                case CurveKind.Logarithmic: return Tr("Logarithmisch", "Logarithmic");
                case CurveKind.Smoothstep: return Tr("S-Kurve", "S-curve");
                case CurveKind.Custom: return Tr("Eigene Kurve", "Custom curve");
                case CurveKind.Bezier: return "Bézier";
                default: return "Linear";
            }
        }
        sealed class MappingResponseSummary
        {
            internal string Brief, Detail;
            internal bool Customized;
        }
        static MappingResponseSummary DescribeMappingResponse(SignalSettings value, OutputTarget target)
        {
            value = value ?? new SignalSettings();
            var defaults = new SignalSettings();
            var details = new List<string>();
            var brief = new List<string>();
            string curve = CurveSummaryName(value.Curve);
            if (value.Curve != CurveKind.Linear) { details.Add(Tr("Kurve: ", "Curve: ") + curve); brief.Add(curve); }
            if (value.Exponent != defaults.Exponent || value.Curve == CurveKind.Exponential || value.Curve == CurveKind.Logarithmic)
                details.Add(Tr("Exponent: ", "Exponent: ") + SummaryNumber(value.Exponent) +
                    (value.Curve == CurveKind.Exponential || value.Curve == CurveKind.Logarithmic ? "" : Tr(" (gespeichert; hier inaktiv)", " (stored; inactive here)")));
            if (!CurvePointEditing.Same(value.CustomPoints, defaults.CustomPoints) || value.Curve == CurveKind.Custom || value.Curve == CurveKind.Bezier)
                details.Add(Tr("Kurvenpunkte: ", "Curve points: ") + string.Join("; ", value.CustomPoints.Select(p => "(" + SummaryNumber(p.X) + ", " + SummaryNumber(p.Y) + ")" + (p.Tangent.HasValue ? Tr(" Steigung ", " slope ") + SummaryNumber(p.Tangent.Value) : "")).ToArray()) +
                    (value.Curve == CurveKind.Custom || value.Curve == CurveKind.Bezier ? "" : Tr(" (gespeichert; hier inaktiv)", " (stored; inactive here)")));
            if (value.TopDeadzone != 0 || value.BottomDeadzone != 0)
            {
                string deadzones = SummaryPercent(value.TopDeadzone) + " / " + SummaryPercent(value.BottomDeadzone);
                brief.Add("DZ " + deadzones); details.Add(Tr("Deadzones oben / unten: ", "Top / bottom deadzones: ") + deadzones);
            }
            if (value.MinOutput != 0 || value.MaxOutput != 1)
            {
                string range = SummaryPercent(value.MinOutput) + "–" + SummaryPercent(value.MaxOutput);
                brief.Add(range); details.Add(Tr("Ausgabebereich: ", "Output range: ") + range);
            }
            if (value.Scale != 1) { brief.Add("×" + SummaryNumber(value.Scale)); details.Add(Tr("Skalierung: ", "Scale: ") + SummaryNumber(value.Scale)); }
            if (value.Hysteresis != 0) { brief.Add(Tr("Hysterese ", "Hysteresis ") + SummaryPercent(value.Hysteresis)); details.Add(Tr("Hysterese: ", "Hysteresis: ") + SummaryPercent(value.Hysteresis)); }
            if (value.SmoothingTimeConstant != 0) { brief.Add(SummaryNumber(value.SmoothingTimeConstant * 1000) + " ms"); details.Add(Tr("Glättung: ", "Smoothing: ") + SummaryNumber(value.SmoothingTimeConstant * 1000) + " ms"); }
            if (value.OutputDeadzone != 0) { brief.Add(Tr("Ausgabe-DZ ", "Output DZ ") + SummaryPercent(value.OutputDeadzone)); details.Add(Tr("Ausgabe-Deadzone: ", "Output deadzone: ") + SummaryPercent(value.OutputDeadzone)); }
            if (value.ButtonThreshold != defaults.ButtonThreshold)
            {
                bool button = (int)target >= 10;
                brief.Add(Tr("Schwelle ", "Threshold ") + SummaryPercent(value.ButtonThreshold));
                details.Add(Tr("Tastenschwelle: ", "Button threshold: ") + SummaryPercent(value.ButtonThreshold) +
                    (button ? "" : Tr(" (gespeichert; nur für Buttons)", " (stored; buttons only)")));
            }
            if (details.Count == 0) return new MappingResponseSummary { Brief = "Linear", Detail = Tr("Ausgang: Standard · lineare Reaktion", "Output: default · linear response") };
            if (brief.Count == 0) brief.Add(Tr("Gespeichert angepasst", "Stored customization"));
            return new MappingResponseSummary { Customized = true, Brief = string.Join(" · ", brief.ToArray()),
                Detail = Tr("Einstellungen dieses Ausgangs", "Settings for this output") + "\n" + string.Join("\n", details.ToArray()) };
        }

        string DescribePhysicalBehavior(KeyInputSettings input)
        {
            if (input == null) return Tr("Kontinuierlicher Druck", "Continuous pressure");
            var parts = new List<string> { input.RapidTriggerEnabled ? "Rapid Trigger" : Tr("Fester Auslösepunkt", "Fixed actuation"),
                Tr("Auslösung ", "Actuation ") + SummaryPercent(input.ActuationPoint) };
            if (input.RapidTriggerEnabled)
            {
                parts.Add(Tr("Loslassen ", "Release ") + SummaryPercent(input.ReleaseMovement));
                parts.Add(Tr("Erneut drücken ", "Repress ") + SummaryPercent(input.PressMovement));
            }
            if (input.OppositeKeyIndex.HasValue)
            {
                string policy = input.OppositePolicy == InputOpposedPolicy.LastPressed ? Tr("letzte Taste", "last pressed") :
                    input.OppositePolicy == InputOpposedPolicy.FirstPressed ? Tr("erste Taste", "first pressed") : Tr("neutral", "neutral");
                parts.Add("SOCD ↔ " + Label(input.OppositeKeyIndex.Value) + " · " + policy);
            }
            return string.Join(" · ", parts.ToArray());
        }

        void RefreshMappingSummaries(bool force)
        {
            if (!bindings.Columns.Contains("response")) return;
            object token = history.SnapshotToken;
            string controller = runtime.SelectedControllerId, language = UiText.Language;
            if (!force && Object.ReferenceEquals(mappingSummarySnapshot, token) && mappingSummaryController == controller && mappingSummaryLanguage == language) return;
            Profile profile = UiReadProfile;
            var configured = CurrentControllerBindings(profile).ToDictionary(b => b.BindingId, StringComparer.Ordinal);
            bindings.Columns["response"].HeaderText = Tr("Reaktion", "Response");
            bindings.Columns["response"].ToolTipText = Tr("Kurve und abweichende Einstellungen je Ausgang. Details beim Darüberfahren.", "Curve and customized settings per output. Hover for details.");
            foreach (DataGridViewRow row in bindings.Rows)
            {
                Tk75.Mapping.Binding binding;
                if (row.Tag == null || !configured.TryGetValue((string)row.Tag, out binding)) continue;
                MappingResponseSummary summary = DescribeMappingResponse(binding.Processing, binding.Target);
                DataGridViewCell cell = row.Cells["response"];
                cell.Value = summary.Brief; cell.ToolTipText = summary.Detail;
                cell.Style.ForeColor = summary.Customized ? Color.FromArgb(193, 179, 255) : ModernTheme.Muted;
                row.Cells["target"].ToolTipText = TargetLabel(binding.Target) + "\n" + summary.Detail;
            }
            int[] selected = SelectedKeys();
            var physical = profile.Inputs.ToDictionary(input => input.KeyIndex);
            var descriptions = new List<string>(); var explained = new List<string>();
            foreach (int index in selected)
            {
                KeyInputSettings input; physical.TryGetValue(index, out input);
                string description = DescribePhysicalBehavior(input); descriptions.Add(description); explained.Add(Label(index) + ": " + description);
            }
            selectionStatus.Text = selected.Length == 0 ? Tr("Keine Taste ausgewählt", "No key selected") :
                (selected.Length == 1 ? Tr("Taste: ", "Key: ") : Tr("Tasten: ", "Keys: ")) +
                (descriptions.Distinct().Count() == 1 ? descriptions[0] : Tr("Unterschiedliches Verhalten", "Mixed behavior"));
            keyCardTips.SetToolTip(selectionStatus, Tr("Physisches Tastenverhalten · gilt für alle zugeordneten Ausgänge", "Physical key behavior · applies to all mapped outputs") + "\n" + string.Join("\n", explained.ToArray()));
            mappingSummarySnapshot = token; mappingSummaryController = controller; mappingSummaryLanguage = language;
        }
    }
}
