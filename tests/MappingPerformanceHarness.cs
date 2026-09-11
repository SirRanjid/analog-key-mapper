using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Tk75.App;
using Tk75.Mapping;
using Tk75.Output;

// This benchmark measures real MappingSession workers with synthetic input and
// output. It does not load a keyboard reader, launch a helper or access a driver.
namespace Tk75.App
{
    public sealed class WorkspaceStore { public void Event(string text) { } }
    public sealed class ReaderSession
    {
        readonly object gate = new object();
        public bool IsReading { get { return true; } }
        public long Published;
        public int Generation, Copies;
        public double Value;
        public void Publish(double value)
        { lock (gate) { Value = value; Generation++; Published = Stopwatch.GetTimestamp(); } }
        public Dictionary<int, double> GetRawSnapshot(double age)
        { var result = new Dictionary<int, double>(); CopyRawSnapshot(age, result); return result; }
        internal void CopyRawSnapshot(double age, Dictionary<int, double> target)
        { lock (gate) { Interlocked.Increment(ref Copies); target.Clear(); target.Add(1, Value); } }
        public bool Observe(double normalized, ref int seen, out double milliseconds)
        {
            lock (gate)
            {
                milliseconds = 0;
                if (Generation == 0 || Generation == seen || Math.Abs(normalized - Value / 100.0) > .000001) return false;
                seen = Generation;
                milliseconds = (Stopwatch.GetTimestamp() - Published) * 1000.0 / Stopwatch.Frequency;
                return true;
            }
        }
    }
}

public static class MappingPerformanceHarness
{
    public sealed class Result
    {
        public int Controllers, InputChanges, OutputSamples, InputCopies, Gen0Collections, Gen1Collections, Gen2Collections;
        public bool Connected, SelectedPreview;
        public double Seconds, CpuCoreEquivalent, ProcessPrivateMiB, ProcessWorkingSetMiB;
        public double LatencyP50Milliseconds, LatencyP95Milliseconds, LatencyP99Milliseconds, LatencyMaximumMilliseconds;
        public string Scope = "Synthetic input publication to fake output Submit; excludes keyboard, HID, pipe, helper, driver, game and UI. Memory includes the benchmark host.";
    }
    sealed class Sink : IControllerOutput
    {
        readonly ReaderSession input;
        readonly object gate = new object();
        readonly List<double> samples = new List<double>();
        int observed;
        volatile bool connected;
        public Sink(ReaderSession input) { this.input = input; }
        public bool IsConnected { get { return connected; } }
        public string Status { get { return "Synthetic performance output"; } }
        public void Connect() { connected = true; }
        public void Neutral() { }
        public void Dispose() { connected = false; }
        public void Submit(ControllerFrame frame)
        {
            if (!connected) throw new InvalidOperationException("Synthetic output is disconnected.");
            double elapsed;
            if (input.Observe(frame.LeftX, ref observed, out elapsed)) lock (gate) samples.Add(elapsed);
        }
        public void Reset() { lock (gate) samples.Clear(); }
        public double[] Samples { get { lock (gate) return samples.ToArray(); } }
    }
    static double Percentile(double[] values, double fraction)
    { return values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * fraction) - 1)]; }

    public static Result Run(int count, int milliseconds, bool connected, bool selectedPreview)
    {
        if (count < 1 || count > 32 || milliseconds < 1000 || milliseconds > 60000) throw new ArgumentOutOfRangeException();
        var input = new ReaderSession();
        var sessions = new List<MappingSession>();
        var sinks = new List<Sink>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var sink = new Sink(input); sinks.Add(sink);
                var session = MappingSession.CreateWithControllerFactory(null, kind => sink, () => true, input.GetRawSnapshot);
                sessions.Add(session);
                session.SetPreviewActive(selectedPreview && i == 0);
                session.SetReader(input);
                var profile = new Profile();
                profile.Bindings.Add(new Binding { KeyIndex = 1, Target = OutputTarget.LeftXPositive });
                session.Configure(profile, new Dictionary<int, Calibration> { { 1, new Calibration(0, 100) } });
                if (connected) session.Enable();
            }
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 750)
            { if (connected || selectedPreview) input.Publish((input.Generation & 1) == 0 ? 25 : 75); Thread.Sleep(20); }
            Thread.Sleep(30);
            foreach (Sink sink in sinks) sink.Reset();
            int inputChanges = input.Generation, copies = input.Copies;
            int[] collections = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            using (var process = Process.GetCurrentProcess())
            {
                TimeSpan cpuStart = process.TotalProcessorTime;
                var wall = Stopwatch.StartNew();
                while (wall.ElapsedMilliseconds < milliseconds)
                { if (connected || selectedPreview) input.Publish((input.Generation & 1) == 0 ? 25 : 75); Thread.Sleep(20); }
                double seconds = wall.Elapsed.TotalSeconds;
                process.Refresh();
                double cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / seconds;
                double[] latency = sinks.SelectMany(sink => sink.Samples).OrderBy(value => value).ToArray();
                return new Result {
                    Controllers = count, Connected = connected, SelectedPreview = selectedPreview, Seconds = seconds,
                    CpuCoreEquivalent = cpu, ProcessPrivateMiB = process.PrivateMemorySize64 / 1048576.0,
                    ProcessWorkingSetMiB = process.WorkingSet64 / 1048576.0, InputChanges = input.Generation - inputChanges,
                    InputCopies = input.Copies - copies, OutputSamples = latency.Length,
                    Gen0Collections = GC.CollectionCount(0) - collections[0], Gen1Collections = GC.CollectionCount(1) - collections[1], Gen2Collections = GC.CollectionCount(2) - collections[2],
                    LatencyP50Milliseconds = Percentile(latency, .5), LatencyP95Milliseconds = Percentile(latency, .95),
                    LatencyP99Milliseconds = Percentile(latency, .99), LatencyMaximumMilliseconds = latency.Length == 0 ? 0 : latency[latency.Length - 1]
                };
            }
        }
        finally { foreach (MappingSession session in sessions) session.Dispose(); }
    }
}
