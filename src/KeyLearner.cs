using System;

namespace Tk75.Diagnostics
{
    public enum KeyLearnerState { Preparing, Armed, Pressing, Completed, Ambiguous }

    // User-operated identification only. No timer presses, calibration or range inference.
    // First observed press/release establishes a released baseline; a second confirms it.
    public sealed class KeyLearner
    {
        int releasedCycles;
        double lastElapsed = -1;
        public KeyLearnerState State { get; private set; }
        public int KeyIndex { get; private set; }
        public string Error { get; private set; }
        public int MaxObservedRaw { get; private set; }
        public int ReleasedCycles { get { return releasedCycles; } }

        public KeyLearner() { State = KeyLearnerState.Preparing; KeyIndex = -1; }
        void Reject(string reason) { State = KeyLearnerState.Ambiguous; Error = reason; }

        public void Feed(TravelSample sample, double elapsed)
        {
            if (State == KeyLearnerState.Ambiguous) return;
            if ((uint)sample.KeyIndex >= 256 || sample.RawValue < 0)
            { Reject("Invalid key index or raw value."); return; }
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0 || elapsed < lastElapsed)
            { Reject("Invalid or backward sample time."); return; }
            lastElapsed = elapsed;
            if (sample.RawValue > 0 && KeyIndex != -1 && sample.KeyIndex != KeyIndex)
            { Reject("Another key produced a positive value; repeat learning with one physical key only."); return; }
            if (State == KeyLearnerState.Completed) return;
            if (sample.RawValue > 0)
            {
                if (KeyIndex == -1) KeyIndex = sample.KeyIndex;
                MaxObservedRaw = Math.Max(MaxObservedRaw, sample.RawValue);
                State = KeyLearnerState.Pressing;
                return;
            }
            // Initial zero reports and releases of unrelated keys provide no identity.
            if (sample.KeyIndex != KeyIndex || State != KeyLearnerState.Pressing) return;
            releasedCycles++;
            State = releasedCycles >= 2 ? KeyLearnerState.Completed : KeyLearnerState.Armed;
        }
    }
}
