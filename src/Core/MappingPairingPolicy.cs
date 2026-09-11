using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Immutable metadata snapshot for a single controller's mapping gesture.
    // Disabled bindings still occupy their pair; this never enables an output.
    public sealed class MappingPairingPolicy
    {
        readonly Dictionary<OutputTarget, HashSet<int>> existing = new Dictionary<OutputTarget, HashSet<int>>();
        public MappingPairingPolicy(Profile profile, string controllerId)
        {
            MappingValidation.RequireValid(profile);
            bool known = profile.Controllers.Count == 0 && controllerId == "main";
            foreach (ControllerDefinition controller in profile.Controllers)
                if (String.Equals(controller.Id, controllerId, StringComparison.Ordinal)) known = true;
            if (!known) throw new ArgumentException("The selected controller is missing.", "controllerId");
            foreach (Binding binding in profile.Bindings)
            {
                if (!String.Equals(binding.ControllerId, controllerId, StringComparison.Ordinal)) continue;
                HashSet<int> keys;
                if (!existing.TryGetValue(binding.Target, out keys)) existing.Add(binding.Target, keys = new HashSet<int>());
                keys.Add(binding.KeyIndex);
            }
        }
        static List<int> Keys(IEnumerable<int> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var result = new List<int>(); var seen = new HashSet<int>();
            foreach (int key in source)
            {
                if (key < 0 || key > 255) throw new ArgumentOutOfRangeException("source");
                if (seen.Add(key)) result.Add(key);
            }
            return result;
        }
        public int[] MissingKeys(IEnumerable<int> source, OutputTarget target)
        {
            if (!Enum.IsDefined(typeof(OutputTarget), target)) throw new ArgumentOutOfRangeException("target");
            var keys = Keys(source); HashSet<int> occupied;
            if (existing.TryGetValue(target, out occupied)) keys.RemoveAll(occupied.Contains);
            return keys.ToArray();
        }
        public OutputTarget[] AvailableTargets(IEnumerable<int> source)
        {
            var keys = Keys(source); var result = new List<OutputTarget>();
            foreach (OutputTarget target in Enum.GetValues(typeof(OutputTarget)))
                if (MissingKeys(keys, target).Length != 0) result.Add(target);
            return result.ToArray();
        }
    }
}
