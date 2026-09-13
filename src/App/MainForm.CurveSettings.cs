using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        string[] curveSliderBindings;
        string curveSliderContext, curveSliderOriginalProfile;
        SignalSettings curveSliderSource;
        bool curveSliderMixed;

        static DataGridView CreateCurveSettingsGrid()
        {
            var grid = new CurveSettingsGrid { Dock = DockStyle.Fill, BackgroundColor = ModernTheme.Surface, BorderStyle = BorderStyle.None,
                RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false };
            return grid;
        }
        string CurveSelectionContext(string[] ids)
        { return (profilePath ?? "") + "\n" + runtime.SelectedControllerId + "\n" + string.Join("\n", ids.OrderBy(id => id, StringComparer.Ordinal).ToArray()); }
        void BuildCurveSettingsSliders()
        {
            settings.Columns.Add(new DataGridViewColumn(new CurveSettingSliderCell()) { Name = "slider", HeaderText = Tr("Regler", "Adjust"), ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 48, MinimumWidth = 88, SortMode = DataGridViewColumnSortMode.NotSortable });
            settings.Columns[2].DisplayIndex = 1;
            settings.Columns[0].FillWeight = 52; settings.Columns[0].MinimumWidth = 112;
            settings.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.None; settings.Columns[1].Width = 78; settings.Columns[1].MinimumWidth = 65;
            foreach (DataGridViewColumn column in settings.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            settings.AllowUserToResizeRows = false;
            var grid = (CurveSettingsGrid)settings;
            grid.SliderStarted += BeginCurveSettingSlider;
            grid.SliderPreviewed += PreviewCurveSettingSlider;
            grid.SliderCommitted += delegate(string property, double value) { Attempt(delegate { CompleteCurveSettingSlider(property, value); }); };
            grid.SliderCanceled += RestoreCurveSettingSlider;
        }
        void BeginCurveSettingSlider(string property)
        {
            curveSliderBindings = SelectedSettingsBindings(); var profile = history.Current;
            var chosen = profile.Bindings.Where(b => curveSliderBindings.Contains(b.BindingId)).ToArray();
            if (chosen.Length == 0) { ((CurveSettingsGrid)settings).CancelSlider(); return; }
            curveSliderContext = CurveSelectionContext(curveSliderBindings);
            curveSliderOriginalProfile = ProfileJson.Serialize(profile); curveSliderSource = chosen[0].Processing;
            curveSliderMixed = chosen.Skip(1).Any(b => !SameCurve(chosen[0].Processing, b.Processing));
        }
        void PreviewCurveSettingSlider(string property, double value)
        {
            if (curveSliderSource == null) return;
            foreach (DataGridViewRow row in settings.Rows)
                if ((string)row.Tag == property) row.Cells[1].Value = value.ToString("0.##########", CultureInfo.InvariantCulture);
            // The plot shows the curve stage. Deadzones, range and time-dependent
            // filters remain separate stages and do not move its editable nodes.
            if (property == "Exponent") curve.UpdateCurve(new SignalSettings { Curve = curveSliderSource.Curve, Exponent = value,
                CustomPoints = curveSliderSource.CustomPoints }, curveSliderMixed, curveSliderContext);
        }
        void CompleteCurveSettingSlider(string property, double value)
        {
            string[] ids = curveSliderBindings;
            bool current = ids != null && CurveSelectionContext(SelectedSettingsBindings()) == curveSliderContext &&
                ProfileJson.Serialize(history.Current) == curveSliderOriginalProfile;
            ClearCurveSliderDraft();
            try { if (current) Commit(KeyEditing.ApplyProperty(history.Current, ids, property, value.ToString("R", CultureInfo.InvariantCulture))); }
            finally { RefreshSettings(); }
        }
        void ClearCurveSliderDraft()
        { curveSliderBindings = null; curveSliderSource = null; curveSliderOriginalProfile = curveSliderContext = null; }
        void RestoreCurveSettingSlider()
        {
            if (curveSliderSource == null) return;
            curve.UpdateCurve(curveSliderSource, curveSliderMixed, curveSliderContext);
            // Restore numeric drafts without rebuilding the rows during native
            // mouse capture/focus dispatch. A selection refresh may follow next.
            var ids = curveSliderBindings; var chosen = history.Current.Bindings.Where(b => ids.Contains(b.BindingId)).ToArray();
            foreach (DataGridViewRow row in settings.Rows)
            {
                string property = row.Tag as string; if (property == null || property == "Curve") continue;
                var field = typeof(SignalSettings).GetField(property);
                string[] values = chosen.Select(b => Convert.ToString(field.GetValue(b.Processing), CultureInfo.InvariantCulture)).Distinct().ToArray();
                row.Cells[1].Value = values.Length == 0 ? "—" : values.Length == 1 ? values[0] : "Gemischt";
            }
            ClearCurveSliderDraft();
        }
        static CurveSettingRange CurveSliderRange(string property, Binding[] chosen)
        {
            if (property == "Curve") return null;
            var result = new CurveSettingRange { Property = property, Minimum = property == "ButtonThreshold" ? .001 : property == "Exponent" ? .05 : 0,
                Maximum = property == "Exponent" ? 10 : property == "Scale" ? 2 : property == "SmoothingTimeConstant" ? .25 : 1,
                Step = property == "Exponent" ? .05 : property == "Scale" ? .01 : .001, Available = chosen.Length != 0 };
            if (chosen.Length == 0) return result;
            var values = chosen.Select(b => (double)typeof(SignalSettings).GetField(property).GetValue(b.Processing)).ToArray();
            result.FirstValue = values[0];
            if (property == "Exponent" || property == "Scale" || property == "SmoothingTimeConstant") result.Maximum = Math.Max(result.Maximum, values.Max());
            if (property == "Exponent" || property == "ButtonThreshold") result.Minimum = Math.Min(result.Minimum, values.Min());
            if (property == "MinOutput") result.Maximum = chosen.Min(b => b.Processing.MaxOutput);
            if (property == "MaxOutput") result.Minimum = chosen.Max(b => b.Processing.MinOutput);
            // The legal shared range is the intersection for every selected
            // mapping. Moving one slider never silently rewrites its neighbors.
            if (property == "TopDeadzone") result.Maximum = chosen.Min(b => 1 - b.Processing.BottomDeadzone - b.Processing.Hysteresis) * (1 - .000001);
            if (property == "BottomDeadzone") result.Maximum = chosen.Min(b => 1 - b.Processing.TopDeadzone - b.Processing.Hysteresis) * (1 - .000001);
            if (property == "Hysteresis") result.Maximum = chosen.Min(b => 1 - b.Processing.TopDeadzone - b.Processing.BottomDeadzone) * (1 - .000001);
            if (property == "TopDeadzone" || property == "BottomDeadzone" || property == "Hysteresis")
                result.Maximum = Math.Floor(result.Maximum / result.Step) * result.Step;
            return result;
        }
        void RefreshCurveSettingRow(DataGridViewRow row, string property, Binding[] chosen, string[] values)
        {
            row.MinimumHeight = row.Height = 44;
            row.Cells[0].Style.WrapMode = DataGridViewTriState.True;
            var slider = row.Cells[2] as CurveSettingSliderCell;
            slider.Range = CurveSliderRange(property, chosen);
            slider.Value = slider.Range != null && values.Length == 1 ? (object)slider.Range.FirstValue : null;
            string description = CurveSettingHelp(property);
            if (slider.Range != null)
            {
                description += "\n\n" + Tr("Regler ziehen oder mit ←/→ anpassen. Umschalt = großer Schritt; Pos1/Ende = Grenzen. Loslassen übernimmt, Esc bricht ab.",
                    "Drag the slider or use ←/→. Shift makes a larger step; Home/End reach the limits. Release to apply; Esc cancels.");
                description += "\n" + Tr("Reglerbereich: ", "Slider range: ") + slider.Range.Display(slider.Range.Minimum) + " – " + slider.Range.Display(slider.Range.Maximum) + ".";
                description += "\n" + Tr("Zahlenfeld: ", "Number field: ") + (property == "SmoothingTimeConstant" ? Tr("Sekunden (0,05 = 50 ms).", "seconds (0.05 = 50 ms).") :
                    property == "Exponent" || property == "Scale" ? Tr("Faktor; größere Werte können direkt eingegeben werden.", "factor; larger values can be entered directly.") : Tr("0–1 (0,25 = 25 %).", "0–1 (0.25 = 25%)."));
            }
            description += "\n" + Tr("Gilt für alle ausgewählten Zuordnungen. Gemischt bleibt unverändert, bis du einen Wert festlegst.",
                "Applies to every selected mapping. Mixed values stay unchanged until you choose a value.");
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = description;
        }
        static string CurveSettingHelp(string property)
        {
            switch (property)
            {
                case "TopDeadzone": return Tr("Ruhebereich: Kleine Bewegungen am oberen Ende erzeugen noch keine Ausgabe. 0,05 ignoriert die ersten 5 % des kalibrierten Weges. Mehr Ruhebereich bedeutet späteren Beginn.", "Top deadzone: small movements near release produce no output. 0.05 ignores the first 5% of calibrated travel. Increase it to start responding later.");
                case "BottomDeadzone": return Tr("Anschlagbereich: Die volle Ausgabe wird vor dem mechanischen Anschlag erreicht. 0,05 erreicht sie nach 95 % des Weges. Zusammen mit Ruhebereich und Schaltabstand muss nutzbarer Weg bleiben.", "Bottom deadzone: full output is reached before bottoming out. 0.05 reaches it at 95% travel. Top deadzone, bottom deadzone and switch gap must leave usable travel.");
                case "Curve": return Tr("Kurvenform: Linear ist gleichmäßig. Exponentiell beginnt sanfter, logarithmisch stärker. Smoothstep rundet die Enden ab. Eigene Punkte sind gerade verbunden; Bézier verbindet sie glatt. Im Diagramm Punkte setzen und ziehen; bei Bézier einen Punkt für seine Griffe wählen.", "Curve shape: Linear is even. Exponential starts gently; logarithmic starts strongly. Smoothstep rounds the ends. Custom joins points with straight segments; Bézier joins them smoothly. Add and drag points in the graph; select a Bézier point to reveal its handles.");
                case "Exponent": return Tr("Kurvenstärke wirkt nur bei exponentieller und logarithmischer Kurve. Größer als 1 macht die exponentielle Kurve am Anfang sanfter. Bei Linear, Smoothstep und eigenen Punkten hat dieser Wert keinen Einfluss.", "Curve strength affects Exponential and Logarithmic curves. Above 1, an exponential curve starts more gently. Linear, Smoothstep and custom points ignore this value.");
                case "MinOutput": return Tr("Kleinste aktive Ausgabe: Sobald ein Druck die Totbereiche überwindet, startet die Ausgabe hier. 0,2 bedeutet mindestens 20 %. Eine losgelassene Taste gibt weiterhin 0 aus.", "Minimum active output: once pressure passes the deadzones, output starts here. 0.2 means at least 20%. A released key still outputs zero.");
                case "MaxOutput": return Tr("Maximale Ausgabe begrenzt die Zuordnung, auch bei vollständig gedrückter Taste. 0,8 begrenzt sie auf 80 %. Sie darf nicht unter der kleinsten aktiven Ausgabe liegen.", "Maximum output caps this mapping even when the key is fully pressed. 0.8 limits it to 80%. It cannot be below minimum active output.");
                case "Scale": return Tr("Ausgabestärke multipliziert das Kurvenergebnis vor dem Ausgabebereich. 0,5 halbiert es; 2 erreicht die Sättigung früher. 0 schaltet die Ausgabe dieser Zuordnung ab.", "Output strength multiplies the curve result before the output range. 0.5 halves it; 2 reaches saturation earlier. Zero suppresses this mapping's output.");
                case "OutputDeadzone": return Tr("Kleine Ausgaben ignorieren: Kurvenergebnisse bis zu diesem Wert bleiben 0. Der verbleibende Bereich wird wieder auf die Ausgabegrenzen verteilt. 0,1 verwirft die untersten 10 % nach der Ausgabestärke.", "Ignore small outputs: curve results up to this value stay at zero. The remaining range is stretched across the output limits. 0.1 removes the lowest 10% after output strength is applied.");
                case "Hysteresis": return Tr("Schaltabstand verhindert Flattern: Der Druck muss erst Ruhebereich plus Schaltabstand überschreiten; beim Loslassen endet die Ausgabe schon am Ruhebereich. 0,02 bedeutet 2 % Weg Abstand. Für erneutes Auslösen durch kleine Bewegungen nutze Rapid Trigger unter Tasten.", "Switch gap prevents flicker: pressure must pass top deadzone plus this gap to activate, but releases at the top deadzone. 0.02 adds a 2% travel gap. For retriggering with small movements, use Rapid Trigger under Keys.");
                case "SmoothingTimeConstant": return Tr("Glättung dämpft schnelle Ausgabeänderungen und erhöht die Verzögerung. Nach einer Zeitkonstante sind etwa 63 % einer Änderung erreicht. 0 = aus; 0,05 Sekunden = 50 ms. Loslassen bleibt sofort. Die statische Kurve zeigt diesen zeitlichen Effekt nicht.", "Smoothing softens quick output changes and adds delay. One time constant reaches about 63% of a change. Zero is off; 0.05 seconds is 50 ms. Release is immediate. The static curve cannot show this time-dependent effect.");
                case "ButtonThreshold": return Tr("Auslösepunkt für digitale Controller-Buttons: Die berechnete Ausgabe muss diesen Wert erreichen. 0,5 bedeutet 50 % Ausgabe. Sticks und analoge Trigger behalten ihre stufenlose Ausgabe; der Punkt beeinflusst nur digitale Buttons.", "Digital button activation point: calculated output must reach this value. 0.5 means 50% output. Sticks and analog triggers retain their continuous values; this threshold affects digital buttons only.");
                default: return "";
            }
        }
        static string CurveSettingCaption(string property)
        {
            switch (property)
            {
                case "TopDeadzone": return Tr("Ruhebereich (0–1)", "Top deadzone (0–1)");
                case "BottomDeadzone": return Tr("Anschlagbereich (0–1)", "Bottom deadzone (0–1)");
                case "Curve": return Tr("Kurvenform", "Curve shape");
                case "Exponent": return Tr("Kurvenstärke (×)", "Curve strength (×)");
                case "MinOutput": return Tr("Ausgabe min. (0–1)", "Min. output (0–1)");
                case "MaxOutput": return Tr("Ausgabe max. (0–1)", "Max. output (0–1)");
                case "Scale": return Tr("Ausgabestärke (×)", "Output strength (×)");
                case "OutputDeadzone": return Tr("Ausgabesperre (0–1)", "Output deadzone (0–1)");
                case "Hysteresis": return Tr("Schaltabstand (0–1)", "Switch gap (0–1)");
                case "SmoothingTimeConstant": return Tr("Glättung (s)", "Smoothing (s)");
                case "ButtonThreshold": return Tr("Button-Schwelle (0–1)", "Button threshold (0–1)");
                default: return property;
            }
        }
    }
}
