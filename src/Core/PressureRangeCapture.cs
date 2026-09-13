using System;

namespace Tk75.Mapping
{
    // One explicit press/release capture. The input thread only feeds numbers;
    // the UI owns saving and reconfiguration after completion.
    public sealed class PressureRangeCapture
    {
        readonly int keyIndex;
        readonly double threshold;
        bool pressing;
        double baseline;
        public bool Completed { get; private set; }
        public bool Pressing { get { return pressing; } }
        public double Minimum { get; private set; }
        public double Maximum { get; private set; }
        public double? Latest { get; private set; }

        public PressureRangeCapture(int keyIndex, double rest, double bottom, double? initial)
        {
            if (keyIndex < 0 || keyIndex > 255 || !Finite(rest) || !Finite(bottom) || rest < 0 || bottom <= rest || bottom > 65535)
                throw new ArgumentOutOfRangeException("rest");
            this.keyIndex = keyIndex;
            threshold = Math.Max(1, Math.Min(4, (bottom - rest) * .005));
            baseline = rest;
            if (initial.HasValue && Finite(initial.Value) && initial.Value >= 0 && initial.Value <= 65535)
            {
                // The observed released level may differ from an old slider
                // minimum. Calibration must be able to repair that minimum.
                baseline = initial.Value;
            }
            Minimum = Maximum = baseline;
        }

        public void Feed(int index, int raw)
        {
            if (Completed || index != keyIndex || raw < 0 || raw > 65535) return;
            Latest = raw;
            if (!pressing)
            {
                if (raw <= baseline + threshold) { baseline = Math.Min(baseline, raw); Minimum = Maximum = baseline; return; }
                pressing = true; Minimum = baseline; Maximum = raw; return;
            }
            Maximum = Math.Max(Maximum, raw);
            if (raw <= baseline + threshold)
            {
                Minimum = Math.Min(Minimum, raw);
                pressing = false; Completed = Maximum > Minimum;
            }
        }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
