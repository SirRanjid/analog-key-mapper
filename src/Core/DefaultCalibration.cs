using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // User-selected standard sensor range. These in-memory values are not
    // measurements and must not be written to the stored hardware calibration.
    // Resolving ranges does not supply samples: Compose still requires current
    // raw input, filtered for freshness by the reader before it reaches the core.
    public static class DefaultCalibration
    {
        public const int MinimumKeyIndex = 0;
        public const int MaximumKeyIndex = 255;
        public const double Rest = 0;
        public const double Bottom = 385;

        public static Dictionary<int, Calibration> Resolve(IDictionary<int, Calibration> existing)
        {
            var resolved = new Dictionary<int, Calibration>();
            for (int key = MinimumKeyIndex; key <= MaximumKeyIndex; key++)
                resolved.Add(key, new Calibration(Rest, Bottom));
            if (existing == null) return resolved;
            foreach (KeyValuePair<int, Calibration> entry in existing)
            {
                if (entry.Key < MinimumKeyIndex || entry.Key > MaximumKeyIndex)
                    throw new ArgumentException("Calibration key index must be between 0 and 255.", "existing");
                Calibration value = entry.Value;
                // Existing invalid/null entries remain invalid: defaults fill
                // absent entries only, rather than hiding a calibration error.
                resolved[entry.Key] = value == null ? null : new Calibration(value.Rest, value.Bottom)
                {
                    UsableMin = value.UsableMin,
                    UsableMax = value.UsableMax,
                    MeasuredTravel = value.MeasuredTravel
                };
            }
            return resolved;
        }
    }
}
