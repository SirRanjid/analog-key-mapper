using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        // Reconcile choices by meaning, retaining existing option objects and
        // native list entries whenever their order and caption are unchanged.
        // Callers own the appropriate event guard while rebinding selection.
        static void RefreshChoices<T>(ComboBox picker, IEnumerable<T> choices, Func<T, T, bool> same) where T : class
        {
            picker.BeginUpdate();
            try
            {
                T[] desired = choices.ToArray();
                // Remove obsolete entries first. Exchanging the selected key
                // then removes/adds just that key in the opposite-key list,
                // instead of shifting every intervening native list entry.
                for (int previous = picker.Items.Count - 1; previous >= 0; previous--)
                    if (!desired.Any(choice => same((T)picker.Items[previous], choice))) picker.Items.RemoveAt(previous);
                int index = 0;
                foreach (T choice in desired)
                {
                    if (index < picker.Items.Count && same((T)picker.Items[index], choice)) { index++; continue; }
                    int found = -1;
                    for (int candidate = index + 1; candidate < picker.Items.Count; candidate++)
                        if (same((T)picker.Items[candidate], choice)) { found = candidate; break; }
                    T item = choice;
                    if (found >= 0) { item = (T)picker.Items[found]; picker.Items.RemoveAt(found); }
                    picker.Items.Insert(index++, item);
                }
                while (picker.Items.Count > index) picker.Items.RemoveAt(picker.Items.Count - 1);
            }
            finally { picker.EndUpdate(); }
        }

        void RefreshOppositeChoices(Profile profile, int[] selected)
        {
            var choices = new List<OppositeKeyItem>();
            if (selected.Length > 1) choices.Add(new OppositeKeyItem(null, Tr("Bestehende Paare beibehalten", "Keep existing pairs"), true));
            if (selected.Length <= 2) choices.Add(new OppositeKeyItem(null, Tr("Kein Gegenpart", "No opposite key")));
            if (selected.Length == 2)
                choices.Add(new OppositeKeyItem(selected[1], Label(selected[0]) + " ↔ " + Label(selected[1])));
            else if (selected.Length == 1)
            {
                var candidates = LayoutIndices().Concat(profile.Bindings.Select(b => b.KeyIndex)).Concat(profile.Inputs.Select(i => i.KeyIndex))
                    .Concat(reader == null ? Enumerable.Empty<int>() : reader.GetSnapshot().Select(s => s.KeyIndex))
                    .Concat(keymap == null ? Enumerable.Empty<int>() : keymap.Entries.Select(e => e.KeyIndex))
                    .Distinct().Where(i => i != selected[0]).OrderBy(i => Label(i), StringComparer.CurrentCultureIgnoreCase);
                foreach (int candidate in candidates) choices.Add(new OppositeKeyItem(candidate, Label(candidate)));
            }
            RefreshChoices(oppositeKey, choices, (before, after) => before.Index == after.Index && before.Preserve == after.Preserve && before.ToString() == after.ToString());
        }
    }
}
