using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Tk75.Mapping;
using Tk75.Output;

// OPTIONAL REAL DEVICE TEST. This is never called by the offline test runner.
// It exercises actual helper processes and ordinary cleanup, without deliberately
// crashing/killing a host. Native HID/XInput packet acceptance is a separate test.
public static class LiveMixedOutputHarness
{
    sealed class Slot
    {
        public readonly string Name;
        public readonly IsolatedOutput Output;
        public readonly ControllerFrame Frame;
        public readonly ManualResetEvent Stop = new ManualResetEvent(false);
        public Thread Pump;
        public volatile Exception PumpError;
        public int? Pid;
        public bool Connected, Removed;
        public long QuitMilliseconds;
        public Slot(string helper, string name, string arguments, ControllerFrame frame)
        {
            Name = name; Frame = frame;
            Output = new IsolatedOutput(helper, arguments, 15000, 250, 3500, 3500);
        }
        public void Start()
        {
            try { Output.Connect(); }
            finally { Pid = Output.LastHostProcessId; }
            Connected = true;
            Pump = new Thread(delegate() {
                try
                {
                    do { Output.Submit(Frame); } while (!Stop.WaitOne(30));
                }
                catch (Exception ex) { PumpError = ex; }
            }) { IsBackground = true, Name = "Live acceptance " + Name };
            Pump.Start();
        }
    }
    static int checks;
    static void Check(bool value, string reason)
    { checks++; if (!value) throw new InvalidOperationException(reason); }
    static void RefuseRunningMapper(string helper)
    {
        string helperName = Path.GetFileNameWithoutExtension(helper);
        foreach (Process process in Process.GetProcesses())
        using (process)
        {
            string name;
            try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
            if (name.Equals("AnalogKeyMapper", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Tk75Monitor", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ViiperOutputHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(helperName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Close the mapper and its monitor/output helpers before this explicit live test. Running: " + name);
        }
    }
    static Dictionary<Slot, long> Acknowledgements(List<Slot> slots)
    {
        var values = new Dictionary<Slot, long>();
        foreach (Slot slot in slots) if (slot.Connected && !slot.Removed) values.Add(slot, slot.Output.AcknowledgedCommands);
        return values;
    }
    static void RequireProgress(Dictionary<Slot, long> before, string phase)
    {
        var deadline = Stopwatch.StartNew();
        foreach (var pair in before)
        {
            Slot slot = pair.Key;
            while (slot.PumpError == null && slot.Output.IsConnected && slot.Output.AcknowledgedCommands <= pair.Value && deadline.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);
            Check(slot.PumpError == null, slot.Name + " pump failed during " + phase + ": " + slot.PumpError);
            Check(slot.Output.IsConnected && slot.Output.HostProcessId == slot.Pid, slot.Name + " lost its own helper during " + phase);
            Check(slot.Output.AcknowledgedCommands > pair.Value, slot.Name + " received no new watchdog/frame ACK during " + phase);
        }
    }
    static bool Exited(int? pid)
    {
        if (!pid.HasValue) return true;
        try { using (var process = Process.GetProcessById(pid.Value)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    static void Remember(List<string> errors, string text)
    { lock (errors) errors.Add(text); }
    sealed class ResourceSample
    {
        public double CpuSeconds, AtSeconds;
        public long WorkingSet, PrivateMemory;
    }
    static ResourceSample ReadResources(List<Slot> slots, Stopwatch clock)
    {
        double before = clock.Elapsed.TotalSeconds;
        var sample = new ResourceSample();
        foreach (Slot slot in slots)
        {
            if (!slot.Connected || slot.Removed || !slot.Pid.HasValue)
                throw new InvalidOperationException("Resource measurement requires every requested helper to be connected.");
            using (Process process = Process.GetProcessById(slot.Pid.Value))
            {
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException(slot.Name + " exited during resource measurement.");
                sample.CpuSeconds += process.TotalProcessorTime.TotalSeconds;
                sample.WorkingSet += process.WorkingSet64;
                sample.PrivateMemory += process.PrivateMemorySize64;
            }
        }
        // Each process is sampled in sequence. Use the midpoint of this short
        // sampling window as the timestamp for the summed process counters.
        sample.AtSeconds = (before + clock.Elapsed.TotalSeconds) * .5;
        return sample;
    }
    static void MeasureResources(List<Slot> slots, int xboxCount, int dualSenseCount)
    {
        var clock = Stopwatch.StartNew();
        ResourceSample first = ReadResources(slots, clock), last = first;
        long peakWorkingSet = first.WorkingSet, peakPrivateMemory = first.PrivateMemory;
        Dictionary<Slot, long> startAcks = Acknowledgements(slots), previousAcks = startAcks;
        do
        {
            Thread.Sleep(1000);
            RequireProgress(previousAcks, "10-second resource measurement");
            last = ReadResources(slots, clock);
            peakWorkingSet = Math.Max(peakWorkingSet, last.WorkingSet);
            peakPrivateMemory = Math.Max(peakPrivateMemory, last.PrivateMemory);
            previousAcks = Acknowledgements(slots);
        } while (last.AtSeconds - first.AtSeconds < 10);
        double elapsed = last.AtSeconds - first.AtSeconds;
        double cpuSeconds = last.CpuSeconds - first.CpuSeconds;
        Check(cpuSeconds >= 0 && elapsed >= 10, "Resource counters and measurement duration must remain valid.");
        long acknowledgements = 0;
        foreach (var pair in startAcks) acknowledgements += previousAcks[pair.Key] - pair.Value;
        Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
            "METRICS: Xbox={0}; DualSense={1}; helpers={2}; wallSeconds={3:F3}; helperCpuSeconds={4:F3}; helperCpuCoreEquivalent={5:F4}; workingSetBytes={6}; privateMemoryBytes={7}; peakSampledWorkingSetBytes={8}; peakSampledPrivateMemoryBytes={9}; ACKs={10}",
            xboxCount, dualSenseCount, slots.Count, elapsed, cpuSeconds, cpuSeconds / elapsed,
            last.WorkingSet, last.PrivateMemory, peakWorkingSet, peakPrivateMemory, acknowledgements));
        Console.WriteLine("METRICS SCOPE: sums of owned helper processes only. CPU core equivalent is CPU seconds / wall seconds, not percent of the whole CPU. Memory sums may count shared pages more than once; peaks are sampled. Mapper/pump process and driver-wide costs are excluded.");
    }
    static long Phase(List<Slot> slots, Action<Slot> action, string name, List<string> errors)
    {
        var clock = Stopwatch.StartNew();
        var threads = new List<Thread>();
        foreach (Slot slot in slots)
        {
            Slot current = slot;
            var thread = new Thread(delegate() {
                try { action(current); }
                catch (Exception ex) { Remember(errors, current.Name + " " + name + ": " + ex.Message); }
            }) { IsBackground = true, Name = "Live acceptance " + name };
            thread.Start(); threads.Add(thread);
        }
        foreach (Thread thread in threads)
        {
            int remaining = (int)Math.Max(0, 10000 - clock.ElapsedMilliseconds);
            if (!thread.Join(remaining)) Remember(errors, name + " did not finish within the test deadline");
        }
        return clock.ElapsedMilliseconds;
    }
    static void Remove(List<Slot> slots, List<string> errors)
    {
        var removing = new List<Slot>();
        foreach (Slot slot in slots) if (!slot.Removed) { slot.Stop.Set(); removing.Add(slot); }
        // Request every pump to stop before waiting for any one pending command.
        var clock = Stopwatch.StartNew();
        foreach (Slot slot in removing)
        {
            int remaining = (int)Math.Max(0, 5000 - clock.ElapsedMilliseconds);
            if (slot.Pump != null && !slot.Pump.Join(remaining)) Remember(errors, slot.Name + " pump did not stop within the test deadline");
            if (slot.PumpError != null) Remember(errors, slot.Name + " pump: " + slot.PumpError.Message);
        }
        // Keep slow removal separate from the neutral phase for all active peers.
        long neutralMilliseconds = Phase(removing, delegate(Slot slot) {
            if (slot.Connected)
            {
                long before = slot.Output.AcknowledgedCommands;
                slot.Output.Neutral();
                if (!slot.Output.IsConnected || slot.Output.AcknowledgedCommands != before + 1)
                    throw new InvalidOperationException("Neutral was not acknowledged by the connected helper");
            }
        }, "neutral", errors);
        long disposalMilliseconds = Phase(removing, delegate(Slot slot) {
            long before = slot.Output.AcknowledgedCommands;
            bool expectedQuit = slot.Output.IsConnected;
            var quitClock = Stopwatch.StartNew();
            slot.Output.Dispose();
            slot.QuitMilliseconds = quitClock.ElapsedMilliseconds;
            if (expectedQuit && slot.Output.AcknowledgedCommands != before + 1)
                Remember(errors, slot.Name + " normal QUIT was not acknowledged: " + slot.Output.Status);
            if (slot.Output.IsConnected || slot.Output.HostProcessId.HasValue)
                Remember(errors, slot.Name + " still exposes a live process after disposal");
            var deadline = Stopwatch.StartNew();
            while (!Exited(slot.Pid) && deadline.ElapsedMilliseconds < 1500) Thread.Sleep(5);
            if (!Exited(slot.Pid)) Remember(errors, slot.Name + " own PID did not exit: " + slot.Pid);
            slot.Removed = true;
        }, "dispose", errors);
        if (removing.Count != 0)
        {
            long slowestQuit = 0;
            foreach (Slot slot in removing) slowestQuit = Math.Max(slowestQuit, slot.QuitMilliseconds);
            Console.WriteLine("CLEANUP: helpers=" + removing.Count + "; neutralPhaseMs=" + neutralMilliseconds + "; disposePhaseMs=" + disposalMilliseconds + "; slowestDisposeMs=" + slowestQuit + "; configuredQuitTimeoutMs=3500");
        }
    }
    public static string Run(string helperPath)
    { return Run(helperPath, 2, 2); }
    public static string Run(string helperPath, int xboxCount, int dualSenseCount)
    {
        checks = 0;
        if (xboxCount < 1 || xboxCount > 4) throw new ArgumentOutOfRangeException("xboxCount", "Choose 1 to 4 Xbox helpers.");
        if (dualSenseCount < 1 || dualSenseCount > 28) throw new ArgumentOutOfRangeException("dualSenseCount", "Choose 1 to 28 DualSense helpers.");
        if (xboxCount + dualSenseCount > 32) throw new ArgumentException("At most 32 output helpers may be requested.");
        string helper = Path.GetFullPath(helperPath);
        Check(File.Exists(helper), "Output helper not found: " + helper);
        RefuseRunningMapper(helper);
        var slots = new List<Slot>(); var errors = new List<string>();
        Exception failure = null;
        try
        {
            Console.WriteLine("CONFIG: Xbox=" + xboxCount + "; DualSense=" + dualSenseCount + "; total=" + (xboxCount + dualSenseCount) + "; pumpIntervalMs=30; measurementSeconds=10");
            for (int i = 0; i < Math.Max(xboxCount, dualSenseCount); i++)
            {
                if (i < xboxCount) slots.Add(new Slot(helper, "Xbox " + (i + 1), "--xbox-runtime-host", DistinctFrame(slots.Count)));
                if (i < dualSenseCount) slots.Add(new Slot(helper, "DualSense " + (i + 1), "--dualsense-host", DistinctFrame(slots.Count)));
            }
            var pids = new HashSet<int>();
            foreach (Slot slot in slots)
            {
                Dictionary<Slot, long> before = Acknowledgements(slots);
                slot.Start();
                Check(slot.Pid.HasValue && pids.Add(slot.Pid.Value), "Each helper must have a unique owned PID");
                Check(slot.Output.IsConnected, slot.Name + " CONNECT was not confirmed");
                RequireProgress(before, slot.Name + " connection");
                Console.WriteLine("CONNECTED: " + slot.Name + "; PID=" + slot.Pid + "; ACKs=" + slot.Output.AcknowledgedCommands);
            }
            MeasureResources(slots, xboxCount, dualSenseCount);
            foreach (Slot removed in new[] { slots[1], slots[0] })
            {
                var peers = Acknowledgements(slots); peers.Remove(removed);
                Remove(new List<Slot> { removed }, errors);
                RequireProgress(peers, removed.Name + " normal removal");
                Console.WriteLine("REMOVED: " + removed.Name + "; acknowledged surviving helper pumps=" + peers.Count);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Remove(slots, errors);
            foreach (Slot slot in slots) if (slot.Pump == null || !slot.Pump.IsAlive) slot.Stop.Dispose();
        }
        if (failure != null) errors.Insert(0, failure.Message);
        if (errors.Count != 0) throw new InvalidOperationException("Live mixed-helper acceptance failed:\n" + String.Join("\n", errors.ToArray()));
        return "PASS: " + checks + " live mixed-helper checks; Xbox=" + xboxCount + "; DualSense=" + dualSenseCount + "; total=" + slots.Count + "; independent watchdog ACKs, 10-second helper resource measurement, peer removal, parallel neutralization, normal QUIT and own-PID exit. HID/XInput packet contents are verified separately.";
    }
    static ControllerFrame DistinctFrame(int index)
    {
        // A distinct trigger level survives 8-bit conversion for every slot.
        double fraction = (index + 1) / 33.0;
        return new ControllerFrame { Buttons = (ushort)(0x1000 << (index % 4)),
            LeftX = index % 2 == 0 ? fraction : -fraction, RightY = fraction,
            LeftTrigger = fraction, RightTrigger = 1 - fraction };
    }
}
