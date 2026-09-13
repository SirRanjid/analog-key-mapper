using System;

namespace Tk75.Mapping
{
    // One deliberate press after arming. A key held when calibration starts
    // must first return to its known calibrated rest; that hold is never saved.
    public sealed class InputThresholdCapture
    {
        readonly int keyIndex;
        readonly double rest, releaseLevel;
        public bool WaitingForRelease { get; private set; }
        public bool Pressing { get; private set; }
        public bool Completed { get; private set; }
        public double Maximum { get; private set; }
        public InputThresholdCapture(int keyIndex, double rest, double bottom, double? initial)
        {
            if (keyIndex < 0 || keyIndex > 255 || !Finite(rest) || !Finite(bottom) || rest < 0 || bottom <= rest || bottom > 65535)
                throw new ArgumentOutOfRangeException("rest");
            this.keyIndex = keyIndex; this.rest = rest;
            releaseLevel = rest + Math.Max(1, Math.Min(4, (bottom - rest) * .005));
            Maximum = rest;
            WaitingForRelease = initial.HasValue && Finite(initial.Value) && initial.Value > releaseLevel;
        }
        public void Feed(int index, int raw)
        {
            if (index != keyIndex || Completed || raw < 0 || raw > 65535) return;
            if (WaitingForRelease)
            { if (raw <= releaseLevel) { WaitingForRelease = false; Maximum = rest; } return; }
            if (!Pressing)
            { if (raw > releaseLevel) { Pressing = true; Maximum = raw; } return; }
            Maximum = Math.Max(Maximum, raw);
            if (raw <= releaseLevel) { Pressing = false; Completed = Maximum > rest; }
        }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
