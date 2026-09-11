using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Tk75.App;
using Tk75.Mapping;
using Tk75.Output;

// Compile-time boundaries only. The real ReaderSession, WorkspaceStore and
// hardware constructors are not loaded. All MappingSession workers are real.
namespace Tk75.App
{
    public sealed class WorkspaceStore
    { public void Event(string message) { throw new InvalidOperationException("No file logging in this test."); } }
    public sealed class ReaderSession
    {
        public bool IsReading { get { return false; } }
        public Dictionary<int, double> GetRawSnapshot(double age) { return new Dictionary<int, double>(); }
        internal void CopyRawSnapshot(double age, Dictionary<int, double> destination) { destination.Clear(); }
    }
}

public static class MultiMappingIntegrationHarness
{
    static int checks;
    static void Check(bool value, string reason)
    { checks++; if (!value) throw new InvalidOperationException("FAIL: " + reason); }
    static void Wait(Func<bool> condition, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 2000) Thread.Sleep(2);
        Check(condition(), reason);
    }
    static bool Near(double a, double b) { return Math.Abs(a - b) < .000001; }
    static bool Neutral(ControllerFrame f)
    { return f.Buttons == 0 && f.LeftX == 0 && f.LeftY == 0 && f.RightX == 0 && f.RightY == 0 && f.LeftTrigger == 0 && f.RightTrigger == 0; }
    static ControllerFrame Copy(ControllerFrame f)
    { return new ControllerFrame { Buttons = f.Buttons, LeftX = f.LeftX, LeftY = f.LeftY, RightX = f.RightX, RightY = f.RightY, LeftTrigger = f.LeftTrigger, RightTrigger = f.RightTrigger }; }

    sealed class SharedInput
    {
        readonly object gate = new object();
        readonly Dictionary<int, double> values = new Dictionary<int, double>();
        public SharedInput() { for (int key = 1; key <= 5; key++) values.Add(key, 0); }
        public void Set(params double[] raw)
        { lock (gate) { for (int i = 0; i < raw.Length; i++) values[i + 1] = raw[i]; } }
        public IDictionary<int, double> Snapshot(double age)
        { lock (gate) return new Dictionary<int, double>(values); }
    }
    sealed class FakeOutput : IControllerOutput
    {
        readonly object gate = new object();
        bool connected;
        int submits, neutrals, disposals, afterDispose, beforeNeutral, invalid;
        ControllerFrame last = new ControllerFrame();
        public readonly ControllerKind Kind;
        public volatile bool FailSubmit;
        public Action ConnectHook, SubmitHook, DisposeHook;
        public FakeOutput(ControllerKind kind) { Kind = kind; }
        public bool IsConnected { get { lock (gate) return connected; } }
        public string Status { get { return "Synthetic output only"; } }
        public int Submits { get { lock (gate) return submits; } }
        public int Neutrals { get { lock (gate) return neutrals; } }
        public int Disposals { get { lock (gate) return disposals; } }
        public int AfterDispose { get { lock (gate) return afterDispose; } }
        public int BeforeNeutral { get { lock (gate) return beforeNeutral; } }
        public int Invalid { get { lock (gate) return invalid; } }
        public ControllerFrame Last { get { lock (gate) return Copy(last); } }
        public void Connect() { lock (gate) connected = true; if (ConnectHook != null) ConnectHook(); }
        public void Submit(ControllerFrame frame)
        {
            Action hook = Interlocked.Exchange(ref SubmitHook, null);
            if (hook != null) hook();
            lock (gate)
            {
                if (disposals != 0) { afterDispose++; throw new InvalidOperationException("Submit after disposal"); }
                if (FailSubmit) throw new InvalidOperationException("Synthetic endpoint send failure");
                submits++; if (neutrals == 0) beforeNeutral++;
                if (frame.Errors.Count != 0) invalid++;
                last = Copy(frame);
            }
        }
        public void Neutral() { lock (gate) { neutrals++; last = new ControllerFrame(); } }
        public void Dispose() { if (DisposeHook != null) DisposeHook(); lock (gate) { disposals++; connected = false; } }
    }
    sealed class Endpoint : IControllerSession
    {
        readonly object outputGate = new object();
        public readonly MappingSession Session;
        public readonly Thread Worker;
        public volatile SharedInput Source;
        public string Id;
        public Action<FakeOutput> PrepareOutput;
        readonly List<FakeOutput> outputs = new List<FakeOutput>();
        public Endpoint()
        {
            Session = MappingSession.CreateWithControllerFactory(null, delegate(ControllerKind kind) {
                var output = new FakeOutput(kind);
                if (PrepareOutput != null) PrepareOutput(output);
                lock (outputGate) outputs.Add(output);
                return output;
            }, delegate { return Source != null; }, delegate(double age) {
                SharedInput source = Source;
                return source == null ? new Dictionary<int, double>() : source.Snapshot(age);
            });
            Worker = (Thread)typeof(MappingSession).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Session);
        }
        public FakeOutput LastOutput { get { lock (outputGate) return outputs[outputs.Count - 1]; } }
        public FakeOutput[] Outputs { get { lock (outputGate) return outputs.ToArray(); } }
        public bool Enabled { get { return Session.Enabled; } }
        public ControllerFrame Frame { get { return Session.Frame; } }
        public PreviewSnapshot Preview { get { return Session.Preview; } }
        public string Status { get { return Session.Status; } }
        public void Configure(Profile profile, IDictionary<int, Calibration> calibration)
        { Id = profile.Controllers[0].Id; Session.Configure(profile, calibration); }
        public void SetInputSource(object source)
        { Session.Disable("Synthetic source changed"); Source = (SharedInput)source; }
        public void SetKeyboardMode(bool value) { Session.SetKeyboardMode(value); }
        public void Enable() { Session.Enable(); }
        public ControllerRelease PrepareDisable(string reason) { return Session.PrepareDisable(reason); }
        public void Disable(string reason) { Session.Disable(reason); }
        public void Dispose() { Session.Dispose(); }
    }
    sealed class Fixture : IDisposable
    {
        public readonly SharedInput Input = new SharedInput();
        public readonly List<Endpoint> Endpoints = new List<Endpoint>();
        public readonly MultiControllerSession Runtime;
        public Fixture()
        {
            Runtime = new MultiControllerSession(delegate { var endpoint = new Endpoint(); Endpoints.Add(endpoint); return endpoint; }, 4);
            Runtime.SetInputSource(Input);
            Profile profile = ControllerRouting.Add(new Profile(), "ps", "PlayStation", ControllerKind.DualSense);
            profile = ControllerRouting.Add(profile, "xbox2", "Second Xbox", ControllerKind.Xbox360);
            profile = MappingAssignments.Add(profile, new[] { 1 }, OutputTarget.LeftXPositive, "main");
            profile = MappingAssignments.Add(profile, new[] { 3 }, OutputTarget.A, "main");
            profile = MappingAssignments.Add(profile, new[] { 1 }, OutputTarget.RightXNegative, "ps");
            profile = MappingAssignments.Add(profile, new[] { 2 }, OutputTarget.RightTrigger, "ps");
            profile = MappingAssignments.Add(profile, new[] { 3 }, OutputTarget.X, "ps");
            profile = MappingAssignments.Add(profile, new[] { 4 }, OutputTarget.LeftYNegative, "xbox2");
            profile = MappingAssignments.Add(profile, new[] { 5 }, OutputTarget.B, "xbox2");
            var calibration = new Dictionary<int, Calibration>();
            for (int key = 1; key <= 5; key++) calibration.Add(key, new Calibration(0, 100));
            Runtime.Configure(profile, calibration);
        }
        public Endpoint Slot(string id)
        { foreach (Endpoint endpoint in Endpoints) if (endpoint.Id == id) return endpoint; throw new InvalidOperationException("Missing test route " + id); }
        public void EnableAll()
        { foreach (string id in new[] { "main", "ps", "xbox2" }) Runtime.EnableController(id); }
        public void Dispose() { Runtime.Dispose(); }
    }
    static void CheckCleanOutput(FakeOutput output, string name)
    {
        Check(output.Neutrals > 0 && output.BeforeNeutral == 0, name + " starts neutral before any mapped frame");
        Check(output.Invalid == 0 && output.AfterDispose == 0, name + " receives only valid frames during its lifetime");
    }
    static void RoutingAndIndividualDisconnect()
    {
        using (var f = new Fixture())
        {
            f.EnableAll(); var xbox = f.Slot("main").LastOutput; var ps = f.Slot("ps").LastOutput; var xbox2 = f.Slot("xbox2").LastOutput;
            Check(xbox.Kind == ControllerKind.Xbox360 && ps.Kind == ControllerKind.DualSense && xbox2.Kind == ControllerKind.Xbox360, "Mixed routes request their own exact controller kind");
            Check(!Object.ReferenceEquals(xbox, ps) && !Object.ReferenceEquals(xbox, xbox2), "Each route owns a separate output instance");
            f.Input.Set(50, 75, 100, 100, 100);
            Wait(delegate { return Near(xbox.Last.LeftX, .5) && xbox.Last.Buttons == 0x1000 && Near(ps.Last.RightX, -.5) && Near(ps.Last.RightTrigger, .75) && ps.Last.Buttons == 0x4000 && xbox2.Last.LeftY == -1 && xbox2.Last.Buttons == 0x2000; }, "Three real workers route simultaneous axes and buttons, including a key shared between Xbox and PS");
            Check(xbox.Last.RightX == 0 && xbox.Last.RightTrigger == 0 && ps.Last.LeftX == 0 && xbox2.Last.LeftX == 0 && xbox2.Last.RightTrigger == 0, "Unassigned channels do not leak between workers");
            XInputPacket first = XInputPacket.FromFrame(xbox.Last), second = XInputPacket.FromFrame(xbox2.Last);
            Check(first.LeftX == 16384 && first.Buttons == 0x1000 && second.LeftY == -32768 && second.Buttons == 0x2000, "Independent Xbox frames preserve signed axis and button packet values");
            f.Runtime.SelectedControllerId = "ps";
            Wait(delegate { return Near(f.Runtime.Preview.RightTrigger, .75); }, "Selection exposes the PS preview without rerouting output");
            Check(f.Runtime.ActiveControllerIds.Length == 3 && xbox.Disposals == 0 && xbox2.Disposals == 0, "Changing the editing route preserves all devices");
            f.Runtime.DisableController("ps", "Individual disconnect");
            Check(!ps.IsConnected && ps.Disposals == 1 && Neutral(ps.Last), "Individual disconnect neutralizes and removes only the PS endpoint");
            Check(f.Runtime.SelectedControllerId == "ps" && f.Runtime.IsControllerEnabled("main") && f.Runtime.IsControllerEnabled("xbox2"), "Disconnecting a selected route preserves its selection and both peers");
            int firstCount = xbox.Submits, secondCount = xbox2.Submits;
            f.Input.Set(25, 0, 0, 50, 0);
            Wait(delegate { return xbox.Submits > firstCount && xbox2.Submits > secondCount && Near(xbox.Last.LeftX, .25) && Near(xbox2.Last.LeftY, -.5) && xbox.Last.Buttons == 0 && xbox2.Last.Buttons == 0; }, "Both surviving workers continue processing fresh values and button releases after peer removal");
            foreach (FakeOutput output in new[] { xbox, ps, xbox2 }) CheckCleanOutput(output, output.Kind.ToString());
        }
    }
    static void NeutralStartupWhilePeerIsActive()
    {
        using (var f = new Fixture())
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            f.Runtime.EnableController("main"); var xbox = f.Slot("main").LastOutput;
            f.Input.Set(100, 0, 0, 0, 0);
            Wait(delegate { return xbox.Last.LeftX == 1; }, "First device has live input before the second connects");
            f.Slot("ps").PrepareOutput = delegate(FakeOutput output) { output.ConnectHook = delegate { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException("Test CONNECT gate expired"); }; };
            Exception connectError = null;
            var connect = new Thread(delegate() { try { f.Runtime.EnableController("ps"); } catch (Exception ex) { connectError = ex; } }) { IsBackground = true };
            connect.Start();
            try
            {
                Check(entered.Wait(2000), "Second device reaches its independent CONNECT boundary");
                f.Input.Set(50, 0, 0, 0, 0);
                Wait(delegate { return Near(xbox.Last.LeftX, .5); }, "A slow second CONNECT does not stall the existing worker");
            }
            finally { release.Set(); Check(connect.Join(3000), "Second CONNECT completes within its test deadline"); }
            Check(connectError == null, "Connecting a peer succeeds without altering existing output");
            var ps = f.Slot("ps").LastOutput;
            Wait(delegate { return ps.Submits >= 3; }, "New output receives worker heartbeat frames");
            Check(Neutral(ps.Last) && Near(xbox.Last.LeftX, .5), "Held startup input is withheld only for the newly connected device");
            f.Input.Set(50, 75, 0, 0, 0);
            Wait(delegate { return Near(ps.Last.RightTrigger, .75); }, "Fresh independent input works while a startup key awaits release");
            Check(ps.Last.RightX == 0 && Near(xbox.Last.LeftX, .5), "A shared held key cannot bypass the new device's release gate");
            f.Input.Set(0, 0, 0, 0, 0);
            Wait(delegate { return xbox.Last.LeftX == 0 && ps.Last.RightTrigger == 0; }, "Physical release is observed by both workers");
            f.Input.Set(25, 0, 0, 0, 0);
            Wait(delegate { return Near(xbox.Last.LeftX, .25) && Near(ps.Last.RightX, -.25); }, "After release the same fresh key independently drives both devices");
            Check(xbox.Disposals == 0 && ps.Disposals == 0, "Adding and arming the peer does not replace either device");
            CheckCleanOutput(xbox, "Existing Xbox"); CheckCleanOutput(ps, "New PS");
        }
    }
    static void FailureIsolationAndExplicitReconnect()
    {
        using (var f = new Fixture())
        {
            f.EnableAll(); var xbox = f.Slot("main").LastOutput; var ps = f.Slot("ps").LastOutput; var xbox2 = f.Slot("xbox2").LastOutput;
            f.Input.Set(50, 50, 0, 50, 0);
            Wait(delegate { return Near(xbox.Last.LeftX, .5) && Near(ps.Last.RightTrigger, .5) && Near(xbox2.Last.LeftY, -.5); }, "All routes produce input before the injected send failure");
            ps.FailSubmit = true;
            Wait(delegate { return ps.Disposals == 1 && !f.Runtime.IsControllerEnabled("ps"); }, "One worker's send failure removes its own endpoint");
            Check(Neutral(ps.Last) && f.Runtime.IsControllerEnabled("main") && f.Runtime.IsControllerEnabled("xbox2"), "A failed device is neutralized while both peers remain connected");
            f.Input.Set(75, 0, 0, 25, 0);
            Wait(delegate { return Near(xbox.Last.LeftX, .75) && Near(xbox2.Last.LeftY, -.25); }, "Healthy workers continue after the peer's send failure");
            Check(f.Slot("ps").Outputs.Length == 1, "A failed worker never reconnects automatically");
            f.Input.Set(0, 0, 0, 0, 0);
            Wait(delegate { return xbox.Last.LeftX == 0 && xbox2.Last.LeftY == 0; }, "Release reaches the surviving routes before explicit reconnect");
            f.Runtime.EnableController("ps"); var replacement = f.Slot("ps").LastOutput;
            Check(!Object.ReferenceEquals(ps, replacement) && f.Slot("main").Outputs.Length == 1 && f.Slot("xbox2").Outputs.Length == 1, "Explicit reconnect replaces only the failed output");
            f.Input.Set(0, 25, 100, 0, 0);
            Wait(delegate { return Near(replacement.Last.RightTrigger, .25) && replacement.Last.Buttons == 0x4000 && xbox.Last.Buttons == 0x1000; }, "Replacement and existing peer receive fresh shared button input");
            CheckCleanOutput(ps, "Failed PS"); CheckCleanOutput(replacement, "Replacement PS");
        }
    }
    static void ShutdownNeutralizesEveryWorker()
    {
        using (var f = new Fixture())
        {
            f.EnableAll(); f.Input.Set(100, 100, 100, 100, 100);
            Wait(delegate { foreach (Endpoint endpoint in f.Endpoints) if (Neutral(endpoint.LastOutput.Last)) return false; return true; }, "All devices are active before shutdown");
            int prematureDisposals = 0;
            foreach (Endpoint endpoint in f.Endpoints)
                endpoint.LastOutput.DisposeHook = delegate {
                    foreach (Endpoint peer in f.Endpoints)
                        if (!Neutral(peer.LastOutput.Last)) Interlocked.Increment(ref prematureDisposals);
                };
            var clock = Stopwatch.StartNew(); f.Runtime.Dispose();
            Check(clock.ElapsedMilliseconds < 2000, "Synthetic shutdown finishes within a bounded deadline");
            Check(prematureDisposals == 0, "All active outputs become neutral before any device disposal begins");
            Check(!f.Runtime.AnyEnabled && f.Runtime.ActiveControllerIds.Length == 0, "Shutdown clears every active route");
            foreach (Endpoint endpoint in f.Endpoints)
            {
                Check(!endpoint.Worker.IsAlive, endpoint.Id + " real mapping thread has ended before Dispose returns");
                foreach (FakeOutput output in endpoint.Outputs)
                {
                    Check(output.Disposals == 1 && !output.IsConnected && Neutral(output.Last), endpoint.Id + " output is neutral and disposed exactly once");
                    CheckCleanOutput(output, endpoint.Id);
                }
            }
            f.Runtime.Dispose();
            foreach (Endpoint endpoint in f.Endpoints)
                foreach (FakeOutput output in endpoint.Outputs) Check(output.Disposals == 1 && output.AfterDispose == 0, "Repeated shutdown causes no duplicate disposal or late Submit");
        }
    }
    static void BlockedSubmitDoesNotDelayPeerNeutralization()
    {
        using (var f = new Fixture())
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            f.EnableAll(); f.Input.Set(100, 100, 100, 100, 100);
            var first = f.Slot("main").LastOutput; var second = f.Slot("ps").LastOutput; var third = f.Slot("xbox2").LastOutput;
            Wait(delegate { return !Neutral(first.Last) && !Neutral(second.Last) && !Neutral(third.Last); }, "All outputs are active before one Submit blocks");
            int secondNeutrals = second.Neutrals, thirdNeutrals = third.Neutrals;
            first.SubmitHook = delegate { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Test Submit gate expired"); };
            Thread stop = null; Exception stopError = null;
            try
            {
                Check(entered.Wait(2000), "First real mapping worker holds its Submit boundary");
                stop = new Thread(delegate() { try { f.Runtime.Disable("Synthetic global stop"); } catch (Exception ex) { stopError = ex; } }) { IsBackground = true };
                stop.Start();
                Wait(delegate { return second.Neutrals > secondNeutrals && third.Neutrals > thirdNeutrals && Neutral(second.Last) && Neutral(third.Last); }, "Both healthy peers become neutral while the first Submit is still blocked");
                Check(!stop.Join(0) && second.Disposals == 0 && third.Disposals == 0, "Device removal waits for all neutralization attempts to finish");
            }
            finally
            {
                release.Set();
                if (stop != null) Check(stop.Join(3000), "Global stop completes after the pending Submit is released");
            }
            Check(stopError == null && !f.Runtime.AnyEnabled, "Global stop completes without leaving an active route");
            foreach (FakeOutput output in new[] { first, second, third })
            {
                Check(output.Disposals == 1 && Neutral(output.Last), "Each output is neutral before its single removal after blocked Submit");
                CheckCleanOutput(output, "Blocked-submit shutdown route");
            }
        }
    }
    public static string Run()
    {
        checks = 0;
        RoutingAndIndividualDisconnect(); NeutralStartupWhilePeerIsActive(); FailureIsolationAndExplicitReconnect(); ShutdownNeutralizesEveryWorker(); BlockedSubmitDoesNotDelayPeerNeutralization();
        return "PASS: " + checks + " multi-mapping integration assertions; real mapping workers, shared synthetic pressure, independent fake Xbox/PS outputs; no hardware or helper processes.";
    }
}
