using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tk75.Diagnostics
{
    public struct KeyStateSnapshot
    {
        public int KeyIndex;
        public string KeyLabel;
        public int RawValue;
        public double AgeMs;
        public bool Stale;
        public bool Known;
    }

    // Publish, Snapshot and Invalidate share fixed per-index state under a short lock.
    // Display belongs to the console/main thread; formatting and output never hold it.
    public sealed class RawLiveView
    {
        readonly object gate = new object();
        readonly int[] values = new int[256];
        readonly double[] times = new double[256];
        readonly bool[] seen = new bool[256];
        readonly bool[] known = new bool[256];
        readonly string[] labels = new string[256];
        readonly KeyStateSnapshot[] displayed = new KeyStateSnapshot[256];
        readonly bool[] wasDisplayed = new bool[256];
        string invalidationReason;
        string displayedReason;

        public string InvalidationReason { get { lock (gate) { return invalidationReason; } } }

        static void ValidateTime(double elapsed)
        {
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0)
                throw new ArgumentOutOfRangeException("elapsed", "Elapsed time must be finite and nonnegative.");
        }

        public void Publish(TravelSample sample, double elapsed)
        {
            if ((uint)sample.KeyIndex >= 256) throw new ArgumentOutOfRangeException("sample", "Key index must be 0..255.");
            ValidateTime(elapsed);
            lock (gate)
            {
                int index = sample.KeyIndex;
                values[index] = sample.RawValue;
                times[index] = elapsed;
                labels[index] = sample.KeyLabel;
                seen[index] = true;
                known[index] = true;
            }
        }

        public void Invalidate(string reason)
        {
            lock (gate)
            {
                invalidationReason = string.IsNullOrWhiteSpace(reason) ? "Eingabestatus ungueltig." : reason;
                for (int index = 0; index < known.Length; index++) known[index] = false;
            }
        }

        public KeyStateSnapshot[] Snapshot(double elapsedMs)
        {
            string ignored;
            return CopySnapshot(elapsedMs, out ignored);
        }

        // Mapping needs only known raw values, not the labels/ages in a UI
        // snapshot. Fill its owned buffer directly without a temporary array.
        internal void CopyRawValues(Dictionary<int, double> destination, double elapsedMs, double maxAgeMs, bool retainKnown)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            ValidateTime(elapsedMs); ValidateTime(maxAgeMs);
            lock (gate)
            {
                destination.Clear();
                for (int index = 0; index < known.Length; index++)
                    if (known[index] && (retainKnown || values[index] == 0 || Math.Max(0, elapsedMs - times[index]) <= maxAgeMs))
                        destination.Add(index, values[index]);
            }
        }

        KeyStateSnapshot[] CopySnapshot(double elapsedMs, out string reason)
        {
            ValidateTime(elapsedMs);
            lock (gate)
            {
                reason = invalidationReason;
                int count = 0;
                for (int index = 0; index < seen.Length; index++) if (seen[index]) count++;
                KeyStateSnapshot[] snapshot = new KeyStateSnapshot[count];
                int position = 0;
                for (int index = 0; index < seen.Length; index++)
                {
                    if (!seen[index]) continue;
                    double age = Math.Max(0, elapsedMs - times[index]);
                    snapshot[position++] = new KeyStateSnapshot
                    {
                        KeyIndex = index, KeyLabel = labels[index], RawValue = values[index],
                        AgeMs = age, Stale = age > 500, Known = known[index]
                    };
                }
                return snapshot;
            }
        }

        public void Display(double elapsed)
        {
            string reason;
            KeyStateSnapshot[] snapshot = CopySnapshot(elapsed, out reason);
            StringBuilder line = new StringBuilder();
            if (reason != displayedReason && reason != null) line.Append("Status: ").Append(reason).Append("   ");
            foreach (KeyStateSnapshot state in snapshot)
            {
                KeyStateSnapshot old = displayed[state.KeyIndex];
                if (wasDisplayed[state.KeyIndex] && old.RawValue == state.RawValue && old.Known == state.Known &&
                    old.Stale == state.Stale && old.KeyLabel == state.KeyLabel) continue;
                string indexText = state.KeyIndex.ToString(CultureInfo.InvariantCulture);
                line.Append(string.IsNullOrEmpty(state.KeyLabel) ? "Index " + indexText : state.KeyLabel);
                line.Append(" [").Append(indexText).Append("]: ");
                if (!state.Known) line.Append("unbekannt");
                else line.Append(state.RawValue.ToString(CultureInfo.InvariantCulture)).Append(state.Stale ? " [letzter Wert]" : " [neu]");
                line.Append("   ");
            }
            if (line.Length == 0) return;
            Console.WriteLine(line.ToString().TrimEnd());
            // Output succeeded: retain only display state, independent of reader state.
            displayedReason = reason;
            foreach (KeyStateSnapshot state in snapshot)
            {
                displayed[state.KeyIndex] = state;
                wasDisplayed[state.KeyIndex] = true;
            }
        }
    }
}
