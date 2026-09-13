using System;
using System.Collections.Generic;
using System.IO;
using Tk75.Diagnostics;

public static class RgbInputSettlingHarness
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    sealed class ClockInput
    {
        public long Now;
        public int Reads, AliveCalls;
        public readonly Queue<long> Arrivals = new Queue<long>();
        public readonly List<int> Waits = new List<int>();
        public readonly List<long> ForwardedAt = new List<long>();
        public readonly List<byte> Forwarded = new List<byte>();
        byte next;
        public byte[] Read(int timeout)
        {
            Reads++; Waits.Add(timeout);
            if (Arrivals.Count != 0 && Arrivals.Peek() <= Now + timeout)
            { Now = Math.Max(Now, Arrivals.Dequeue()); return new[] { next++ }; }
            Now += timeout; return null;
        }
        public void Forward(byte[] report) { ForwardedAt.Add(Now); Forwarded.Add(report[0]); }
        public void Alive() { AliveCalls++; }
        public void Run(int duration) { RgbInputSettling.Wait(duration, 1000, delegate { return Now; }, Read, Forward, Alive); }
    }
    public static int Run()
    {
        checks = 0;
        foreach (int duration in new[] { 20, 100 })
        {
            var quiet = new ClockInput { Now = 500 };
            quiet.Run(duration);
            Check(quiet.Now == 500 + duration && quiet.Reads == duration / 20 && quiet.Forwarded.Count == 0,
                "Quiet input honors the complete settling deadline with blocking reads and no fabricated reports.");
            Check(quiet.Waits.TrueForAll(delegate(int wait) { return wait == 20; }), "Quiet waits check cancellation every 20ms without a busy loop.");
            var busy = new ClockInput();
            for (int time = 1; time <= duration + 5; time++) busy.Arrivals.Enqueue(time);
            busy.Run(duration);
            Check(busy.Now == duration && busy.Forwarded.Count == duration && busy.Arrivals.Count == 5,
                "Continuous input neither shortens nor extends the settling deadline and leaves future events unread.");
            for (int i = 0; i < duration; i++)
                Check(busy.Forwarded[i] == i && busy.ForwardedAt[i] == i + 1,
                    "Every completed report is forwarded once in order immediately at its arrival, not after a 20ms batch.");
        }
        var mixed = new ClockInput();
        foreach (int time in new[] { 2, 3, 25, 67, 100, 101 }) mixed.Arrivals.Enqueue(time);
        mixed.Run(100);
        Check(mixed.Now == 100 && mixed.Forwarded.Count == 5 && mixed.Arrivals.Count == 1,
            "Interleaved quiet/busy periods honor one fixed deadline and include the final completed report.");
        Check(mixed.Waits.TrueForAll(delegate(int wait) { return wait >= 1 && wait <= 20; }), "Every wait is bounded and positive.");
        var cancel = new ClockInput(); bool cancelled = false;
        try
        {
            RgbInputSettling.Wait(100, 1000, delegate { return cancel.Now; }, cancel.Read, cancel.Forward,
                delegate { if (cancel.Now >= 40) throw new OperationCanceledException(); });
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && cancel.Now == 40 && cancel.Reads == 2, "A cancelled parent stops during settling instead of waiting for the full deadline.");
        bool stopped = false; int reads = 0;
        try { RgbInputSettling.Wait(20, 1000, delegate { return 0; }, delegate(int wait) { reads++; return null; }, delegate { }, delegate { throw new OperationCanceledException(); }); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && reads == 0, "An already cancelled session cannot consume another input report.");
        var failure = new ClockInput(); bool failed = false;
        try { RgbInputSettling.Wait(100, 1000, delegate { return failure.Now; }, delegate { throw new IOException("Read failed."); }, failure.Forward, failure.Alive); }
        catch (IOException) { failed = true; }
        Check(failed && failure.Forwarded.Count == 0, "A failed input read propagates immediately without synthesized input.");
        long backwards = 3; bool rejected = false;
        try { RgbInputSettling.Wait(20, 1000, delegate { return backwards--; }, delegate { throw new Exception("No read after invalid clock."); }, delegate { }, delegate { }); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "A backwards clock cannot silently finish or prolong a settling wait.");
        var overshoot = new ClockInput();
        RgbInputSettling.Wait(20, 1000, delegate { return overshoot.Now; }, delegate(int wait) { overshoot.Reads++; overshoot.Now += wait + 3; return new byte[] { 9 }; }, overshoot.Forward, overshoot.Alive);
        Check(overshoot.Now == 23 && overshoot.Reads == 1 && overshoot.Forwarded.Count == 1,
            "Scheduler overshoot never discards an already completed report or starts an extra wait.");
        long ticks = 999; int highResolutionReports = 0;
        RgbInputSettling.Wait(20, 1000000, delegate { return ticks; }, delegate(int wait) { ticks += 1000; return new byte[] { 1 }; },
            delegate { highResolutionReports++; }, delegate { });
        Check(ticks == 20999 && highResolutionReports == 20,
            "Starting just before an absolute millisecond boundary still waits the complete twenty milliseconds under continuous input.");
        return checks;
    }
}
