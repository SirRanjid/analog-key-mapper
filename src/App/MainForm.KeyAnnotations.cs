using System;
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
            var inputs = history.Current.Inputs.ToDictionary(input => input.KeyIndex);
            foreach (int index in LayoutIndices())
            {
                KeyInputSettings input; inputs.TryGetValue(index, out input);
                var range = sharedPressureRange.ForKey(index);
                string rangeText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}–{1:0.##}", range.Rest, range.Bottom);
                string description = DescribePhysicalBehavior(input) + "\n" + Tr("Druckbereich dieser Taste: ", "This key's pressure range: ") + rangeText;
                string compact = "", full = "";
                if (input != null)
                {
                    compact = "↓" + SummaryPercent(input.ActuationPoint);
                    full = compact + " ↑" + SummaryPercent(input.RapidTriggerEnabled ? input.ReleaseMovement : input.ActuationPoint);
                    if (input.RapidTriggerEnabled) full += " ↧" + SummaryPercent(input.PressMovement);
                    description += "\n" + Tr("↓ Erstes Auslösen · ↑ Loslassen · ↧ Erneutes Auslösen. Werte in Prozent des individuellen Druckbereichs.",
                        "↓ Initial actuation · ↑ Release · ↧ Repress. Values are percentages of this key's pressure range.");
                    description += "\n" + (input.RapidTriggerEnabled
                        ? Tr("Rapid Trigger: Loslassen ab dem höchsten Druck, erneutes Auslösen ab dem tiefsten Druck seit dem Loslassen.",
                            "Rapid Trigger: release movement is measured from peak pressure; repress movement from the lowest pressure since release.")
                        : Tr("Aktiv ab dem Auslösepunkt; darunter wird die Taste losgelassen.", "Active at or above actuation; released below it."));
                }
                else description += "\n" + Tr("Kein zusätzlicher Auslösepunkt eingestellt; die Ausgabe folgt dem Druckbereich.", "No additional actuation gate is configured; output follows the pressure range.");
                keyboard.SetBehaviorAnnotation(index, new KeyboardBehaviorAnnotation { Compact = compact, Full = full, Description = description });
            }
        }
    }
}
