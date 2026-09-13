using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        void RefreshKeyBehaviorAnnotations()
        {
            if (keyboard.LayoutModel == null) return;
            Profile profile = history.Current;
            var inputs = profile.Inputs.ToDictionary(input => input.KeyIndex);
            var outputs = profile.Bindings.GroupBy(binding => binding.KeyIndex).ToDictionary(group => group.Key, group => group.ToArray());
            var controllers = ControllerRouting.EffectiveControllers(profile).ToDictionary(controller => controller.Id, StringComparer.Ordinal);
            var individualRanges = new HashSet<int>(calibration == null ? new int[0] : calibration.KeyRanges.Select(entry => entry.KeyIndex));
            foreach (int index in LayoutIndices())
            {
                KeyInputSettings input; inputs.TryGetValue(index, out input);
                Tk75.Mapping.Binding[] mapped; if (!outputs.TryGetValue(index, out mapped)) mapped = new Tk75.Mapping.Binding[0];
                var range = sharedPressureRange.ForKey(index);
                string rangeText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}–{1:0.##}", range.Rest, range.Bottom);
                string description = DescribePhysicalBehavior(input) + "\n" + Tr("Druckbereich dieser Taste: ", "This key's pressure range: ") + rangeText;
                string compact = "", full = "", narrow = "";
                if (input != null)
                {
                    string press = SummaryNumber(input.ActuationPoint * 100);
                    string release = SummaryNumber(input.ReleaseMovement * 100);
                    compact = input.RapidTriggerEnabled ? "↓" + press + "↑" + release : "↕" + press;
                    full = compact;
                    narrow = input.RapidTriggerEnabled ? press + "/" + release : compact;
                    description += "\n" + Tr("↓ Erstes Auslösen · ↑ Loslassen · ↕ gleicher Druck-/Loslasspunkt. Kleine Beschriftungen ohne %-Zeichen: Prozent des individuellen Druckbereichs; bei Platzmangel Druck/Loslassen.",
                        "↓ Initial actuation · ↑ Release · ↕ same press/release point. Compact labels omit the % sign: percentages of this key's pressure range; press/release when space is tight.");
                    description += "\n" + (input.RapidTriggerEnabled
                        ? Tr("Rapid Trigger: Loslassen ab dem höchsten Druck, erneutes Auslösen um den Aktuationsweg ab dem tiefsten Druck seit dem Loslassen.",
                            "Rapid Trigger: release movement is measured from peak pressure; repress uses actuation travel from the lowest pressure since release.")
                        : Tr("Aktiv ab dem Auslösepunkt; darunter wird die Taste losgelassen.", "Active at or above actuation; released below it."));
                }
                else description += "\n" + Tr("Kein zusätzlicher Auslösepunkt eingestellt; die Ausgabe folgt dem Druckbereich.", "No additional actuation gate is configured; output follows the pressure range.");
                if (mapped.Length != 0)
                {
                    var markers = mapped.Select(KeyOutputMarker).Distinct().ToArray();
                    if (input == null)
                    {
                        compact = full = markers.Length == 1 ? markers[0] : "→…";
                        narrow = markers.Length == 1 && mapped.All(binding => (int)binding.Target < (int)OutputTarget.A)
                            ? "→" + SummaryNumber(mapped[0].Processing.MaxOutput * 100) : compact;
                    }
                    description += "\n\n" + Tr("Ausgänge aller Controller: ● Button-Schwelle in Prozent der berechneten Ausgabe; → analoger Ausgabebereich (ein Wert: Maximum). →… bedeutet unterschiedliche Ausgänge; keine erfundene physische Auslöseschwelle.",
                        "Outputs across all controllers: ● button threshold as a percentage of calculated output; → analog output range (one value: maximum). →… means different outputs; it is not an invented physical actuation gate.");
                    foreach (var binding in mapped.OrderBy(binding => binding.ControllerId, StringComparer.Ordinal).ThenBy(binding => binding.Target))
                    {
                        ControllerDefinition controller; controllers.TryGetValue(binding.ControllerId, out controller);
                        string name = controller == null ? binding.ControllerId : controller.Name;
                        ControllerStyle style = controller != null && controller.Kind == ControllerKind.DualSense ? ControllerStyle.PlayStation5 : ControllerStyle.Xbox;
                        description += "\n" + name + " · " + ControllerPresentation.Label(binding.Target, style) + ": " + KeyOutputMarker(binding) +
                            (binding.Enabled ? "" : Tr(" (aus)", " (off)")) + "\n" + DescribeMappingResponse(binding.Processing, binding.Target).Detail;
                    }
                }
                if (individualRanges.Contains(index))
                {
                    description += "\n" + Tr("Individueller Rohdruckbereich eingestellt: ", "Individual raw pressure range configured: ") + rangeText + ".";
                    if (compact.Length == 0) { compact = full = "R" + rangeText; narrow = "R"; }
                }
                keyboard.SetBehaviorAnnotation(index, new KeyboardBehaviorAnnotation { Compact = compact, Full = full, Narrow = narrow, Description = description });
            }
        }
        static string KeyOutputMarker(Tk75.Mapping.Binding binding)
        {
            SignalSettings settings = binding.Processing;
            if ((int)binding.Target >= (int)OutputTarget.A) return "●" + SummaryNumber(settings.ButtonThreshold * 100);
            return "→" + SummaryNumber(settings.MinOutput * 100) + "–" + SummaryNumber(settings.MaxOutput * 100);
        }
    }
}
