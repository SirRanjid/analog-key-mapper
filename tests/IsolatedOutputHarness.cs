using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Tk75.Mapping;
using Tk75.Output;

public static class IsolatedOutputHarness
{
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException("FAIL: " + message); }
    static Exception Reject(Action action, string message)
    { try { action(); } catch (Exception ex) { checks++; return ex; } throw new InvalidOperationException("FAIL: " + message); }
    static bool Exited(int? id)
    {
        if (!id.HasValue) return true;
        try { using (var child = Process.GetProcessById(id.Value)) return child.HasExited; }
        catch (ArgumentException) { return true; }
    }
    static void Gone(int? id, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!Exited(id) && clock.ElapsedMilliseconds < 1500) Thread.Sleep(5);
        Check(Exited(id), message);
    }
    sealed class FakeBackend : IControllerOutput
    {
        public bool IsConnected { get; private set; }
        public string Status { get { return "Synthetic only"; } }
        public int Neutrals, Disposals;
        public readonly List<XInputPacket> Packets = new List<XInputPacket>();
        public void Connect() { IsConnected = true; }
        public void Submit(ControllerFrame frame) { Packets.Add(XInputPacket.FromFrame(frame)); }
        public void Neutral() { Neutrals++; }
        public void Dispose() { Disposals++; IsConnected = false; }
    }
    sealed class BlockedCloseStream : MemoryStream
    {
        internal readonly ManualResetEventSlim Closing = new ManualResetEventSlim(false);
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        internal int Flushes;
        public override void Flush() { Interlocked.Increment(ref Flushes); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Closing.Set(); Release.Wait(5000); }
            base.Dispose(disposing);
        }
    }
    public static string RunProtocol()
    {
        checks = 0;
        int created = 0;
        var sink = new StringWriter(CultureInfo.InvariantCulture);
        int result = OutputHost.Run(new StringReader(""), sink, delegate { created++; return new FakeBackend(); });
        Check(result == 0 && created == 0, "EOF before CONNECT never constructs a backend");
        var fake = new FakeBackend(); sink = new StringWriter(CultureInfo.InvariantCulture);
        result = OutputHost.Run(new StringReader("TK75OUT/1 1 CONNECT\nTK75OUT/1 2 FRAME 36865 51 204 -32768 32767 16384 -16384\nTK75OUT/1 3 NEUTRAL\nTK75OUT/1 4 QUIT\n"), sink, delegate { return fake; });
        Check(result == 0 && fake.Packets.Count == 1, "Valid versioned commands reach fake backend");
        var packet = fake.Packets[0];
        Check(packet.Buttons == 36865 && packet.LeftTrigger == 51 && packet.RightTrigger == 204 && packet.LeftX == -32768 && packet.LeftY == 32767 && packet.RightX == 16384 && packet.RightY == -16384, "Integer packet survives both process conversions exactly");
        Check(fake.Disposals == 1 && fake.Neutrals == 3, "QUIT neutralizes and disposes exactly once");
        Check(sink.ToString().Contains("TK75OUT/1 4 ACK") && !sink.ToString().Contains("ERROR"), "Each successful command has correlated ACK");
        foreach (string invalid in new[] {
            "TK75OUT/1 2 FRAME 0 0 0 NaN 0 0 0", "TK75OUT/1 2 FRAME 0 256 0 0 0 0 0",
            "TK75OUT/1 2 FRAME 1024 0 0 0 0 0 0", "TK75OUT/1 2 FRAME 0 0 0 32768 0 0 0",
            "TK75OUT/1 2 FRAME 0 0 0 0 0 0", "TK75OUT/1 1 NEUTRAL", "TK75OUT/2 2 FRAME 0 0 0 0 0 0 0",
            "TK75OUT/1 2 UNKNOWN", new string('x', 600) })
        {
            fake = new FakeBackend(); sink = new StringWriter(CultureInfo.InvariantCulture);
            result = OutputHost.Run(new StringReader("TK75OUT/1 1 CONNECT\n" + invalid + "\n"), sink, delegate { return fake; });
            Check(result == 1 && fake.Packets.Count == 0 && fake.Disposals == 1 && sink.ToString().Contains("ERROR"), "Broken peer is rejected and cleaned: " + invalid.Substring(0, Math.Min(32, invalid.Length)));
        }
        Check(OutputHost.Run(new string[0]) == 64, "Host flag dispatch rejects normal UI arguments without loading native client");
        using (var output = new IsolatedOutput("not-started.exe", "--test-only", 15000, 250, 3500, 3500))
            Check(output.HostProcessId == null && !output.IsConnected, "Graceful cleanup configuration does not launch a child");
        Reject(delegate { new IsolatedOutput("not-started.exe", "", 15000, 250, 3500, -1); }, "Negative graceful cleanup deadline is rejected");
        Reject(delegate { new IsolatedOutput("not-started.exe", "", 15000, 250, 3500, 30001); }, "Unbounded graceful cleanup deadline is rejected");
        var input = new BlockedCloseStream();
        try
        {
            var clock = Stopwatch.StartNew();
            var close = IsolatedOutput.CloseInputWithoutFlush(input);
            Check(clock.ElapsedMilliseconds < 500, "Blocked pipe close is queued without blocking its caller");
            Check(input.Closing.Wait(1500), "Underlying input stream receives the close request");
            Check(!close.IsCompleted && input.Flushes == 0, "Blocked close never flushes a StreamWriter on the caller");
            input.Release.Set();
            Check(close.Wait(1500) && input.Flushes == 0, "Pipe close can finish after cleanup without an extra flush");
        }
        finally { input.Release.Set(); }
        return "PASS: " + checks + " offline output protocol/cleanup assertions; fake backend and memory streams only.";
    }
    public static string RunProcesses(string helper)
    {
        checks = 0;
        ParallelProcesses(helper);
        using (var output = new IsolatedOutput(helper, "success", 2000, 200))
        {
            Check(output.HostProcessId == null && !output.IsConnected, "Constructor does not start child");
            output.Connect(); int? pid = output.HostProcessId;
            Check(output.IsConnected && pid.HasValue, "Fake child connects through private job");
            var frame = new ControllerFrame { LeftY = 1 };
            output.Submit(frame); long count = output.AcknowledgedCommands;
            output.Submit(frame); Check(output.AcknowledgedCommands == count, "Identical immediate frame is deduplicated");
            Thread.Sleep(110); output.Submit(frame);
            Check(output.AcknowledgedCommands == count + 1, "Unchanged frame receives periodic bounded heartbeat ACK");
            output.Neutral(); output.Dispose(); output.Dispose();
            Check(!output.IsConnected && output.HostProcessId == null, "Dispose clears process and connection state");
            Gone(pid, "Disposed own child exited");
            Reject(delegate { output.Connect(); }, "Disposed wrapper cannot reconnect");
        }
        using (var output = new IsolatedOutput(helper, "stall-connect", 200, 150))
        {
            var clock = Stopwatch.StartNew(); Reject(output.Connect, "Stalled CONNECT must time out");
            Check(clock.ElapsedMilliseconds < 1800 && !output.IsConnected, "CONNECT timeout and cleanup are bounded");
            Gone(output.LastHostProcessId, "Stalled CONNECT child was killed");
        }
        foreach (string mode in new[] { "stall-frame", "crash", "malformed", "wrong-id", "oversized", "error" })
        using (var output = new IsolatedOutput(helper, mode, 2000, 150))
        {
            output.Connect(); int? pid = output.HostProcessId; var clock = Stopwatch.StartNew();
            Exception error = Reject(delegate { output.Submit(new ControllerFrame { LeftY = 1 }); }, mode + " must fail");
            Check(clock.ElapsedMilliseconds < 1800 && !output.IsConnected && output.HostProcessId == null, mode + " disarms with bounded cleanup");
            Check(!String.IsNullOrEmpty(error.Message), mode + " has visible reason");
            Gone(pid, mode + " child no longer exists");
        }
        using (var output = new IsolatedOutput(helper, "stall-neutral", 2000, 150))
        {
            output.Connect(); int? pid = output.HostProcessId; var clock = Stopwatch.StartNew();
            Reject(output.Neutral, "Stalled NEUTRAL must time out");
            Check(clock.ElapsedMilliseconds < 1800 && !output.IsConnected, "NEUTRAL timeout never blocks indefinitely");
            Gone(pid, "Stalled NEUTRAL child was killed");
        }
        using (var output = new IsolatedOutput(helper, "stall-quit", 2000, 150))
        {
            output.Connect(); int? pid = output.HostProcessId; var clock = Stopwatch.StartNew(); output.Dispose();
            Check(clock.ElapsedMilliseconds < 1800 && !output.IsConnected, "Stalled QUIT has bounded Dispose");
            Gone(pid, "Stalled QUIT child was killed");
        }
        using (var output = new IsolatedOutput(helper, "stderr-flood", 2000, 500))
        {
            output.Connect(); output.Submit(new ControllerFrame { RightTrigger = 1 });
            Check(output.IsConnected, "Stderr flood is drained without deadlock or retained logs");
        }
        using (var output = new IsolatedOutput(helper, "early-eof", 2000, 150))
        { Reject(output.Connect, "Early child EOF fails Connect"); Gone(output.LastHostProcessId, "Early EOF child is gone"); }
        using (var output = new IsolatedOutput(helper, "success", 2000, 150))
        {
            output.Connect(); int? pid = output.HostProcessId;
            Reject(delegate { output.Submit(new ControllerFrame { LeftX = Double.NaN }); }, "Invalid frame rejected before sending");
            Check(!output.IsConnected, "Invalid parent frame disarms child"); Gone(pid, "Invalid-frame child is terminated");
        }
        using (var owner = new Process { StartInfo = new ProcessStartInfo {
            FileName = helper, Arguments = "owner-exit", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } })
        {
            owner.Start();
            Check(owner.WaitForExit(4000), "Synthetic abrupt owner exits in bounded time");
            string line = owner.StandardOutput.ReadToEnd().Trim(); int childId;
            Check(owner.ExitCode == 0 && Int32.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out childId), "Owner reports only its fake child PID");
            childId = Int32.Parse(line, CultureInfo.InvariantCulture);
            Gone(childId, "KILL_ON_JOB_CLOSE removes fake child on abrupt owner exit without Dispose");
        }
        return "PASS: " + checks + " isolated-process assertions; only synthetic helper processes, no ViGEm load or driver/controller access.";
    }
    static void ParallelProcesses(string helper)
    {
        using (var first = new IsolatedOutput(helper, "route-a", 2000, 500))
        using (var second = new IsolatedOutput(helper, "route-b", 2000, 500))
        {
            first.Connect(); second.Connect();
            int? firstPid = first.HostProcessId, secondPid = second.HostProcessId;
            Check(firstPid.HasValue && secondPid.HasValue && firstPid != secondPid, "Concurrent outputs own distinct child processes");
            first.Submit(new ControllerFrame { Buttons = 0x1000, LeftTrigger = .2, LeftX = 1 });
            second.Submit(new ControllerFrame { Buttons = 0x2000, RightTrigger = .8, RightY = -1 });
            Check(first.IsConnected && second.IsConnected, "Both children accept only their own independent golden button/axis packets");
            long secondCommands = second.AcknowledgedCommands;
            first.Dispose(); Gone(firstPid, "Disposing one output removes its own child");
            Check(second.IsConnected && second.HostProcessId == secondPid && !Exited(secondPid), "Disposing the first private job preserves its live peer");
            second.Submit(new ControllerFrame { Buttons = 0x2000, RightTrigger = .8, RightY = 1 });
            Check(second.AcknowledgedCommands == secondCommands + 1, "Surviving peer acknowledges fresh output after the other job is disposed");
            second.Dispose(); Gone(secondPid, "Second child is removed only by its own disposal");
        }
        using (var failing = new IsolatedOutput(helper, "crash", 2000, 500))
        using (var healthy = new IsolatedOutput(helper, "route-b", 2000, 500))
        {
            failing.Connect(); healthy.Connect();
            int? failedPid = failing.HostProcessId, healthyPid = healthy.HostProcessId;
            healthy.Submit(new ControllerFrame { Buttons = 0x2000, RightTrigger = .8, RightY = -1 });
            long healthyCommands = healthy.AcknowledgedCommands;
            Reject(delegate { failing.Submit(new ControllerFrame { LeftX = 1 }); }, "One concurrent child's crash remains visible to its owner");
            Gone(failedPid, "Crashed child cleanup finishes independently");
            Check(healthy.IsConnected && healthy.HostProcessId == healthyPid && !Exited(healthyPid), "A peer crash cannot close another output's job or pipe");
            healthy.Submit(new ControllerFrame { Buttons = 0x2000, RightTrigger = .8, RightY = 1 });
            Check(healthy.AcknowledgedCommands == healthyCommands + 1, "Healthy peer keeps acknowledging its own packet after the other child crashes");
            healthy.Dispose(); Gone(healthyPid, "Healthy peer still cleans up its own child normally");
        }
    }
}
