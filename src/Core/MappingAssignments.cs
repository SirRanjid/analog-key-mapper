using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    public static class MappingAssignments
    {
        // One gesture appends only missing key/target/controller assignments.
        // Existing assignments, including disabled ones, retain IDs and settings.
        // Validate before returning; the caller's profile remains unchanged on failure.
        public static Profile Add(Profile profile, IEnumerable<int> keys, OutputTarget target)
        { return Add(profile, keys, target, "main"); }
        public static Profile Add(Profile profile, IEnumerable<int> keys, OutputTarget target, string controllerId)
        {
            MappingValidation.RequireValid(profile);
            bool knownController = profile.Controllers.Count == 0 && controllerId == "main";
            foreach (ControllerDefinition controller in profile.Controllers) if (controller.Id == controllerId) knownController = true;
            if (!knownController) throw new ArgumentException("Der ausgewählte Controller fehlt.", "controllerId");
            if (keys == null) throw new ArgumentNullException("keys");
            if (!Enum.IsDefined(typeof(OutputTarget), target))
                throw new ArgumentOutOfRangeException("target", "Unbekanntes Ausgabeziel.");
            var selected = new List<int>();
            var seen = new HashSet<int>();
            foreach (int key in keys)
            {
                if (key < 0 || key > 255) throw new ArgumentOutOfRangeException("keys", "Tastenindex muss in 0..255 liegen.");
                if (seen.Add(key)) selected.Add(key);
            }
            if (selected.Count == 0) throw new ArgumentException("Mindestens eine Taste auswählen.", "keys");
            var assigned = new HashSet<int>();
            foreach (Binding binding in profile.Bindings)
                if (binding.ControllerId == controllerId && binding.Target == target) assigned.Add(binding.KeyIndex);
            selected.RemoveAll(delegate(int key) { return assigned.Contains(key); });
            if (profile.Bindings.Count > 4096 - selected.Count)
                throw new ArgumentException("Die Zuordnung würde die Grenze von 4096 Einträgen überschreiten.", "keys");

            Profile result = ProfileJson.Clone(profile);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in result.Bindings) ids.Add(binding.BindingId);
            foreach (int key in selected)
            {
                var binding = new Binding { KeyIndex = key, Target = target, ControllerId = controllerId };
                while (!ids.Add(binding.BindingId)) binding.BindingId = Guid.NewGuid().ToString("N");
                result.Bindings.Add(binding);
            }
            MappingValidation.RequireValid(result);
            return result;
        }
    }
}
