using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Tk75.App;
using Tk75.Diagnostics;

public static class ReaderSessionHarness
{
    class FakeSource : IReportSource, IIdentifiedReportSource
    {
        public uint? Model;
        public uint? DeviceModelId { get { return Model; } }
        readonly object gate = new object();
        readonly Queue<object> input = new Queue<object>();
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        public readonly ManualResetEvent Disposed = new ManualResetEvent(false);
        public int LastTimeout, TimeoutCount;
        public bool ThrowOnDispose;
        public void Push(object value) { lock (gate) { input.Enqueue(value); } ready.Set(); }
        public byte[] Read(int timeoutMs)
        {
            LastTimeout = timeoutMs;
            object item = null;
            lock (gate) { if (input.Count != 0) item = input.Dequeue(); }
            if (item == null)
            {
                ready.WaitOne(timeoutMs);
                lock (gate) { if (input.Count != 0) item = input.Dequeue(); }
            }
            if (item == null) { Interlocked.Increment(ref TimeoutCount); return null; }
            Exception error = item as Exception;
            if (error != null) throw error;
            return (byte[])item;
        }
        public void Dispose()
        {
            ready.Dispose(); Disposed.Set();
            if (ThrowOnDispose) throw new IOException("synthetic dispose failure");
        }
    }
    sealed class EventSource : FakeSource, IEventStateReportSource
    {
        public EventDeviceState State = EventDeviceState.Live;
        public EventDeviceState DeviceState { get { return State; } }
    }
    static int assertions;
    static void Check(bool valid, string message) { assertions++; if (!valid) throw new Exception("FAIL: " + message); }
    static void Until(Func<bool> condition, string message)
    {
        Stopwatch limit = Stopwatch.StartNew();
        while (!condition() && limit.ElapsedMilliseconds < 2000) Thread.Sleep(2);
        Check(condition(), message);
    }
    static byte[] Report(int index, int raw)
    { byte[] report = new byte[32]; report[0] = 5; report[1] = 27; report[2] = (byte)(raw & 255); report[3] = (byte)(raw >> 8); report[4] = (byte)index; return report; }
    static CollectionInfo Device()
    {
        CollectionInfo device = new CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "Synthetic TK75", version = 0x0403, usagePage = 65535, usage = 1, inputReportLength = 32 };
        device.reportCapabilities.Add(new Dictionary<string, object> { { "reportType", "input" }, { "kind", "value" }, { "reportId", 5 }, { "usagePage", 65535 }, { "bitSize", 8 }, { "reportCount", 31 } });
        return device;
    }
    public static string Run()
    {
        assertions = 0;
        FakeSource first = new FakeSource { Model = 3591 }, second = new FakeSource();
        Queue<FakeSource> sources = new Queue<FakeSource>(); sources.Enqueue(first); sources.Enqueue(second);
        int count = 0, faults = 0;
        ReaderSession session = new ReaderSession(Device(), new RongYuanTravel32(), delegate { return sources.Dequeue(); });
        var reusable = new Dictionary<int, double> { { 255, 999 } };
        try
        {
            session.Sample += delegate { throw new InvalidOperationException("synthetic subscriber error"); };
            session.Sample += delegate { Interlocked.Increment(ref count); session.GetSnapshot(); };
            session.Fault += delegate { Interlocked.Increment(ref faults); };
            Check(!session.IsReading && session.GetRawSnapshot(500).Count == 0, "no live values before explicit start");
            session.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 0, "a reusable buffer is cleared before reading starts");
            Check(session.Fingerprint.Length == 64, "logical fingerprint exposed");
            session.Start();
            Until(delegate { return session.IsReading; }, "first fake source opened");
            Check(!session.HasReceivedSamples && session.SampleCount == 0, "open transport alone does not claim received pressure values");
            Check(session.DeviceModelId == 3591, "identified ISO model propagated");
            first.Push(Report(14, 300)); first.Push(Report(9, 200)); first.Push(Report(9, 0));
            Until(delegate { return Volatile.Read(ref count) == 3; }, "throwing subscriber isolated from later subscribers and future samples");
            Check(session.HasReceivedSamples && session.SampleCount == 3, "valid report count and receiving state");
            Dictionary<int, double> raw = session.GetRawSnapshot(500);
            Check(raw.Count == 2 && raw[14] == 300 && raw[9] == 0, "individual release retains independent held key");
            session.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 2 && reusable[14] == 300 && reusable[9] == 0, "reusable raw snapshot matches detached values");
            reusable[14] = 999; raw[9] = 999;
            session.CopyRawSnapshot(500, reusable);
            Check(reusable[14] == 300 && reusable[9] == 0, "consumer edits cannot change reader-owned values");
            Thread.Sleep(30);
            raw = session.GetRawSnapshot(1);
            Check(!raw.ContainsKey(14) && raw.ContainsKey(9) && raw[9] == 0, "expired positives omitted; confirmed zero retained while connected");
            session.CopyRawSnapshot(1, reusable);
            Check(reusable.Count == 1 && reusable[9] == 0, "buffer reuse removes an expired positive without fabricating a release");
            KeyStateSnapshot[] diagnostic = session.GetSnapshot();
            Check(diagnostic[1].RawValue == 300 && diagnostic[1].Known, "watchdog does not fabricate diagnostic zero");
            KeyStateSnapshot[] visible = session.GetUiSnapshot(1);
            Check(visible[1].RawValue == 300 && visible[1].Known && visible[1].Stale, "UI marks an expired passive positive stale without inventing zero");
            Check(visible[0].RawValue == 0 && visible[0].Known && !visible[0].Stale, "UI retains a confirmed passive release exactly as mapping does");
            Until(delegate { return Volatile.Read(ref first.TimeoutCount) > 0; }, "read timeout observed");
            Check(session.IsReading && faults == 0, "ordinary timeout does not disconnect or fault");
            Check(first.LastTimeout == 100, "bounded 100ms source read contract");
            Stopwatch stopping = Stopwatch.StartNew(); session.Stop();
            Check(stopping.ElapsedMilliseconds < 1000, "stop does not block UI indefinitely");
            Check(first.Disposed.WaitOne(1000), "first source drained/disposed on its worker");
            Check(!session.IsReading && session.GetRawSnapshot(500).Count == 0, "stopped session has no active values");
            session.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 0, "stop clears an already populated reusable buffer");
            foreach (KeyStateSnapshot state in session.GetSnapshot()) Check(!state.Known, "stop invalidates every previous value");
            foreach (KeyStateSnapshot state in session.GetUiSnapshot(500)) Check(!state.Known && state.Stale, "stopped input cannot remain live in the UI");
            // Explicit restart uses a new source. No code scans or reconnects itself.
            session.Start();
            Until(delegate { return session.IsReading; }, "explicit restart opens next source");
            Check(!session.HasReceivedSamples && session.SampleCount == 0 && !session.DeviceModelId.HasValue, "restart clears sample readiness and old device identity");
            Check(session.GetRawSnapshot(500).Count == 0, "restart cannot rearm old values");
            second.Push(Report(14, 7));
            Until(delegate { return Volatile.Read(ref count) == 4; }, "fresh report after explicit restart");
            raw = session.GetRawSnapshot(500);
            Check(raw.Count == 1 && raw[14] == 7, "only republished key becomes current after restart");
            session.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 1 && reusable[14] == 7, "reused buffer cannot restore an earlier reader's released or held keys");
        }
        finally { session.Dispose(); }
        Check(second.Disposed.WaitOne(1000), "second source disposed");

        EventSource events = new EventSource { Model = 3591 };
        using (ReaderSession held = new ReaderSession(Device(), new RongYuanTravel32(), delegate { return events; }))
        {
            held.Start(); Until(delegate { return held.IsReading; }, "event source opened");
            events.Push(Report(14, 385)); events.Push(Report(9, 250)); events.Push(Report(9, 0));
            Until(delegate { return held.SampleCount == 3; }, "event states observed independently");
            Thread.Sleep(30);
            var raw = held.GetRawSnapshot(1);
            Check(raw.Count == 2 && raw[14] == 385 && raw[9] == 0, "live device lease retains held state past key TTL and independent release");
            Check(!raw.ContainsKey(21), "device lease never creates an unobserved key");
            Check(held.GetSnapshot()[1].AgeMs > 1, "retaining held state does not forge a new sample time");
            KeyStateSnapshot[] visible = held.GetUiSnapshot(1);
            Check(visible.Length == 2 && visible[1].KeyIndex == 14 && visible[1].RawValue == 385 && visible[1].Known && !visible[1].Stale,
                "UI continues displaying a verified held event key after the per-key TTL");
            Check(visible[1].AgeMs > 1 && visible[0].RawValue == 0 && !visible[0].Stale,
                "UI preserves real sample age and an independent verified release");
            held.CopyRawSnapshot(1, reusable);
            Check(reusable.Count == 2 && reusable[14] == 385 && reusable[9] == 0, "reuse preserves verified event states past the per-key TTL");
            events.State = EventDeviceState.Waiting;
            foreach (KeyStateSnapshot state in held.GetUiSnapshot(500)) Check(state.Stale, "waiting device evidence invalidates held and released UI states");
            held.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 0, "waiting device evidence clears a previously live buffer");
            events.State = EventDeviceState.Live;
            Check(!held.GetUiSnapshot(1)[1].Stale, "UI resumes a still-valid session only after live device evidence");
            held.CopyRawSnapshot(1, reusable);
            Check(reusable.Count == 2, "a still-valid session resumes from waiting only with live evidence");
            events.State = EventDeviceState.Expired;
            foreach (KeyStateSnapshot state in held.GetUiSnapshot(500)) Check(state.Stale, "UI notices device lease expiry before mapping requests another snapshot");
            Check(held.GetRawSnapshot(500).Count == 0, "device lease expiry removes held and released active states");
            held.CopyRawSnapshot(500, reusable);
            Check(reusable.Count == 0, "device lease expiry also clears a reusable buffer");
            events.State = EventDeviceState.Live;
            events.Push(Report(14, 384));
            Until(delegate { return held.SampleCount == 4; }, "late event arrived after expired lease");
            Check(held.GetRawSnapshot(500).Count == 0, "late evidence cannot resurrect expired session state");
            foreach (KeyStateSnapshot state in held.GetUiSnapshot(500)) Check(state.Stale, "late evidence cannot resurrect an expired UI session either");
        }

        foreach (bool malformed in new[] { false, true })
        {
            FakeSource broken = new FakeSource { ThrowOnDispose = true };
            ReaderSession failing = new ReaderSession(Device(), new RongYuanTravel32(), delegate { return broken; });
            int received = 0, reported = 0;
            try
            {
                failing.Sample += delegate { Interlocked.Increment(ref received); };
                failing.Fault += delegate { throw new InvalidOperationException("synthetic fault subscriber error"); };
                failing.Fault += delegate { failing.GetRawSnapshot(500); Interlocked.Increment(ref reported); };
                failing.Start(); Until(delegate { return failing.IsReading; }, "failure test source opened");
                broken.Push(Report(14, 250)); Until(delegate { return Volatile.Read(ref received) == 1; }, "known value established before failure");
                if (malformed) { byte[] wrong = Report(14, 300); wrong[31] = 1; broken.Push(wrong); }
                else broken.Push(new IOException("synthetic disconnect"));
                Until(delegate { return Volatile.Read(ref reported) == 1; }, "read/format failure reaches isolated fault subscriber once");
                Check(broken.Disposed.WaitOne(1000), "failed source closed");
                Thread.Sleep(10);
                Check(reported == 1, "secondary dispose error does not duplicate fault");
                Check(!failing.IsReading && failing.GetRawSnapshot(500).Count == 0, "failure immediately removes active values");
                Check(!failing.GetSnapshot()[0].Known && failing.GetSnapshot()[0].RawValue == 250, "failure preserves diagnostic value as unknown");
                Check(!string.IsNullOrWhiteSpace(failing.Status), "failure status available");
            }
            finally { failing.Dispose(); }
        }
        ReaderSession unopened = new ReaderSession(Device(), new RongYuanTravel32(), delegate { throw new IOException("synthetic open failure"); });
        int openFaults = 0;
        try
        {
            unopened.Fault += delegate { Interlocked.Increment(ref openFaults); };
            unopened.Start(); Until(delegate { return Volatile.Read(ref openFaults) == 1; }, "open failure emits one fault");
            Check(!unopened.IsReading && unopened.GetRawSnapshot(500).Count == 0, "open failure cannot produce active values");
        }
        finally { unopened.Dispose(); }
        return "PASS: " + assertions + " assertions; fake sources only; timeout, independent keys, watchdog, stop/restart, malformed/disconnected input and isolated subscribers; no hardware calls.";
    }
}
