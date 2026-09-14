using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tk75.App;
using Tk75.Mapping;
using Tk75.Output;

// Unit-boundary stand-ins only. Production ReaderSession/WorkspaceStore are
// deliberately NOT compiled into this offline harness. Their ordinary API is
// separately checked by the application build and ReaderSession's own fake tests.
namespace Tk75.App
{
    public sealed class WorkspaceStore
    { public void Event(string message) { throw new InvalidOperationException("Tests must not write logs."); } }
    public sealed class ReaderSession
    {
        public bool IsReading { get; set; }
        public Func<double, Dictionary<int, double>> Snapshot;
        public Dictionary<int, double> GetRawSnapshot(double age) { return Snapshot == null ? new Dictionary<int, double>() : Snapshot(age); }
        internal void CopyRawSnapshot(double age, Dictionary<int, double> destination)
        {
            destination.Clear();
            foreach (var item in GetRawSnapshot(age)) destination.Add(item.Key, item.Value);
        }
    }
}

public static class MappingSessionHarness
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }
    static void Wait(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 1500) Thread.Sleep(2);
        Check(condition(), message);
    }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, message); }
    static bool Neutral(ControllerFrame value)
    { return value.LeftX == 0 && value.LeftY == 0 && value.RightX == 0 && value.RightY == 0 && value.LeftTrigger == 0 && value.RightTrigger == 0 && value.Buttons == 0; }

    sealed class FakeInput
    {
        readonly object gate = new object();
        readonly Dictionary<int, double> values = new Dictionary<int, double>();
        readonly Dictionary<int, double> ages = new Dictionary<int, double>();
        bool reading = true;
        public Action SnapshotHook;
        double requestedAge;
        int snapshotCalls;
        public int SnapshotCalls { get { return Interlocked.CompareExchange(ref snapshotCalls, 0, 0); } }
        public bool Reading { get { lock (gate) return reading; } set { lock (gate) reading = value; } }
        public double RequestedAge { get { lock (gate) return requestedAge; } }
        public void Set(int key, double value, double age)
        { lock (gate) { values[key] = value; ages[key] = age; } }
        public void Remove(int key) { lock (gate) { values.Remove(key); ages.Remove(key); } }
        public IDictionary<int, double> Snapshot(double maximumAge)
        {
            Interlocked.Increment(ref snapshotCalls);
            var copy = new Dictionary<int, double>();
            lock (gate)
            {
                requestedAge = maximumAge;
                if (reading) foreach (var pair in values)
                    if (pair.Value == 0 || ages[pair.Key] <= maximumAge) copy.Add(pair.Key, pair.Value);
            }
            Action hook = Interlocked.Exchange(ref SnapshotHook, null);
            if (hook != null) hook();
            return copy;
        }
    }

    sealed class FakeOutput : IControllerOutput, ICancellableControllerConnection
    {
        readonly object gate = new object();
        bool connected;
        int submits, nonzero, invalid, neutralCalls, disposeCalls, afterDispose;
        ControllerFrame last = new ControllerFrame();
        public Action ConnectHook, SubmitHook;
        public Action ReadConnectionHook;
        public Action<CancellationToken> CancellableConnectHook;
        public Action<ControllerFrame> ObserveSubmit;
        public volatile bool ThrowNeutral, ThrowDispose, ThrowSubmit, DropOnSubmit;
        public string Status { get { return "Synthetic output"; } }
        public bool IsConnected { get { if (ReadConnectionHook != null) ReadConnectionHook(); lock (gate) return connected; } }
        public int Submits { get { lock (gate) return submits; } }
        public int Nonzero { get { lock (gate) return nonzero; } }
        public int Invalid { get { lock (gate) return invalid; } }
        public int NeutralCalls { get { lock (gate) return neutralCalls; } }
        public int DisposeCalls { get { lock (gate) return disposeCalls; } }
        public int AfterDispose { get { lock (gate) return afterDispose; } }
        public ControllerFrame Last { get { lock (gate) return last; } }
        public void Connect() { lock (gate) connected = true; if (ConnectHook != null) ConnectHook(); }
        public void Connect(CancellationToken cancellationToken)
        {
            if (CancellableConnectHook == null) { Connect(); return; }
            lock (gate) connected = true;
            CancellableConnectHook(cancellationToken);
        }
        public void Drop() { lock (gate) connected = false; }
        public void Submit(ControllerFrame value)
        {
            Action hook = Interlocked.Exchange(ref SubmitHook, null);
            if (hook != null) hook();
            Action<ControllerFrame> observer = ObserveSubmit;
            if (observer != null) observer(value);
            lock (gate)
            {
                if (disposeCalls != 0) { afterDispose++; throw new InvalidOperationException("Submit after Dispose"); }
                submits++;
                if (value.Errors.Count != 0) invalid++;
                if (!MappingSessionHarness.Neutral(value)) nonzero++;
                last = value;
                if (ThrowSubmit) throw new InvalidOperationException("Synthetic Submit failure");
                if (DropOnSubmit) connected = false;
            }
        }
        public void Neutral()
        {
            lock (gate)
            {
                neutralCalls++; last = new ControllerFrame();
                if (ThrowNeutral) throw new InvalidOperationException("Synthetic Neutral failure");
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                disposeCalls++; connected = false;
                if (ThrowDispose) throw new InvalidOperationException("Synthetic Dispose failure");
            }
        }
    }

    sealed class Fixture : IDisposable
    {
        public readonly FakeInput Input = new FakeInput();
        public readonly Profile Profile = new Profile();
        public readonly Dictionary<int, Calibration> Calibrations = new Dictionary<int, Calibration>();
        public readonly MappingSession Session;
        public readonly List<FakeOutput> Outputs = new List<FakeOutput>();
        public readonly List<ControllerKind> RequestedControllers = new List<ControllerKind>();
        public Action<FakeOutput> PreparingOutput;
        public Action<ControllerKind> PreparingController;
        public Fixture(bool twoKeys) : this(twoKeys, false) { }
        public Fixture(bool twoKeys, bool typedFactory) : this(twoKeys, typedFactory, true) { }
        public Fixture(bool twoKeys, bool typedFactory, bool initialValues)
        {
            Profile.Bindings.Add(new Binding { BindingId = "w", KeyIndex = 14, Target = OutputTarget.LeftYPositive });
            if (twoKeys) Profile.Bindings.Add(new Binding { BindingId = "s", KeyIndex = 9, Target = OutputTarget.LeftYNegative });
            if (initialValues) { Input.Set(14, 0, 0); Input.Set(9, 0, 0); }
            Calibrations.Add(14, new Calibration(0, 100)); Calibrations.Add(9, new Calibration(0, 100));
            Func<IControllerOutput> createOutput = delegate {
                var candidate = new FakeOutput(); Outputs.Add(candidate);
                if (PreparingOutput != null) PreparingOutput(candidate);
                return candidate;
            };
            if (typedFactory)
                Session = MappingSession.CreateWithControllerFactory(null, delegate(ControllerKind kind) {
                    RequestedControllers.Add(kind);
                    if (PreparingController != null) PreparingController(kind);
                    return createOutput();
                }, delegate { return Input.Reading; }, Input.Snapshot);
            else
                // Keep the old anonymous-delegate call as a compile regression.
                Session = new MappingSession(null, delegate { return createOutput(); }, delegate { return Input.Reading; }, Input.Snapshot);
            Session.Configure(Profile, Calibrations);
        }
        public FakeOutput Arm()
        { Session.Enable(); Check(Session.Enabled, "Explicit Enable arms fake output"); return Outputs[Outputs.Count - 1]; }
        public void RemainsOff(int created, string message)
        { Thread.Sleep(35); Check(!Session.Enabled && Outputs.Count == created, message); }
        public void Dispose() { Session.Dispose(); }
    }

    static void InitiallyUnknownInputs()
    {
        using (var f = new Fixture(true, false, false))
        {
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "An empty live device starts with neutral heartbeat frames");
            Check(output.Nonzero == 0 && output.Invalid == 0 && f.Session.Frame.InputResults.Count == 0, "Unknown raw values remain absent rather than fabricated zero measurements");
            f.Input.Set(14, 65, 0);
            Wait(delegate { return output.Last.LeftY == .65; }, "The first real key measurement activates its binding without reconnecting");
            Check(!f.Session.Frame.InputResults.ContainsKey(9) && !f.Session.Frame.BindingResults.ContainsKey("s"), "A still-unknown binding is deferred independently");
            f.Session.SetKeyboardMode(true); f.Session.SetKeyboardMode(false);
            Wait(delegate { return output.Last.LeftY == .65; }, "Mode resume permits still-unknown unrelated keys");
            f.Input.Set(9, 30, 0); f.Input.Set(14, 0, 0);
            Wait(delegate { return output.Last.LeftY == -.3; }, "A second newly observed key joins the same controller immediately");
            f.Input.Remove(9);
            Wait(delegate { return output.DisposeCalls == 1; }, "A previously observed key disappearing is a failure, not an unknown startup key");
            Check(Neutral(output.Last) && output.Invalid == 0, "Expired known input neutralizes and never submits an invalid frame");
            Reject(f.Session.Enable, "Explicit reconnection cannot erase missing-key history within the same reader");
        }
        using (var f = new Fixture(true, false, false))
        {
            f.Input.Set(14, 0, 0); var output = f.Arm();
            f.Input.Set(14, 80, 0);
            Wait(delegate { return output.Last.LeftY == .8; }, "One known binding remains usable while another has never reported");
            f.Input.Set(9, double.NaN, 0);
            Wait(delegate { return output.DisposeCalls == 1; }, "A malformed first measurement still disarms every binding");
            Check(Neutral(output.Last) && output.Invalid == 0, "Malformed present input is never filtered as merely unavailable");
        }
        using (var f = new Fixture(true, false, false))
        {
            f.Calibrations.Remove(9); f.Session.Configure(f.Profile, f.Calibrations);
            Reject(f.Session.Enable, "Unknown input cannot conceal a missing required calibration");
            Check(f.Outputs.Count == 0, "Missing calibration prevents backend construction");
            f.Calibrations[9] = new Calibration(10, 10); f.Session.Configure(f.Profile, f.Calibrations);
            Reject(f.Session.Enable, "Unknown input cannot conceal an invalid configured calibration");
        }
        using (var f = new Fixture(true, false, false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Profile.Bindings.Add(new Binding { BindingId = "d", KeyIndex = 21, Target = OutputTarget.A });
            f.Calibrations[21] = new Calibration(0, 100); f.Session.Configure(f.Profile, f.Calibrations);
            f.Input.Set(14, 80, 0);
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "A held SOCD member with an unknown counterpart connects neutrally");
            Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "Incomplete SOCD pair cannot conceal a held startup key");
            f.Input.Set(14, 0, 0);
            Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "Confirmed release frees the startup gate even before the other member reports");
            f.Input.Set(14, 100, 0); f.Input.Set(21, 100, 0);
            Wait(delegate { return output.Last.Buttons == 0x1000; }, "Unrelated newly observed button works while an SOCD member is unknown");
            Check(output.Last.LeftY == 0 && !f.Session.Frame.InputResults.ContainsKey(14) && !f.Session.Frame.InputResults.ContainsKey(9), "Both paired inputs are withheld until each has a real value");
            f.Input.Set(9, 0, 0);
            Wait(delegate { return output.Last.LeftY == 1 && output.Last.Buttons == 0x1000; }, "The SOCD pair joins output when its missing real measurement arrives");
            f.Input.Set(9, 100, 0);
            Wait(delegate { return output.Last.LeftY == -1; }, "The newly complete pair retains ordinary last-pressed behavior");
        }
        using (var f = new Fixture(false, false, false))
        {
            f.PreparingOutput = delegate(FakeOutput candidate) { candidate.ConnectHook = delegate { f.Input.Set(14, 100, 0); }; };
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "Unknown-to-held connect race keeps the newly connected device neutral");
            Check(f.Outputs.Count == 1 && output.DisposeCalls == 0 && output.Nonzero == 0, "Post-connect observation gates a first held measurement without an impulse or failed connection");
        }
        using (var f = new Fixture(false))
        {
            var first = f.Arm(); f.Input.Set(14, 100, 0);
            Wait(delegate { return first.Last.LeftY == 1; }, "Old reader has observed a held key before replacement");
            var replacement = new ReaderSession { IsReading = true };
            f.Session.SetReader(replacement);
            Check(first.DisposeCalls == 1 && Neutral(first.Last), "Replacing the reader neutralizes the original device");
            var second = f.Arm();
            Wait(delegate { return second.Submits >= 3; }, "A replacement reader starts neutral with its own empty observation history");
            Check(second.Nonzero == 0 && second.Invalid == 0, "Old held-key history cannot leak into a new reader");
            replacement.Snapshot = delegate(double age) { return new Dictionary<int, double> { { 14, 25 } }; };
            Wait(delegate { return second.Last.LeftY == .25; }, "Replacement reader learns its first real key normally");
            replacement.IsReading = false;
            Wait(delegate { return second.DisposeCalls == 1; }, "Device lease loss still disconnects output with deferred-input support");
        }
        using (var f = new Fixture(true, false, false))
        {
            var output = f.Arm(); f.Input.Reading = false;
            Wait(delegate { return output.DisposeCalls == 1; }, "Device lease loss disconnects even when no key has ever reported");
            Check(Neutral(output.Last), "Unknown-start disconnect remains neutral");
        }
    }

    static void StartupReleaseGate()
    {
        using (var f = new Fixture(true))
        {
            f.Profile.OpposedPolicy = OpposedPolicy.Subtract; f.Session.Configure(f.Profile, f.Calibrations);
            f.Input.Set(14, 80, 0); var output = f.Arm();
            Wait(delegate { return output.Submits >= 3 && f.Session.Preview.BindingResults.ContainsKey("w"); }, "Held-key connection sends neutral heartbeats while keeping real preview values");
            Check(output.Nonzero == 0 && f.Session.Preview.BindingResults["w"].Final == .8, "Startup gating affects output participation without changing the measured preview");
            f.Session.Enable(); int submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3; }, "Repeated Enable does not bypass the held-key startup gate");
            Check(output.Nonzero == 0 && f.Outputs.Count == 1, "Repeated connection request neither emits held input nor recreates the device");
            f.Input.Set(9, 25, 0);
            Wait(delegate { return output.Last.LeftY == -.25; }, "Unrelated fresh input works immediately while another startup key is withheld");
            f.Input.Set(14, 0, 0);
            Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "A real release makes the initial key ready without reconnecting");
            f.Input.Set(14, 60, 0);
            Wait(delegate { return Math.Abs(output.Last.LeftY - .35) < 1e-12; }, "A fresh press uses its exact normal mapping after release");
            Check(output.DisposeCalls == 0 && output.Invalid == 0 && f.Outputs.Count == 1, "Startup release keeps the same valid connected output");
        }
        using (var f = new Fixture(false))
        {
            f.Calibrations[14] = new Calibration(0, 385); f.Session.Configure(f.Profile, f.Calibrations);
            f.Input.Set(14, 1, 0); var output = f.Arm();
            Wait(delegate { return output.Last.LeftY == 1.0 / 385; }, "One-count startup tolerance leaves the real live mapping value unchanged");
            Check(!f.Session.Status.Contains("Loslassen"), "One count around the nominal rest point does not leave an unnecessary release gate");
            f.Input.Set(14, 100, 0);
            Wait(delegate { return output.Last.LeftY == 100.0 / 385; }, "Startup tolerance does not add a permanent deadzone");
        }
        using (var f = new Fixture(false))
        {
            f.Calibrations[14] = new Calibration(385, 0); f.Session.Configure(f.Profile, f.Calibrations);
            f.Input.Set(14, 384, 0); var output = f.Arm();
            Wait(delegate { return output.Last.LeftY == 1.0 / 385; }, "One-count rest tolerance also respects inverted sensor ranges");
            Check(!f.Session.Status.Contains("Loslassen"), "Inverted one-count rest offset is not mistaken for a held startup key");
        }
        foreach (bool button in new[] { false, true })
        {
            using (var f = new Fixture(false))
            {
                f.Calibrations[14] = new Calibration(0, 385);
                if (button) { f.Profile.Bindings[0].Target = OutputTarget.A; f.Profile.Bindings[0].Processing.ButtonThreshold = .001; }
                else { f.Profile.Bindings[0].Processing.MinOutput = .75; f.Profile.Bindings[0].Processing.SmoothingTimeConstant = .1; }
                f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(14, 1, 0); var output = f.Arm();
                Wait(delegate { return output.Submits >= 3; }, "Tiny raw startup input with a strong configured effect connects safely");
                Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "Noise tolerance cannot trigger a sensitive button or amplified/smoothed output");
                f.Input.Set(14, 0, 0);
                Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "Actual rest releases an amplified or sensitive startup mapping");
                f.Input.Set(14, 1, 0);
                Wait(delegate { return button ? output.Last.Buttons == 0x1000 : output.Last.LeftY > 0; }, "Fresh tiny input retains the user's sensitive mapping after startup release");
            }
        }
        foreach (bool inverted in new[] { false, true })
        {
            using (var f = new Fixture(false))
            {
                f.Calibrations[14] = inverted ? new Calibration(1, 0) : new Calibration(0, 1);
                f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(14, .5, 0); var output = f.Arm();
                Wait(delegate { return output.Submits >= 3; }, "Small calibration range connects with held pressure gated");
                Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "One raw count never classifies half of a small calibration range as rest");
                f.Input.Set(14, inverted ? .996 : .004, 0);
                Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "Capped half-percent rest tolerance releases a sufficiently low current value");
                f.Input.Set(14, .5, 0);
                Wait(delegate { return output.Last.LeftY == .5; }, "Small and inverted ranges retain their full live resolution after startup");
            }
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Bindings[0].Processing.TopDeadzone = .1; f.Profile.Bindings[0].Processing.Hysteresis = .05;
            f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(14, 20, 0); var output = f.Arm();
            f.Input.Set(14, 12, 0); int submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3; }, "Initial key moves into the configured hysteresis band");
            Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "Hysteresis band alone cannot count as a release of a previously held key");
            f.Input.Set(14, 10, 0);
            Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "The configured rest deadzone counts as a confirmed release");
            f.Input.Set(14, 25, 0);
            Wait(delegate { return Math.Abs(output.Last.LeftY - 1.0 / 6) < 1e-12; }, "Existing deadzone settings remain exact after initial release");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .5, ReleaseMovement = .8 });
            f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(14, 80, 0); var output = f.Arm();
            f.Session.SetKeyboardMode(true); f.Session.SetKeyboardMode(false); int submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3; }, "Mode switching retains the initial held-key gate on the same output");
            Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "Keyboard-mode toggling cannot bypass initial neutral connection");
            f.Session.SetKeyboardMode(true); f.Input.Set(14, 40, 0);
            Wait(delegate { return f.Session.Preview.LeftY == .4; }, "Physical release below first activation is observed while output is paused");
            f.Session.SetKeyboardMode(false); submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3 && !f.Session.Status.Contains("Loslassen"); }, "Startup release detected in keyboard mode remains released when resuming");
            Check(output.Nonzero == 0, "Old rapid-trigger preview history cannot emit output after startup release");
            f.Input.Set(14, 60, 0);
            Wait(delegate { return output.Last.LeftY == .6; }, "A new actuation starts fresh rapid-trigger output");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(9, 80, 0); var output = f.Arm(); f.Input.Set(14, 100, 0);
            Wait(delegate { return output.Submits >= 3; }, "An unbound held SOCD counterpart is included in startup gating");
            Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "A held unbound counterpart cannot affect the paired physical input through hidden SOCD state");
            f.Input.Set(9, 0, 0);
            Wait(delegate { return output.Last.LeftY == 1; }, "Releasing the initial counterpart allows a new press on its paired bound key");
        }
        foreach (int failure in new[] { 0, 1, 2 })
        {
            using (var f = new Fixture(false))
            {
                f.Input.Set(14, 80, 0); var output = f.Arm();
                if (failure == 0) f.Input.Remove(14);
                else if (failure == 1) f.Input.Set(14, double.NaN, 0);
                else f.Input.Reading = false;
                Wait(delegate { return output.DisposeCalls == 1; }, "Withheld startup keys still enforce missing, malformed and disconnected input checks");
                Check(output.Nonzero == 0 && output.Invalid == 0 && Neutral(output.Last), "Startup input failure disconnects without treating bad evidence as a release");
                f.Input.Reading = true; f.Input.Set(14, 0, 0); f.RemainsOff(1, "Fresh values after a startup input fault never reconnect automatically");
            }
        }
        using (var f = new Fixture(false))
        {
            f.Input.Set(14, 80, 0); var output = f.Arm(); f.Session.Disable("F8"); f.Input.Set(14, 0, 0);
            Check(output.DisposeCalls == 1 && Neutral(output.Last), "Emergency stop disconnects even while every binding waits for startup release");
            f.RemainsOff(1, "Releasing a startup key after emergency stop cannot reactivate its device");
        }
    }

    static void PreviewDemand()
    {
        using (var f = new Fixture(false))
        {
            FakeOutput output = f.Arm(); f.Input.Set(14, 80, 0);
            Wait(delegate { return f.Session.Preview.LeftY == .8 && output.Last.LeftY == .8; }, "Visible preview and actual output initially observe the same input.");
            long before = f.Session.PreviewComputations;
            int submitted = output.Submits;
            var elapsed = Stopwatch.StartNew(); Thread.Sleep(220);
            long computations = f.Session.PreviewComputations - before;
            Check(computations > 0 && computations <= Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 33) + 1,
                "Visible preview has its own 33ms cadence (updates=" + computations + ").");
            Check(output.Submits > submitted + 3, "Actual output continues independently of the slower visible preview.");
            f.Session.SetPreviewActive(false);
            before = f.Session.PreviewComputations; submitted = output.Submits;
            f.Input.Set(14, 35, 0);
            Wait(delegate { return output.Last.LeftY == .35 && output.Submits >= submitted + 5; }, "Hidden preview leaves current output and heartbeat submissions active.");
            Check(f.Session.PreviewComputations == before && f.Session.Preview.BindingResults.Count == 0,
                "Hidden preview performs no composition and exposes no retained display state.");
            Check(f.Session.Enabled && output.NeutralCalls == 1 && output.DisposeCalls == 0,
                "Hiding the preview does not neutralize or disconnect its controller.");
            f.Session.SetPreviewActive(true);
            Wait(delegate { return f.Session.Preview.LeftY == .35; }, "Reactivated preview resumes from the latest actual input.");
        }
        using (var f = new Fixture(false))
        using (var firstVisible = new ManualResetEvent(false))
        {
            f.Profile.Bindings[0].Processing.SmoothingTimeConstant = .2;
            f.Session.Configure(f.Profile, f.Calibrations);
            FakeOutput output = f.Arm(); f.Input.Set(14, 100, 0);
            Wait(delegate { return output.Last.LeftY > .7 && f.Session.Preview.LeftY > .7; }, "Both independent smoothing histories have advanced before hiding.");
            f.Session.SetPreviewActive(false);
            long hiddenRevision = f.Session.PreviewComputations;
            Thread.Sleep(120);
            int observed = 0; double firstPreview = -1, liveOutput = -1;
            output.ObserveSubmit = delegate(ControllerFrame value) {
                if (f.Session.PreviewComputations <= hiddenRevision || Interlocked.CompareExchange(ref observed, 1, 0) != 0) return;
                firstPreview = f.Session.Preview.LeftY; liveOutput = value.LeftY; firstVisible.Set();
            };
            f.Session.SetPreviewActive(true);
            Check(firstVisible.WaitOne(1500), "Output observes the first reactivated display snapshot without UI scheduling.");
            output.ObserveSubmit = null;
            Check(firstPreview == 0 && liveOutput > .7, "Reactivation resets preview history and dt without resetting real output smoothing.");
            Wait(delegate { return f.Session.PreviewComputations >= hiddenRevision + 5; }, "Reactivated preview continues beyond its first update.");
            Check(f.Session.Preview.LeftY > .45, "Preview smoothing advances by its own 33ms intervals rather than output's 4ms dt.");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .5, PressMovement = .05, ReleaseMovement = .6 });
            f.Session.Configure(f.Profile, f.Calibrations); FakeOutput output = f.Arm(); f.Input.Set(14, 80, 0);
            Wait(delegate { return f.Session.Preview.LeftY > 0 && output.Last.LeftY > 0; }, "Rapid-trigger preview begins with genuine activation.");
            f.Session.SetPreviewActive(false); f.Input.Set(14, 20, 0);
            Wait(delegate { return Neutral(output.Last); }, "Actual rapid-trigger release still runs while hidden.");
            f.Input.Set(14, 70, 0);
            Wait(delegate { return output.Last.LeftY > 0; }, "Live rapid-trigger history reuses the actuation distance while hidden.");
            f.Input.Set(14, 30, 0);
            Wait(delegate { return output.Last.LeftY == .3; }, "Live RT can remain active below initial actuation while the release movement has not been reached.");
            f.Session.SetPreviewActive(true);
            Wait(delegate { return f.Session.Preview.InputResults.ContainsKey(14); }, "Reactivated rapid-trigger preview is computed.");
            Check(f.Session.Preview.LeftY == 0 && output.Last.LeftY > 0,
                "Preview reactivation starts fresh physical history without changing live rapid-trigger history.");
        }
        foreach (bool disconnect in new[] { false, true })
        using (var f = new Fixture(false))
        {
            FakeOutput output = f.Arm(); f.Input.Set(14, 80, 0);
            Wait(delegate { return output.Nonzero > 0; }, "Hidden safety test starts with active output.");
            f.Session.SetPreviewActive(false);
            long before = f.Session.PreviewComputations;
            if (disconnect) f.Input.Reading = false; else f.Input.Set(14, 80, 501);
            Wait(delegate { return !f.Session.Enabled && output.DisposeCalls > 0; }, "Hidden preview preserves " + (disconnect ? "input-loss" : "stale-input") + " disarming.");
            Check(Neutral(output.Last) && f.Session.PreviewComputations == before, "Safety neutralization does not require display computation.");
        }
    }

    static void DormantHiddenSession()
    {
        using (var f = new Fixture(false))
        {
            f.Session.SetPreviewActive(false);
            int reads = f.Input.SnapshotCalls; long previews = f.Session.PreviewComputations;
            Thread.Sleep(100);
            Check(f.Input.SnapshotCalls == reads && f.Session.PreviewComputations == previews,
                "A hidden disconnected session sleeps without polling input or composing preview.");
            f.Session.Configure(f.Profile, f.Calibrations);
            Thread.Sleep(45);
            Check(f.Input.SnapshotCalls == reads && f.Session.PreviewComputations == previews,
                "Configuration wakes a dormant session without creating unwanted input-processing ticks.");
            f.Input.Set(14, 70, 0); f.Session.SetPreviewActive(true);
            Wait(delegate { return f.Session.Preview.LeftY == .7; }, "Preview activation wakes a dormant worker with current input.");
            f.Session.SetPreviewActive(false); f.Input.Set(14, 0, 0);
            FakeOutput output = f.Arm(); f.Input.Set(14, 60, 0);
            Wait(delegate { return output.Last.LeftY == .6 && output.Submits >= 3; }, "Explicit Enable wakes a dormant hidden worker into normal active output.");
            Check(f.Session.Preview.BindingResults.Count == 0, "Enabled hidden output does not reactivate display work.");
            f.Session.Disable("test dormant stop"); reads = f.Input.SnapshotCalls;
            Thread.Sleep(100);
            Check(f.Input.SnapshotCalls == reads, "Disabling hidden output returns its worker to indefinite event wait.");
        }
        using (var f = new Fixture(false))
        {
            f.Session.SetPreviewActive(false);
            int captures = 0;
            f.Session.SetReader(new ReaderSession { IsReading = true, Snapshot = delegate(double age) {
                Interlocked.Increment(ref captures); return new Dictionary<int, double> { { 14, 0 }, { 9, 0 } };
            } });
            Thread.Sleep(60);
            Check(captures == 0, "Replacing the reader preserves dormant hidden state without capturing new input.");
            f.Session.SetPreviewActive(true);
            Wait(delegate { return Interlocked.CompareExchange(ref captures, 0, 0) > 0 && f.Session.Preview.HasValidInput; },
                "Preview activation after a source change wakes against the new reader.");
            f.Session.SetPreviewActive(false);
            using (var finished = new ManualResetEvent(false))
            {
                Exception failure = null;
                var disposer = new Thread(delegate() { try { f.Session.Dispose(); } catch (Exception ex) { failure = ex; } finally { finished.Set(); } });
                disposer.IsBackground = true; disposer.Start();
                Check(finished.WaitOne(1500), "Dispose wakes and joins an indefinitely sleeping worker.");
                Check(failure == null, "Dormant worker disposal succeeds without handle or wake races.");
                disposer.Join();
            }
        }
    }

    static void AsyncConnections()
    {
        PendingReplyCancellation();
        using (var f = new Fixture(false))
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Reject(delegate { f.Session.EnableAsync(cancelled.Token); }, "An already-canceled request is rejected before creating an output.");
            Check(f.Outputs.Count == 0 && !f.Session.Connecting, "Rejected pre-cancellation reserves no candidate.");
            // The concrete process backend checks cancellation before any path,
            // job, process or native operation, even for a nonexistent helper.
            using (var isolated = new IsolatedOutput(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synthetic-never-launch-controller.exe"), "", 100, 100))
            {
                bool canceled = false;
                try { isolated.Connect(cancelled.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && isolated.HostProcessId == null, "Canceled isolated connection never starts or opens an output host.");
            }
        }
        foreach (string mutation in new[] { "cancel", "disable", "configure", "source", "mode", "dispose" })
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            f.PreparingOutput = delegate(FakeOutput candidate) {
                candidate.CancellableConnectHook = delegate(CancellationToken token) { entered.Set(); release.WaitOne(); };
            };
            Task connection = f.Session.EnableAsync(CancellationToken.None);
            try
            {
                Check(entered.WaitOne(1500), "Asynchronous connection enters its private candidate.");
                Task same = f.Session.EnableAsync(CancellationToken.None);
                Check(Object.ReferenceEquals(connection, same) && f.Outputs.Count == 1, "Repeated connect requests reuse exactly one pending candidate.");
                Task action = Task.Factory.StartNew(delegate {
                    Check(f.Session.Connecting && !f.Session.Enabled, "Pending connection getters do not wait for native Connect.");
                    string status = f.Session.Status; var frame = f.Session.Frame; var preview = f.Session.Preview;
                    if (mutation == "cancel") f.Session.CancelPendingConnection();
                    else if (mutation == "disable") f.Session.Disable("cancel pending");
                    else if (mutation == "configure") f.Session.Configure(f.Profile, f.Calibrations);
                    else if (mutation == "source") f.Session.SetReader(new ReaderSession { IsReading = true, Snapshot = delegate { return new Dictionary<int, double> { { 14, 0 } }; } });
                    else if (mutation == "mode") f.Session.SetKeyboardMode(true);
                    else f.Session.Dispose();
                });
                Check(action.Wait(1500), mutation + " returns while the candidate is still blocked inside Connect.");
                Task drain = f.Session.CancelPendingConnection();
                Check(!drain.IsCompleted && !connection.IsCompleted, "Uncooperative late candidate remains owned until real cleanup finishes.");
                release.Set();
                Wait(delegate { return connection.IsCompleted && drain.IsCompleted; }, "Late canceled connection drains after returning from Connect.");
                Check(connection.IsCanceled && !f.Session.Enabled && !f.Session.Connecting,
                    mutation + " prevents publication of a late successful native connection.");
                Check(f.Outputs[0].DisposeCalls == 1 && f.Outputs[0].NeutralCalls == 1 && f.Outputs[0].Submits == 0,
                    "Canceled unpublished candidate is neutralized and disposed exactly once, without mapped frames.");
                if (mutation == "cancel") Check(f.Session.Status == "Controller-Verbindung abgebrochen.",
                    "Finished direct cancellation does not leave a stale connecting status.");
            }
            finally { release.Set(); }
        }
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var cancellation = new CancellationTokenSource())
        {
            f.PreparingOutput = delegate(FakeOutput candidate) {
                candidate.CancellableConnectHook = delegate(CancellationToken token) {
                    entered.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
                };
            };
            Task pending = f.Session.EnableAsync(cancellation.Token);
            Check(entered.WaitOne(1500), "Cancellable backend received its connection token.");
            cancellation.Cancel();
            Wait(delegate { return pending.IsCompleted; }, "External cancellation interrupts a cooperative native connection.");
            Check(pending.IsCanceled && f.Outputs[0].DisposeCalls == 1 && !f.Session.Enabled,
                "Caller cancellation is reported only after owned candidate cleanup.");
            f.PreparingOutput = null;
            Task retry = f.Session.EnableAsync(CancellationToken.None);
            Check(retry.Wait(1500) && f.Session.Enabled && f.Outputs.Count == 2,
                "A cleaned canceled attempt permits one explicit fresh retry.");
        }
        using (var first = new Fixture(false))
        using (var second = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            FakeOutput peer = first.Arm(); first.Input.Set(14, 80, 0);
            second.PreparingOutput = delegate(FakeOutput candidate) { candidate.CancellableConnectHook = delegate { entered.Set(); release.WaitOne(); }; };
            Task pending = second.Session.EnableAsync(CancellationToken.None);
            try
            {
                Check(entered.WaitOne(1500), "A second controller is pending while the first remains connected.");
                int before = peer.Submits; first.Input.Set(14, 30, 0);
                Wait(delegate { return peer.Last.LeftY == .3 && peer.Submits >= before + 5; }, "Existing peer continues current frames and heartbeat during another Connect.");
                second.Session.CancelPendingConnection(); release.Set();
                Wait(delegate { return pending.IsCompleted; }, "Second controller cancellation completes independently.");
                Check(pending.IsCanceled && first.Session.Enabled && peer.DisposeCalls == 0,
                    "Canceling a pending controller never neutralizes or removes its connected peer.");
            }
            finally { release.Set(); }
        }
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int connectionReads = 0;
            f.PreparingOutput = delegate(FakeOutput candidate) {
                candidate.ReadConnectionHook = delegate {
                    if (Interlocked.Increment(ref connectionReads) == 2) { entered.Set(); release.WaitOne(); }
                };
            };
            Task pending = f.Session.EnableAsync(CancellationToken.None);
            try
            {
                Check(entered.WaitOne(1500), "The private candidate pauses at its last connection check before publication.");
                ControllerRelease detached = null;
                Task stop = Task.Factory.StartNew(delegate { detached = f.Session.PrepareDisable("stop before publication"); });
                Check(stop.Wait(1500), "PrepareDisable does not wait for a private candidate's connection getter.");
                Check(detached == null && !f.Session.Enabled, "Unpublished output has no transferred release ownership.");
                release.Set();
                Wait(delegate { return pending.IsCompleted; }, "Canceled final-check candidate finishes owned cleanup.");
                Check(pending.IsCanceled && !f.Session.Enabled && f.Outputs[0].Submits == 0 && f.Outputs[0].DisposeCalls == 1,
                    "Cancellation before publication cannot be followed by a late active output.");
            }
            finally { release.Set(); }
        }
        using (var f = new Fixture(false))
        {
            f.Session.SetPreviewActive(false);
            f.PreparingOutput = delegate(FakeOutput candidate) {
                candidate.ConnectHook = delegate { f.Input.SnapshotHook = delegate { f.Session.SetKeyboardMode(true); }; };
            };
            Task pending = f.Session.EnableAsync(CancellationToken.None);
            Wait(delegate { return pending.IsCompleted; }, "A mode change during final input validation completes its canceled candidate.");
            Check(pending.IsCanceled && !f.Session.Enabled && f.Session.KeyboardMode && f.Outputs[0].Submits == 0 && f.Outputs[0].DisposeCalls == 1,
                "Generation is rechecked after the final snapshot, so a validation-time mode change cannot publish stale output.");
        }
    }

    sealed class SyntheticReplyPipe : System.IO.TextReader
    {
        readonly System.Collections.Concurrent.BlockingCollection<char> data = new System.Collections.Concurrent.BlockingCollection<char>();
        public void Feed(string text) { foreach (char character in text) data.Add(character); }
        public void Complete() { data.CompleteAdding(); }
        public override int Read() { char character; return data.TryTake(out character, Timeout.Infinite) ? character : -1; }
        protected override void Dispose(bool disposing) { if (disposing) data.Dispose(); base.Dispose(disposing); }
    }
    static void PendingReplyCancellation()
    {
        // Exercise the real reply reader with an in-memory blocking pipe. No
        // process, job or device is created, and cancellation must not close the
        // pipe: the normal shutdown path still needs to drain the host's reply.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(IsolatedOutput).GetNestedType("ReplyChannel", System.Reflection.BindingFlags.NonPublic);
        var wait = type.GetMethod("Wait", flags, null, new[] { typeof(int), typeof(CancellationToken) }, null);
        var take = type.GetMethod("TryTake", flags);
        var close = type.GetMethod("Close", flags);
        using (var input = new SyntheticReplyPipe())
        using (var cancellation = new CancellationTokenSource())
        {
            object channel = Activator.CreateInstance(type, flags, null, new object[] { input, System.IO.TextReader.Null }, null);
            try
            {
                Exception failure = null;
                Task awaiting = Task.Factory.StartNew(delegate {
                    try { wait.Invoke(channel, new object[] { 15000, cancellation.Token }); }
                    catch (System.Reflection.TargetInvocationException ex) { failure = ex.InnerException; }
                });
                cancellation.Cancel();
                Check(awaiting.Wait(1500) && failure is OperationCanceledException,
                    "Canceling a pending CONNECT reply wakes its long command wait without killing or disposing its pipe.");
                input.Feed(OutputWire.Version + " 1 ACK\n");
                Check((bool)wait.Invoke(channel, new object[] { 1500, CancellationToken.None }),
                    "The existing reply reader remains available for the host's ordered cleanup after cancellation.");
                object[] reply = { null };
                Check((bool)take.Invoke(channel, reply) && reply[0] != null,
                    "A real protocol ACK is still parsed and drained after the canceled wait.");
            }
            finally { input.Complete(); close.Invoke(channel, null); }
        }
    }

    public static string Run()
    {
        checks = 0;
        PreviewDemand();
        DormantHiddenSession();
        AsyncConnections();
        using (var f = new Fixture(false))
        {
            FakeOutput first = f.Arm();
            ControllerRelease delayed = f.Session.PrepareDisable("previous controller disconnected");
            try
            {
                f.Input.Set(14, 85, 0);
                FakeOutput next = f.Arm();
                Wait(delegate { return next.Submits >= 3; }, "A reconnected controller starts with neutral heartbeats while its key is held.");
                string currentStatus = f.Session.Status;
                Check(next.Nonzero == 0, "The new connection guards the initially held key.");
                delayed.Neutral(); delayed.Dispose();
                int submitted = next.Submits;
                Wait(delegate { return next.Submits >= submitted + 3; }, "The new controller continues after old cleanup finishes.");
                Check(next.Nonzero == 0 && f.Session.Status == currentStatus && next.DisposeCalls == 0,
                    "Old release cleanup cannot clear the new held-key guard, overwrite its status, or remove its output.");
                Check(first.DisposeCalls == 1, "Delayed release removes only its original output.");
                f.Input.Set(14, 0, 0);
                Wait(delegate { return f.Session.Status == "Virtueller Controller aktiv"; }, "The held key becomes ready after its actual release.");
                f.Input.Set(14, 85, 0);
                Wait(delegate { return next.Nonzero > 0; }, "A fresh press produces output after delayed cleanup.");
            }
            finally { delayed.Dispose(); }
        }
        using (var f = new Fixture(false))
        {
            FakeOutput output = f.Arm(); f.Input.Set(14, 85, 0);
            Wait(delegate { return output.Nonzero > 0; }, "Two-phase release starts with real worker output.");
            ControllerRelease pending = f.Session.PrepareDisable("group stop");
            int neutrals = output.NeutralCalls;
            Check(pending != null && !f.Session.Enabled && output.DisposeCalls == 0, "PrepareDisable detaches without slow backend cleanup.");
            pending.Neutral();
            Check(output.NeutralCalls == neutrals + 1 && Neutral(output.Last) && output.DisposeCalls == 0, "Neutralization is a separate observable phase.");
            int submitted = output.Submits;
            Thread.Sleep(25); Check(output.Submits == submitted, "Neutralized detached output receives no later worker frames.");
            pending.Dispose(); pending.Dispose();
            Check(output.DisposeCalls == 1 && output.AfterDispose == 0, "Release token removes its owned backend exactly once.");
            Check(f.Session.PrepareDisable("already stopped") == null, "Repeated group preparation has no output left to release.");
        }
        using (var f = new Fixture(false))
        {
            Thread.Sleep(70);
            int before = f.Input.SnapshotCalls; Thread.Sleep(250); int idle = f.Input.SnapshotCalls - before;
            Check(idle > 0 && idle < 30 && f.Session.RequestedWaitMilliseconds == 16, "Visible disconnected sessions request 16-ms waits and continue capturing input.");
            FakeOutput output = f.Arm(); Wait(delegate { return output.Submits > 0; }, "Activation wakes the session and starts output.");
            before = f.Input.SnapshotCalls; Thread.Sleep(250); int active = f.Input.SnapshotCalls - before;
            // Windows may round both timeouts to the same timer quantum. Assert
            // the real requested wait and continuing work, not a fictitious OS
            // guarantee that a four-millisecond timeout always wakes in four ms.
            Check(active > 0 && f.Session.RequestedWaitMilliseconds == 4, "Connected output requests 4-ms waits and continues processing (idle=" + idle + ", active=" + active + ").");
        }
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            FakeOutput output = f.Arm();
            output.SubmitHook = delegate { entered.Set(); if (!release.WaitOne(1500)) throw new TimeoutException("Synthetic in-flight frame"); };
            Check(entered.WaitOne(1000), "An actual mapping worker has a controlled in-flight Submit.");
            var watch = Stopwatch.StartNew(); ControllerRelease pending = f.Session.PrepareDisable("group stop");
            Check(pending != null && watch.ElapsedMilliseconds < 250, "Group preparation does not wait behind one blocked output.");
            Exception failure = null;
            var neutralizer = new Thread(delegate() { try { pending.Neutral(); } catch (Exception error) { failure = error; } });
            int neutralBefore = output.NeutralCalls; neutralizer.Start();
            try { Thread.Sleep(20); Check(output.NeutralCalls == neutralBefore, "Neutralization drains the already-running Submit before neutral output."); }
            finally { release.Set(); Check(neutralizer.Join(1000), "Neutralization completes after the in-flight frame exits."); }
            Check(failure == null && Neutral(output.Last), "The drained output is left neutral.");
            int submitted = output.Submits; pending.Dispose(); Thread.Sleep(20);
            Check(output.Submits == submitted && output.AfterDispose == 0, "Group release cannot send another frame after neutral/removal.");
        }
        InitiallyUnknownInputs();
        StartupReleaseGate();
        using (var f = new Fixture(true))
        {
            var output = f.Arm(); f.Input.Set(14, 65, 0);
            Wait(delegate { return output.Last.LeftY == .65; }, "Live mapped value exists before keyboard mode");
            f.Session.SetKeyboardMode(true);
            Check(f.Session.KeyboardMode && f.Session.Enabled && output.DisposeCalls == 0 && Neutral(output.Last), "Keyboard mode synchronously neutralizes without removing the device");
            int submits = output.Submits, nonzero = output.Nonzero;
            f.Input.Set(14, 85, 0); f.Input.Remove(9);
            Wait(delegate { return output.Submits >= submits + 3; }, "Keyboard mode keeps a neutral frame heartbeat when an unused key is missing");
            Check(output.Nonzero == nonzero && output.Invalid == 0 && f.Session.Enabled && Neutral(f.Session.Frame), "Keyboard mode never submits mapped values and keeps valid reader connected");
            Wait(delegate { return f.Session.Preview.LeftY == .85 && f.Session.Preview.HasValidInput; }, "Input preview remains independent of the keyboard-mode output pause");
            f.Input.Remove(14); submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3; }, "An empty detached input snapshot still permits neutral-only output while reader is valid");
            Wait(delegate { return f.Session.Enabled && !f.Session.Preview.HasValidInput; }, "Empty paused input is not faked as a measured value");
            f.Input.Set(14, 85, 0);
            Reject(delegate { f.Session.SetKeyboardMode(false); }, "Resuming with missing required input must fail strict validation");
            Check(!f.Session.Enabled && output.DisposeCalls == 1 && Neutral(output.Last), "Failed resume removes the output without sending a partial value");
            f.Input.Set(9, 0, 0); f.Input.Set(14, 0, 0); f.RemainsOff(1, "Restoring input after failed resume cannot reconnect automatically");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .5, PressMovement = .02, ReleaseMovement = .8 });
            f.Session.Configure(f.Profile, f.Calibrations);
            var output = f.Arm(); f.Input.Set(14, 60, 0);
            Wait(delegate { return output.Last.LeftY == .6 && f.Session.Preview.LeftY == .6; }, "Rapid trigger actuates in output and independent preview before pause");
            f.Session.SetKeyboardMode(true); f.Input.Set(14, 30, 0);
            Wait(delegate { return f.Session.Preview.LeftY == .3; }, "Independent preview may retain its real rapid-trigger history while paused");
            f.Session.SetKeyboardMode(false); int submits = output.Submits;
            Wait(delegate { return output.Submits >= submits + 3; }, "Mapped worker resumes on the original connected output");
            Check(f.Session.Enabled && output.DisposeCalls == 0 && Neutral(output.Last) && f.Session.Preview.LeftY == 0, "Resume discards old rapid-trigger activation below the first threshold in output and preview");
            f.Input.Set(14, 70, 0);
            Wait(delegate { return output.Last.LeftY == .7; }, "Fresh physical actuation works after resume");
            f.Session.SetKeyboardMode(true); f.Session.Disable("F8");
            Check(!f.Session.Enabled && output.DisposeCalls == 1, "F8 still removes a paused controller");
            f.Session.SetKeyboardMode(false); f.RemainsOff(1, "Changing mode after F8 cannot reconnect output");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Bindings[0].Processing.SmoothingTimeConstant = .1;
            f.Session.Configure(f.Profile, f.Calibrations);
            var output = f.Arm(); f.Input.Set(14, 100, 0);
            Wait(delegate { return output.Last.LeftY > .8; }, "Smoothing accumulates a nonzero state before pause");
            f.Session.SetKeyboardMode(true); f.Input.Set(14, 10, 0); f.Session.SetKeyboardMode(false);
            int submits = output.Submits; Wait(delegate { return output.Submits > submits; }, "Smoothed output resumes");
            Check(output.Last.LeftY >= 0 && output.Last.LeftY <= .1, "Resume smoothing starts fresh without tail from the old high value");
        }
        using (var f = new Fixture(true))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed });
            f.Session.Configure(f.Profile, f.Calibrations);
            var output = f.Arm(); f.Input.Set(14, 100, 0);
            Wait(delegate { return output.Last.LeftY == 1; }, "First physical direction is active before pause");
            f.Input.Set(9, 100, 0); Wait(delegate { return output.Last.LeftY == -1; }, "Last-pressed order is established before pause");
            f.Session.SetKeyboardMode(true); f.Session.SetKeyboardMode(false);
            int submits = output.Submits; Wait(delegate { return output.Submits >= submits + 3; }, "SOCD worker resumes");
            Check(Neutral(output.Last), "Fresh simultaneous held directions cannot inherit a stale last-pressed winner");
        }
        using (var f = new Fixture(false))
        {
            f.Session.SetKeyboardMode(true); f.Session.Configure(f.Profile, f.Calibrations);
            Check(f.Session.KeyboardMode, "Configuration preserves keyboard-mode preference");
            var output = f.Arm(); f.Input.Set(14, 50, 0);
            Wait(delegate { return output.Submits > 2; }, "Newly enabled controller honors the existing keyboard mode");
            Check(output.Nonzero == 0 && output.Invalid == 0 && f.Session.Status.Contains("Tastaturmodus"), "Paused activation sends only valid neutral heartbeat frames and reports connected pause");
            f.Input.Reading = false;
            Wait(delegate { return output.DisposeCalls == 1; }, "Lost reader still disconnects in keyboard mode");
            f.Input.Reading = true; f.RemainsOff(1, "Reader recovery during keyboard mode never reconnects output");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); output.ThrowNeutral = true;
            Reject(delegate { f.Session.SetKeyboardMode(true); }, "Synchronous neutral failure is reported to mode caller");
            Check(f.Session.KeyboardMode && !f.Session.Enabled && output.DisposeCalls == 1, "Neutral failure leaves the requested safe mode and removes output");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); f.Session.SetKeyboardMode(true); output.ThrowSubmit = true;
            Wait(delegate { return output.DisposeCalls == 1; }, "A failed neutral heartbeat still disconnects output");
            f.RemainsOff(1, "Neutral heartbeat failure is not retried as a new connection");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); f.Session.SetKeyboardMode(true); f.Input.Set(14, Double.NaN, 0);
            Wait(delegate { return output.DisposeCalls == 1; }, "Present invalid raw data still disarms paused output");
            Check(Neutral(output.Last), "Malformed keyboard-mode input never reaches mapped output");
        }
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var switched = new ManualResetEvent(false))
        {
            var output = f.Arm(); f.Input.Set(14, 70, 0);
            output.SubmitHook = delegate { entered.Set(); if (!release.WaitOne(1000)) throw new TimeoutException("Synthetic Submit timeout"); };
            Check(entered.WaitOne(1000), "Controlled mapped Submit is in flight before mode switch");
            Exception modeError = null;
            var switcher = new Thread(delegate() { try { f.Session.SetKeyboardMode(true); } catch (Exception ex) { modeError = ex; } finally { switched.Set(); } });
            switcher.Start();
            try { Check(!switched.WaitOne(20), "Mode setter waits for prior Submit before its synchronous neutral boundary"); }
            finally { release.Set(); }
            Check(switched.WaitOne(1000) && switcher.Join(1000), "Mode transition completes after in-flight Submit drains");
            Check(modeError == null && f.Session.Enabled && Neutral(output.Last), "Successful mode return guarantees neutral on the same connected device");
            int submits = output.Submits, nonzero = output.Nonzero;
            Wait(delegate { return output.Submits >= submits + 3; }, "Worker renews frame heartbeat after synchronous mode transition");
            Check(output.Nonzero == nonzero && Neutral(output.Last), "No deferred mapped packet escapes after keyboard-mode return");
            f.Session.SetKeyboardMode(false);
            Wait(delegate { return output.Last.LeftY == .7; }, "Resuming uses current valid held input on the same device");
            Check(output.DisposeCalls == 0 && f.Outputs.Count == 1, "Mode toggling creates no replacement virtual controller");
        }
        using (var f = new Fixture(false, true))
        {
            var xbox = f.Arm();
            Check(f.RequestedControllers.Count == 1 && f.RequestedControllers[0] == ControllerKind.Xbox360, "Legacy profile requests Xbox explicitly from typed factory");
            f.Input.Set(14, 50, 0);
            Wait(delegate { return xbox.Last.LeftY == .5; }, "Xbox fake receives live fraction before changing controller");
            f.Profile.Controller = ControllerKind.DualSense;
            f.Session.Configure(f.Profile, f.Calibrations);
            Check(!f.Session.Enabled && xbox.DisposeCalls == 1 && Neutral(xbox.Last), "Changing controller disarms and neutralizes existing output");
            f.RemainsOff(1, "Changing controller never creates replacement automatically");
            var dual = f.Arm();
            Wait(delegate { return dual.Submits >= 3; }, "Changed controller connects neutrally while an old key remains held");
            Check(dual.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "Changing controller cannot carry a held key into its new output");
            Check(f.RequestedControllers.Count == 2 && f.RequestedControllers[1] == ControllerKind.DualSense, "Explicit activation forwards DualSense selection exactly");
            f.Input.Set(14, 0, 0);
            Wait(delegate { return !f.Session.Status.Contains("Loslassen"); }, "Released key becomes ready on the selected controller");
            f.Input.Set(14, 75, 0);
            Wait(delegate { return dual.Last.LeftY == .75; }, "Selected DualSense fake receives the same normalized mapping");
            f.Profile.Controller = ControllerKind.Xbox360;
            f.Session.Disable("Synthetic pause"); f.Input.Set(14, 0, 0);
            f.Arm();
            Check(f.RequestedControllers[2] == ControllerKind.DualSense, "Mutating caller-owned controller choice cannot change configured selection");
            var invalid = ProfileJson.Clone(f.Profile); invalid.Controller = (ControllerKind)99;
            Reject(delegate { f.Session.Configure(invalid, f.Calibrations); }, "Unknown controller choice is rejected");
            Check(!f.Session.Enabled && f.Outputs[2].DisposeCalls == 1, "Rejected controller choice disarms the prior output");
            f.RemainsOff(3, "Rejected controller choice cannot trigger an Xbox fallback");
        }
        using (var f = new Fixture(false, true))
        {
            f.Profile.Controller = ControllerKind.DualSense; f.Session.Configure(f.Profile, f.Calibrations);
            f.PreparingController = delegate(ControllerKind kind) { throw new InvalidOperationException("Synthetic selected controller unavailable"); };
            Reject(f.Session.Enable, "Unavailable selected controller propagates factory error");
            Check(f.RequestedControllers.Count == 1 && f.RequestedControllers[0] == ControllerKind.DualSense && f.Outputs.Count == 0, "Unavailable DualSense never retries Xbox or creates an unselected output");
            Check(f.Session.Status.Contains("Synthetic selected controller unavailable") && Neutral(f.Session.Frame), "Unavailable selection leaves useful status and neutral preview");
            f.RemainsOff(0, "Unavailable selection remains disarmed without automatic fallback");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Controller = ControllerKind.DualSense; f.Session.Configure(f.Profile, f.Calibrations);
            Reject(f.Session.Enable, "Untyped legacy factory cannot silently accept a different controller");
            Check(f.Outputs.Count == 0 && !f.Session.Enabled, "Legacy adapter rejects alternate controller before invoking its output factory");
        }
        using (var f = new Fixture(false))
        {
            f.RemainsOff(0, "Construction and Configure never create an output");
            var output = f.Arm();
            Wait(delegate { return output.Submits > 0; }, "Worker emits synthetic states");
            Check(output.Nonzero == 0 && output.NeutralCalls == 1, "Activation has no startup impulse");
            f.Input.Set(14, 50, 0);
            Wait(delegate { return Math.Abs(output.Last.LeftY - 0.5) < 0.0001; }, "Physical fraction reaches correct output");
            var view = f.Session.Frame; view.LeftY = 123; view.Errors.Add("external mutation");
            if (view.BindingResults.ContainsKey("w")) view.BindingResults["w"].Final = 456;
            var second = f.Session.Frame;
            Check(second.LeftY != 123 && second.Errors.Count == 0 && second.BindingResults["w"].Final != 456, "Preview is a detached deep snapshot");
            f.Session.Disable("Ruhezustand - Controller aus");
            Check(output.DisposeCalls == 1 && output.NeutralCalls == 2 && Neutral(output.Last), "Power pause neutralizes and disposes output");
            f.Input.Set(14, 0, 0); f.RemainsOff(1, "Wake/fresh release does not automatically rearm");
            f.Session.Dispose(); f.Session.Dispose();
            Check(output.DisposeCalls == 1 && output.AfterDispose == 0, "Repeated session Dispose is harmless");
            Reject(f.Session.Enable, "Disposed session cannot enable");
            Reject(delegate { f.Session.Configure(f.Profile, f.Calibrations); }, "Disposed session cannot configure");
        }
        using (var f = new Fixture(true))
        {
            f.Input.Set(14, 100, 0); f.Input.Set(9, 100, 0);
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "Opposing held keys allow a neutral controller connection");
            f.Input.Set(14, 0, 0);
            int submits = output.Submits; Wait(delegate { return output.Submits >= submits + 3; }, "One opposing startup key releases independently");
            Check(output.Nonzero == 0, "Releasing one opposing startup key cannot expose the other held direction");
        }
        using (var f = new Fixture(true))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 9 });
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14 });
            f.Session.Configure(f.Profile, f.Calibrations);
            f.Input.Set(14, 100, 0); f.Input.Set(9, 100, 0);
            Wait(delegate { var frame = f.Session.Frame; return frame.InputResults.Count == 2 && frame.InputResults[14].Active && frame.InputResults[9].Active; }, "Both physical input states reach preview before SOCD");
            Check(Neutral(f.Session.Frame), "SOCD suppresses opposing bindings in calculated preview");
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "SOCD-held keys connect neutrally without a modal rejection");
            f.Input.Set(14, 0, 0);
            int submits = output.Submits; Wait(delegate { return output.Submits >= submits + 3; }, "SOCD startup gate observes one released member");
            Check(output.Nonzero == 0 && f.Session.Status.Contains("Loslassen"), "SOCD pair remains withheld until each held startup member has released");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .2, PressMovement = .05, ReleaseMovement = .1 });
            f.Profile.Bindings[0].Processing.MinOutput = .3;
            f.Session.Configure(f.Profile, f.Calibrations);
            var output = f.Arm(); f.Input.Set(14, 60, 0);
            Wait(delegate { return output.Last.LeftY > .6 && f.Session.Frame.InputResults[14].Active; }, "Rapid trigger follows physical press");
            f.Input.Set(14, 45, 0);
            Wait(delegate { return output.Last.LeftY == 0 && !f.Session.Frame.InputResults[14].Active; }, "Physical lift immediately releases despite minimum output");
            Thread.Sleep(35);
            Check(output.Last.LeftY == 0, "Rapid-trigger state persists across unchanged worker frames");
            f.Input.Set(14, 55, 0); Thread.Sleep(35);
            Check(output.Last.LeftY == 0, "A shorter legacy press distance does not reactivate the live worker early");
            f.Input.Set(14, 65, 0);
            Wait(delegate { return output.Last.LeftY > 0 && f.Session.Frame.InputResults[14].Active; }, "Physical repress reactivates after moving the actuation distance from the new low point");
            var detached = f.Session.Frame; detached.InputResults[14].Allowed = false; detached.InputResults[14].Normalized = 99;
            Check(f.Session.Frame.InputResults[14].Allowed && f.Session.Frame.InputResults[14].Normalized <= 1, "Per-key preview results are detached copies");
            f.Input.Reading = false;
            Wait(delegate { return !f.Session.Enabled; }, "Lost input disables rapid-trigger output");
            f.Input.Set(14, 10, 0); f.Input.Reading = true;
            Wait(delegate { return f.Session.Frame.InputResults.ContainsKey(14); }, "Fresh per-key state after reconnection");
            Check(!f.Session.Frame.InputResults[14].Active, "Reconnect resets rapid-trigger history before a below-threshold value");
            f.RemainsOff(1, "Rapid-trigger input does not automatically rearm output");
        }
        using (var f = new Fixture(false))
        {
            f.Profile.Bindings[0].Processing.TopDeadzone = .1;
            f.Session.Configure(f.Profile, f.Calibrations); f.Input.Set(14, 5, 0);
            var output = f.Arm();
            Check(output.Nonzero == 0, "Legacy calibration rest deadzone still permits neutral arming");
        }
        using (var f = new Fixture(false))
        {
            f.Input.Set(14, 0, 1000000);
            var output = f.Arm();
            f.Input.Set(14, 100, 500);
            Wait(delegate { return output.Last.LeftY == 1; }, "Exactly 500ms remains usable");
            Check(f.Input.RequestedAge == 500, "Session requires the specified 500ms safety snapshot");
            f.Input.Set(14, 100, 500.001);
            Wait(delegate { return output.DisposeCalls == 1; }, "Expired positive disarms output");
            Check(output.NeutralCalls == 2 && Neutral(output.Last), "Expired positive becomes neutral output, not held forever");
            f.Input.Set(14, 0, 0); f.RemainsOff(1, "Fresh values after expiry never autoarm");
        }
        using (var f = new Fixture(true))
        {
            var output = f.Arm();
            f.Input.Remove(9); f.Input.Set(14, 100, 0);
            Wait(delegate { return output.DisposeCalls == 1 && f.Session.Frame.Errors.Count != 0; }, "One missing bound key disarms the entire controller");
            Check(output.Invalid == 0 && output.Nonzero == 0, "No partial frame with a valid pressed key reaches output");
            Check(Neutral(f.Session.Frame), "Invalid partial frame remains neutral on the strict output path");
            Wait(delegate { return f.Session.Preview.LeftY > 0 && f.Session.Preview.HasValidInput; }, "Missing different key must not blank the independent live preview");
            Check(f.Session.Preview.Errors.Count > 0 && !f.Session.Enabled, "Partial preview includes its warning without arming output");
            f.Input.Set(9, 0, 0); f.Input.Set(14, 0, 0); f.RemainsOff(1, "Restoring missing key does not autoarm");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); f.Input.Reading = false;
            Wait(delegate { return output.DisposeCalls == 1; }, "Input disconnect disables output");
            f.Input.Reading = true; f.RemainsOff(1, "Input reconnect alone leaves output disabled");
        }
        using (var f = new Fixture(false))
        {
            f.PreparingOutput = delegate(FakeOutput output) { output.ConnectHook = delegate { f.Input.Reading = false; }; };
            Reject(f.Session.Enable, "Disconnect during Connect prevents arming");
            Check(f.Outputs[0].DisposeCalls == 1 && f.Outputs[0].Submits == 0, "Connect-race candidate is cleaned without Submit");
        }
        using (var f = new Fixture(false))
        {
            f.PreparingOutput = delegate(FakeOutput candidate) { candidate.ConnectHook = delegate { f.Input.Set(14, 100, 0); }; };
            var output = f.Arm();
            Wait(delegate { return output.Submits >= 3; }, "A key pressed during slow Connect is held neutral on the connected output");
            Check(output.DisposeCalls == 0 && output.Nonzero == 0, "Connect-time key press produces neither an impulse nor a failed connection");
        }
        using (var f = new Fixture(false))
        {
            f.PreparingOutput = delegate(FakeOutput output) { output.ConnectHook = delegate { f.Input.SnapshotHook = delegate { f.Input.Reading = false; }; }; };
            Reject(f.Session.Enable, "Disconnect while reading the post-Connect snapshot is detected");
            Check(f.Outputs[0].DisposeCalls == 1 && !f.Session.Enabled, "Snapshot-race candidate is disposed");
        }
        using (var f = new Fixture(false))
        {
            f.PreparingOutput = delegate(FakeOutput output) { output.ConnectHook = delegate { throw new InvalidOperationException("Synthetic Connect failure"); }; };
            Reject(f.Session.Enable, "Partially successful Connect failure propagates");
            Check(f.Outputs[0].DisposeCalls == 1 && f.Outputs[0].NeutralCalls == 1 && f.Session.Status.Contains("Synthetic Connect failure"), "Connect failure cleans and preserves useful status");
        }
        using (var f = new Fixture(false))
        {
            f.PreparingOutput = delegate(FakeOutput output) { output.ConnectHook = output.Drop; };
            Reject(f.Session.Enable, "Connect must actually report a connected backend");
            Check(f.Outputs[0].DisposeCalls == 1, "Unconnected candidate is disposed");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); output.ThrowSubmit = true;
            Wait(delegate { return output.DisposeCalls == 1; }, "Submit failure disarms");
            Check(output.NeutralCalls == 2 && f.Session.Status.Contains("Synthetic Submit failure"), "Submit failure neutralizes and reports reason");
            f.Input.Set(14, 75, 0);
            Wait(delegate { return f.Session.Preview.LeftY == .75; }, "Preview continues after backend failure without creating a new output");
            f.RemainsOff(1, "Healthy input never retries output after failure");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); output.DropOnSubmit = true;
            Wait(delegate { return output.DisposeCalls == 1; }, "Silent loss of backend during Submit is detected");
            f.RemainsOff(1, "Backend drop never auto reconnects");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); output.ThrowNeutral = true; output.ThrowDispose = true;
            f.Session.Disable("Synthetic emergency pause");
            Check(!f.Session.Enabled && output.NeutralCalls == 2 && output.DisposeCalls == 1, "Cleanup continues even if Neutral and Dispose throw");
            f.RemainsOff(1, "Cleanup failure cannot retain armed session state");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm();
            f.Profile.Bindings[0].Target = OutputTarget.RightYPositive; f.Calibrations[14].Bottom = 400;
            f.Input.Set(14, 50, 0);
            Wait(delegate { return output.Last.LeftY == 0.5; }, "Configuration uses copies of profile and calibration");
            Check(output.Last.RightY == 0, "Mutating caller-owned profile cannot alter active target");
            f.Session.Configure(f.Profile, f.Calibrations);
            Check(!f.Session.Enabled && output.DisposeCalls == 1 && Neutral(output.Last), "Valid Configure immediately neutralizes existing output");
            f.RemainsOff(1, "Configuration change requires explicit Enable");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm();
            Reject(delegate { f.Session.Configure(null, f.Calibrations); }, "Invalid Configure is rejected");
            Check(!f.Session.Enabled && output.DisposeCalls == 1 && Neutral(output.Last), "Rejected Configure still disarms old output");
            f.RemainsOff(1, "Rejected configuration does not revive old output");
        }
        using (var f = new Fixture(false))
        {
            f.Input.Set(14, 75, 0);
            Wait(delegate { return f.Session.Preview.LeftY == .75; }, "Original key is observed before replacing its binding");
            f.Profile.Bindings[0].KeyIndex = 9;
            f.Calibrations.Remove(14); f.Input.Remove(14);
            f.Session.Configure(f.Profile, f.Calibrations);
            var output = f.Arm(); f.Input.Set(9, 55, 0);
            Wait(delegate { return output.Last.LeftY == .55; }, "Reconfiguration replaces required keys and their calibration requirements");
            f.Input.Remove(9);
            Wait(delegate { return output.DisposeCalls == 1; }, "A newly configured key still disarms output when its observed state disappears");
        }
        using (var f = new Fixture(false))
        {
            f.Input.Reading = false;
            Wait(delegate { return f.Session.Frame.Errors.Count != 0 && f.Session.Preview.Errors.Count != 0; }, "Disconnected frame and preview explain absent input");
            var frame = f.Session.Frame; frame.LeftY = 1; frame.Errors.Clear();
            var preview = f.Session.Preview; preview.LeftY = 1; preview.Errors.Clear();
            Check(Neutral(f.Session.Frame) && f.Session.Frame.Errors.Count == 1 && f.Session.Preview.LeftY == 0 && f.Session.Preview.Errors.Count == 1,
                "Consumer edits cannot corrupt retained disconnected snapshots");
        }
        using (var f = new Fixture(false))
        {
            var output = f.Arm(); f.Session.SetReader(null);
            Check(!f.Session.Enabled && output.DisposeCalls == 1, "SetReader replacement neutralizes previous output");
            Reject(f.Session.Enable, "Absent replacement reader cannot enable");
        }
        using (var f = new Fixture(false))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var configured = new ManualResetEvent(false))
        {
            var output = f.Arm();
            output.SubmitHook = delegate { entered.Set(); if (!release.WaitOne(1000)) throw new TimeoutException("Synthetic blocking Submit timeout"); };
            Check(entered.WaitOne(1000), "Controlled Submit entered");
            Exception editError = null;
            var editor = new Thread(delegate() { try { f.Session.Configure(f.Profile, f.Calibrations); } catch (Exception ex) { editError = ex; } finally { configured.Set(); } });
            editor.Start();
            try { Check(!configured.WaitOne(20), "Configure serializes behind in-flight Submit"); }
            finally { release.Set(); }
            Check(configured.WaitOne(1000) && editor.Join(1000), "Configure finishes after Submit drains");
            Check(editError == null && !f.Session.Enabled && output.DisposeCalls == 1 && Neutral(output.Last), "Configure leaves final output neutral after concurrent Submit");
            Thread.Sleep(20); Check(output.AfterDispose == 0, "No later worker Submit reaches disposed output");
        }
        return "PASS: " + checks + " fake MappingSession checks; no hardware, DLL load, driver, real reader, file store or virtual controller.";
    }
}
