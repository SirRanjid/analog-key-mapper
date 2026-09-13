using System;
using System.IO;

namespace Tk75.Diagnostics
{
    // Pure deadline coordination. The supplied read waits on the already-owned
    // input completion; null means its timeout elapsed. No polling/sleep thread,
    // no synthetic input and no reduction of the manufacturer's settling time.
    public static class RgbInputSettling
    {
        public static void Wait(int milliseconds, long clockFrequency, Func<long> now, Func<int, byte[]> read,
            Action<byte[]> forward, Action requireAlive)
        {
            if (milliseconds < 1 || milliseconds > 1000) throw new ArgumentOutOfRangeException("milliseconds");
            if (clockFrequency < 1) throw new ArgumentOutOfRangeException("clockFrequency");
            if (now == null) throw new ArgumentNullException("now");
            if (read == null) throw new ArgumentNullException("read");
            if (forward == null) throw new ArgumentNullException("forward");
            if (requireAlive == null) throw new ArgumentNullException("requireAlive");
            // Ceiling in raw clock ticks, without overflowing frequency * ms.
            // Subtracting floored absolute milliseconds could shorten a busy
            // interval by almost 1ms when it begins near a tick boundary.
            long requiredTicks = (clockFrequency / 1000) * milliseconds + ((clockFrequency % 1000) * milliseconds + 999) / 1000;
            long started = now(), previous = started;
            while (true)
            {
                requireAlive();
                long current = now();
                if (current < previous) throw new InvalidDataException("RGB settling requires a monotonic clock.");
                previous = current;
                long remaining = requiredTicks - (current - started);
                if (remaining <= 0) return;
                // Quiet input blocks, and checks parent cancellation at least
                // every 20ms. Busy input wakes immediately without restarting
                // the settling deadline or leaving completed reports buffered.
                int timeout = (int)Math.Max(1, Math.Min(20, Math.Ceiling(remaining * 1000d / clockFrequency)));
                byte[] report = read(timeout);
                requireAlive();
                if (report != null) forward(report);
            }
        }
    }
}
