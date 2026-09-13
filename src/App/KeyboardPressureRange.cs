using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

namespace Tk75.App
{
    // Old keyboard-wide settings remain the fallback. Explicit edits override
    // only their selected keys; all sessions use the same cached resolution.
    public sealed class KeyboardPressureRange
    {
        public readonly double Minimum, Maximum, ScaleMaximum, MaximumEndpoint;
        public readonly bool IsDefault;
        public readonly bool HasIgnoredLegacyValues;
        readonly Dictionary<int, Calibration> resolved = new Dictionary<int, Calibration>(256);
        readonly HashSet<int> configuredKeys = new HashSet<int>();
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
            for (int key = 0; key <= 255; key++) resolved.Add(key, new Calibration(Minimum, Maximum));
            if (document != null)
                foreach (KeyPressureRangeEntry entry in document.KeyRanges)
                {
                    resolved[entry.KeyIndex] = new Calibration(entry.Minimum, entry.Maximum);
                    configuredKeys.Add(entry.KeyIndex);
                }
            MaximumEndpoint = resolved.Values.Max(value => value.Bottom);
            ScaleMaximum = Math.Max(MaximumEndpoint, document != null && document.ScaleMaximum.HasValue ? document.ScaleMaximum.Value : DefaultCalibration.Bottom);
        }

        public Dictionary<int, Calibration> Resolve() { return resolved; }
        public Calibration ForKey(int keyIndex) { return resolved[keyIndex]; }
        public bool IsDefaultForKey(int keyIndex) { return IsDefault && !configuredKeys.Contains(keyIndex); }
        public double Depth(int keyIndex, int raw)
        {
            Calibration range = ForKey(keyIndex);
            return Math.Max(0, Math.Min(1, (raw - range.Rest) / (range.Bottom - range.Rest)));
        }
        public CalibrationDocument Apply(CalibrationDocument original, IEnumerable<int> keys, double minimum, double maximum, double scaleMaximum)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            var selected = new HashSet<int>(keys);
            if (selected.Any(key => key < 0 || key > 255)) throw new ArgumentOutOfRangeException("keys");
            var next = Copy(original);
            next.KeyRanges = next.KeyRanges.Where(entry => !selected.Contains(entry.KeyIndex)).ToList();
            next.KeyRanges.AddRange(selected.OrderBy(key => key).Select(key => new KeyPressureRangeEntry { KeyIndex = key, Minimum = minimum, Maximum = maximum }));
            double endpoint = resolved.Where(item => !selected.Contains(item.Key)).Select(item => item.Value.Bottom).DefaultIfEmpty(1).Max();
            next.ScaleMaximum = Math.Max(Math.Max(endpoint, maximum), scaleMaximum);
            return next;
        }
        public CalibrationDocument ApplyScale(CalibrationDocument original, double scaleMaximum)
        {
            var next = Copy(original);
            next.ScaleMaximum = Math.Max(MaximumEndpoint, scaleMaximum);
            return next;
        }
        static CalibrationDocument Copy(CalibrationDocument original)
        {
            if (original == null) throw new ArgumentNullException("original");
            return new CalibrationDocument { DeviceIdentity = original.DeviceIdentity, ProtocolFingerprint = original.ProtocolFingerprint,
                Entries = original.Entries.ToList(), KeyRanges = original.KeyRanges.ToList(), GlobalMinimum = original.GlobalMinimum,
                GlobalMaximum = original.GlobalMaximum, ScaleMaximum = original.ScaleMaximum };
        }
    }
}
