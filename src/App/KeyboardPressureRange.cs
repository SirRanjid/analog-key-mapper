using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

namespace Tk75.App
{
    // The editor, preview and all controller sessions share this same resolved
    // range. Legacy measurements remain on disk and supply an initial envelope.
    public sealed class KeyboardPressureRange
    {
        public readonly double Minimum, Maximum, ScaleMaximum;
        public readonly bool IsDefault;
        public readonly bool HasIgnoredLegacyValues;
        public KeyboardPressureRange(CalibrationDocument document)
        {
            Minimum = DefaultCalibration.Rest; Maximum = DefaultCalibration.Bottom;
            IsDefault = document == null || (!document.GlobalMinimum.HasValue && document.Entries.Count == 0);
            if (document != null && document.GlobalMinimum.HasValue)
            { Minimum = document.GlobalMinimum.Value; Maximum = document.GlobalMaximum.Value; }
            else if (document != null && document.Entries.Count != 0)
            {
                var usable = document.Entries.Where(e => e.Rest >= 0 && e.Bottom > e.Rest && e.Bottom <= 65535).ToArray();
                HasIgnoredLegacyValues = usable.Length != document.Entries.Count;
                if (usable.Length != 0) { Minimum = usable.Min(e => e.Rest); Maximum = usable.Max(e => e.Bottom); }
                else IsDefault = true;
            }
            ScaleMaximum = Math.Max(Maximum, document != null && document.ScaleMaximum.HasValue ? document.ScaleMaximum.Value : DefaultCalibration.Bottom);
        }
        public Dictionary<int, Calibration> Resolve()
        {
            var result = new Dictionary<int, Calibration>(256);
            for (int key = 0; key <= 255; key++) result.Add(key, new Calibration(Minimum, Maximum));
            return result;
        }
        public double Depth(int raw) { return Math.Max(0, Math.Min(1, (raw - Minimum) / (Maximum - Minimum))); }
        public CalibrationDocument Apply(CalibrationDocument original, double minimum, double maximum, double scaleMaximum)
        {
            if (original == null) throw new ArgumentNullException("original");
            return new CalibrationDocument { DeviceIdentity = original.DeviceIdentity, ProtocolFingerprint = original.ProtocolFingerprint,
                Entries = original.Entries.ToList(), GlobalMinimum = minimum, GlobalMaximum = maximum, ScaleMaximum = Math.Max(maximum, scaleMaximum) };
        }
    }
}
