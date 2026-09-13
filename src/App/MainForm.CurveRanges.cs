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
        readonly CurveRangeRail curveInputRange = new CurveRangeRail(), curveOutputRange = new CurveRangeRail { Reversed = true };
        void BuildCurveRangeRails()
        {
            curve.SetRangeRails(curveInputRange, curveOutputRange);
            WireCurveRangeRail(curveInputRange, true); WireCurveRangeRail(curveOutputRange, false);
            advancedPanel.VisibleChanged += delegate { if (!advancedPanel.Visible) CancelCurveRangeGestures(); };
        }
        void WireCurveRangeRail(CurveRangeRail rail, bool input)
        {
            rail.EditStarted += delegate(int handle) {
                settings.EndEdit(); ((CurveSettingsGrid)settings).CancelSlider();
                BeginCurveSettingSlider(RangeProperty(input, handle));
            };
            rail.Previewed += delegate(int handle, double value) {
                PreviewCurveSettingSlider(RangeProperty(input, handle), RangePropertyValue(input, handle, value));
                UpdateRangeRailTip(rail, input);
            };
            rail.Committed += delegate(int handle, double value) {
                Attempt(delegate { CompleteCurveSettingSlider(RangeProperty(input, handle), RangePropertyValue(input, handle, value)); });
            };
            rail.Canceled += delegate { RestoreCurveSettingSlider(); UpdateRangeRailTip(rail, input); };
        }
        static string RangeProperty(bool input, int handle)
        { return input ? handle == 0 ? "TopDeadzone" : "BottomDeadzone" : handle == 0 ? "MinOutput" : "MaxOutput"; }
        static double RangePropertyValue(bool input, int handle, double value) { return input && handle == 1 ? 1 - value : value; }
        void CancelCurveRangeGestures() { curveInputRange.CancelEdit(); curveOutputRange.CancelEdit(); }
        void RefreshCurveRangeRails(Binding[] chosen)
        {
            foreach (var rail in new[] { curveInputRange, curveOutputRange })
            {
                bool input = rail == curveInputRange;
                rail.Enabled = chosen.Length != 0;
                for (int handle = 0; handle < 2; handle++)
                {
                    string property = RangeProperty(input, handle);
                    CurveSettingRange range = CurveSliderRange(property, chosen);
                    double[] values = chosen.Select(b => (double)typeof(SignalSettings).GetField(property).GetValue(b.Processing)).ToArray();
                    bool reverse = input && handle == 1;
                    double shown = chosen.Length == 0 ? handle : RangePropertyValue(input, handle, range.FirstValue);
                    rail.Configure(handle, shown, values.Distinct().Count() > 1, reverse ? 1 - range.Maximum : range.Minimum, reverse ? 1 - range.Minimum : range.Maximum);
                }
                UpdateRangeRailTip(rail, input);
            }
        }
        void UpdateRangeRailTip(CurveRangeRail rail, bool input)
        {
            rail.AccessibleName = input ? Tr("Nutzbarer Eingangsbereich", "Usable input range") : Tr("Ausgabebereich", "Output range");
            string lower = rail.MixedAt(0) ? Tr("gemischt", "mixed") : (rail.ValueAt(0) * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
            string upper = rail.MixedAt(1) ? Tr("gemischt", "mixed") : (rail.ValueAt(1) * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
            string help = rail.AccessibleName + ": " + lower + " – " + upper + ".\n" + (input ?
                Tr("IN: 0 % oben, 100 % unten. Oberer Griff: Ruhebereich. Unterer Griff: volle Eingabe vor dem Anschlag. Der Schaltabstand bleibt erhalten.",
                    "IN: 0% at the top, 100% at the bottom. Upper handle: top deadzone. Lower handle: full input before bottoming out. The switch gap is preserved.") :
                Tr("OUT: 0 % unten, 100 % oben, wie die Ausgabeachse. Unterer Griff: kleinste aktive Ausgabe. Oberer Griff: maximale Ausgabe. Losgelassen bleibt die Ausgabe 0.",
                    "OUT: 0% at the bottom, 100% at the top, matching the output axis. Lower handle: minimum active output. Upper handle: maximum output. Released output remains zero."));
            help += "\n" + Tr("Griffe ziehen. ←/→ oder Leertaste wählt einen Griff, ↑/↓ verändert ihn, Umschalt für große Schritte. Loslassen übernimmt einmal; Esc bricht ab. Alle ausgewählten Zuordnungen ändern sich gemeinsam.",
                "Drag a handle. Left/Right or Space selects one, Up/Down adjusts it, Shift uses larger steps. Release applies one edit; Esc cancels. Every selected mapping changes together.");
            rail.AccessibleDescription = help; keyCardTips.SetToolTip(rail, null);
        }
    }
}
