using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tk75.App;
using Tk75.Mapping;

// New orchestration tests only. No production MappingSession, reader, native
// output, process launcher, GUI control or previously blocked test is loaded.
public static class MultiControllerSessionHarness
{
    // Deliberately controlled lifecycle: tests choose when cleanup completes.
    // No Task.Run, timer, output process or hidden device implementation here.
    sealed class AsyncFake : IControllerSession, IPreviewDemandSession, IAsyncControllerSession
    {
        internal readonly Fake Inner = new Fake();
        internal TaskCompletionSource<object> Request, Drain;
        internal bool Cancelled;
        internal int Starts;
        CancellationToken token;
        public bool Connecting { get { return Request != null; } }
        public bool Enabled { get { return Inner.Enabled; } }
        public ControllerFrame Frame { get { return Inner.Frame; } }
        public PreviewSnapshot Preview { get { return Inner.Preview; } }
        public string Status { get { return Connecting ? "connecting" : Inner.Status; } }
        public void SetPreviewActive(bool value) { Inner.SetPreviewActive(value); }
        public void Configure(Profile profile, IDictionary<int, Calibration> calibration) { CancelPendingConnection(); Inner.Configure(profile, calibration); }
        public void SetInputSource(object source) { CancelPendingConnection(); Inner.SetInputSource(source); }
        public void SetKeyboardMode(bool value) { if (Inner.KeyboardMode != value) CancelPendingConnection(); Inner.SetKeyboardMode(value); }
        public void Enable() { throw new Exception("Async endpoint must use its async API."); }
        public Task EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Request != null) return Request.Task;
            if (Enabled) return Task.FromResult(0);
            Starts++; Cancelled = false; token = cancellationToken;
            Request = new TaskCompletionSource<object>(); Drain = new TaskCompletionSource<object>();
            return Request.Task;
        }
        public Task CancelPendingConnection()
        { if (Request == null) return Task.FromResult(0); Cancelled = true; return Drain.Task; }
        public void Finish()
        {
            var request = Request; var drain = Drain; Request = null; Drain = null;
            bool canceled = Cancelled || token.IsCancellationRequested;
            Inner.Active = !canceled && !Inner.Disposed;
            drain.SetResult(null);
            if (canceled) request.SetCanceled(); else request.SetResult(null);
        }
        public void Disable(string reason) { CancelPendingConnection(); Inner.Disable(reason); }
        public ControllerRelease PrepareDisable(string reason) { CancelPendingConnection(); return Inner.PrepareDisable(reason); }
        public void Dispose() { CancelPendingConnection(); Inner.Dispose(); }
    }
    sealed class AsyncFixture : IDisposable
    {
        internal readonly List<AsyncFake> Created = new List<AsyncFake>();
        internal readonly MultiControllerSession Runtime;
        internal AsyncFixture(int limit)
        { Runtime = new MultiControllerSession(delegate { var value = new AsyncFake(); Created.Add(value); return value; }, limit); }
        internal AsyncFake Slot(string id)
        { foreach (var value in Created) if (!value.Inner.Disposed && value.Inner.Profile.Controllers[0].Id == id) return value; throw new Exception("Missing async slot " + id); }
        public void Dispose()
        {
            Runtime.Dispose();
            foreach (var value in Created) if (value.Connecting) value.Finish();
        }
    }
    sealed class Fake : IControllerSession, IPreviewDemandSession
    {
        public bool Active, Disposed, FailEnable, FailConfigure, FailInput, FailDisable, FailMode, KeyboardMode;
        public int ConfigureCalls, EnableCalls, DisableCalls, DisposeCalls, ModeCalls;
        public Action EnableHook, DisableHook;
        public Action NeutralHook, RemoveHook;
        public int NeutralCalls, RemoveCalls;
        public bool PreviewActive;
        public int PreviewDemandCalls;
        public Profile Profile;
        public object Source;
        public string LastReason;
        public ControllerFrame CurrentFrame = new ControllerFrame();
        public PreviewSnapshot CurrentPreview = new PreviewSnapshot();
        public bool Enabled { get { return Active && !Disposed; } }
        public ControllerFrame Frame { get { return CurrentFrame; } }
        public PreviewSnapshot Preview { get { return CurrentPreview.Copy(); } }
        public string Status { get { return Active ? "connected" : "disconnected"; } }
        public void SetPreviewActive(bool value) { PreviewActive = value; PreviewDemandCalls++; }
        public void Configure(Profile profile, IDictionary<int, Calibration> calibration)
        {
            if (Disposed) throw new ObjectDisposedException("Fake");
            ConfigureCalls++; Active = false;
            if (FailConfigure) throw new InvalidOperationException("configure failure");
            Profile = ProfileJson.Clone(profile);
        }
        public void SetInputSource(object source)
        {
            Active = false;
            if (FailInput) throw new InvalidOperationException("input failure");
            Source = source;
        }
        public void Enable()
        {
            if (EnableHook != null) EnableHook();
            EnableCalls++; Active = true;
            if (FailEnable) throw new InvalidOperationException("connect failed after allocating output");
        }
        public void SetKeyboardMode(bool value)
        {
            if (Disposed) throw new ObjectDisposedException("Fake");
            ModeCalls++; if (FailMode) throw new InvalidOperationException("mode neutral failure");
            KeyboardMode = value; CurrentFrame = new ControllerFrame();
        }
        public void Disable(string reason)
        {
            DisableCalls++;
            if (DisableHook != null) DisableHook();
            LastReason = reason;
            if (FailDisable) throw new InvalidOperationException("disable failure");
            Active = false;
        }
        public ControllerRelease PrepareDisable(string reason)
        {
            bool active = Active;
            Disable(reason);
            if (!active) return null;
            return new ControllerRelease(delegate { Interlocked.Increment(ref NeutralCalls); if (NeutralHook != null) NeutralHook(); },
                delegate { Interlocked.Increment(ref RemoveCalls); if (RemoveHook != null) RemoveHook(); });
        }
        public void Dispose() { DisposeCalls++; Disposed = true; Active = false; }
    }
    sealed class Fixture : IDisposable
    {
        public readonly List<Fake> Created = new List<Fake>();
        public readonly MultiControllerSession Runtime;
        public Fixture() : this(4) { }
        public Fixture(int limit)
        { Runtime = new MultiControllerSession(delegate { var value = new Fake(); Created.Add(value); return value; }, limit); }
        public Fake Slot(string id)
        { foreach (Fake value in Created) if (!value.Disposed && value.Profile.Controllers[0].Id == id) return value; throw new Exception("Missing fake slot " + id); }
        public void Dispose() { Runtime.Dispose(); }
    }
    static int checks;
    static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
    static void Reject(Action action, string reason)
    { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, reason); }
    static Dictionary<int, Calibration> Calibration()
    { return new Dictionary<int, Calibration> { { 1, new Calibration(0, 385) }, { 2, new Calibration(0, 385) } }; }
    static Profile TwoPlayers()
    {
        Profile profile = ControllerRouting.Add(new Profile(), "player2", "Player 2", ControllerKind.Xbox360);
        profile = MappingAssignments.Add(profile, new[] { 1 }, OutputTarget.LeftXPositive, "main");
        return MappingAssignments.Add(profile, new[] { 2 }, OutputTarget.RightTrigger, "player2");
    }
    static void AsyncReservationsAndRetiredCleanup()
    {
        using (var f = new AsyncFixture(1))
        {
            var runtime = f.Runtime;
            runtime.Configure(ControllerRouting.Add(TwoPlayers(), "ps", "PS", ControllerKind.DualSense), Calibration());
            var first = f.Slot("main"); var second = f.Slot("player2"); var ps = f.Slot("ps");
            Task pending = runtime.EnableControllerAsync("main", CancellationToken.None);
            Check(!pending.IsCompleted && runtime.IsControllerConnecting("main") && !runtime.IsControllerEnabled("main"), "Async connect reserves a pending slot without claiming a confirmed connection.");
            Check(Object.ReferenceEquals(pending, runtime.EnableControllerAsync("main", CancellationToken.None)) && first.Starts == 1, "Repeated async connect requests reuse the same slot attempt.");
            Check(runtime.Frame != null && runtime.Preview != null && runtime.Status == "connecting" && runtime.ActiveControllerIds.Length == 0,
                "Pending-state getters remain available and do not count unconfirmed controllers as active.");
            Reject(delegate { runtime.EnableControllerAsync("player2", CancellationToken.None); }, "Pending Xbox attempts consume capacity before confirmation.");
            Check(second.Starts == 0, "Rejected capacity creates no second pending Xbox output.");
            Task psPending = runtime.EnableControllerAsync("ps", CancellationToken.None);
            Check(ps.Starts == 1 && !psPending.IsCompleted, "PlayStation reservations do not consume Xbox slots.");
            Task drain = runtime.CancelPendingConnections();
            Check(!drain.IsCompleted && first.Cancelled && ps.Cancelled, "Global cancel signals all pending attempts immediately but retains their cleanup drain.");
            first.Finish(); Check(!drain.IsCompleted, "Global drain waits for every owned pending candidate.");
            ps.Finish(); Check(drain.IsCompleted && pending.IsCanceled && psPending.IsCanceled, "Global drain completes only after both candidate cleanups.");
            Task retry = runtime.EnableControllerAsync("player2", CancellationToken.None); second.Finish();
            Check(retry.IsCompleted && !retry.IsFaulted && second.Enabled, "Cleaned canceled reservations release capacity for an explicit new request.");
        }
        using (var f = new AsyncFixture(4))
        {
            var runtime = f.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            var first = f.Slot("main"); var second = f.Slot("player2");
            runtime.EnableControllerAsync("main", CancellationToken.None);
            Task other = runtime.EnableControllerAsync("player2", CancellationToken.None);
            Task disconnect = runtime.DisableControllerAsync("main", "cancel this slot");
            Check(first.Cancelled && !second.Cancelled && !disconnect.IsCompleted, "ID-based pending cancellation leaves the peer attempt untouched.");
            first.Finish();
            Check(disconnect.IsCompleted && !other.IsCompleted && !second.Cancelled, "One slot's disconnect never waits for an unrelated pending controller.");
            second.Finish(); Check(second.Enabled && runtime.IsControllerEnabled("player2"), "Untouched pending peer can complete normally.");
        }
        foreach (string change in new[] { "configure", "source", "mode", "dispose" })
        using (var f = new AsyncFixture(4))
        {
            var runtime = f.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            var old = f.Slot("player2");
            Task pending = runtime.EnableControllerAsync("player2", CancellationToken.None);
            if (change == "configure") runtime.Configure(new Profile(), new Dictionary<int, Calibration>());
            else if (change == "source") runtime.SetInputSource(new object());
            else if (change == "mode") runtime.KeyboardMode = true;
            else runtime.Dispose();
            Check(old.Cancelled && !pending.IsCompleted, change + " invalidates pending work without waiting for candidate completion.");
            Task drain = runtime.CancelPendingConnections();
            Check(!drain.IsCompleted, "Cleanup remains owned even after a connecting slot was removed or disposed.");
            old.Finish();
            Check(drain.IsCompleted && pending.IsCanceled && !runtime.IsControllerEnabled("player2"), change + " cannot publish a stale late candidate.");
        }
    }
    static void PreviewDemandFollowsVisibilityAndSelection()
    {
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.EnableController("main"); runtime.EnableController("player2");
            int firstConfigure = first.ConfigureCalls, secondConfigure = second.ConfigureCalls;
            int firstDisable = first.DisableCalls, secondDisable = second.DisableCalls;
            int firstMode = first.ModeCalls, secondMode = second.ModeCalls;
            Check(first.PreviewActive && !second.PreviewActive, "Only the selected controller initially computes a preview.");
            runtime.SetPreviewActive(false);
            Check(!first.PreviewActive && !second.PreviewActive, "Hidden display disables preview demand on all controllers.");
            runtime.SelectedControllerId = "player2";
            Check(!first.PreviewActive && !second.PreviewActive, "Changing selection while hidden cannot reactivate any preview.");
            runtime.SetPreviewActive(true);
            Check(!first.PreviewActive && second.PreviewActive, "Showing the display activates its current selection immediately.");
            int calls = first.PreviewDemandCalls + second.PreviewDemandCalls;
            runtime.SetPreviewActive(true); runtime.SelectedControllerId = "player2";
            Check(first.PreviewDemandCalls + second.PreviewDemandCalls == calls, "Repeated live refreshes do not reset preview demand or history.");
            for (int i = 0; i < 4; i++)
            {
                runtime.SelectedControllerId = "main";
                Check(first.PreviewActive && !second.PreviewActive, "First controller regains preview demand after a selection switch.");
                runtime.SelectedControllerId = "player2";
                Check(!first.PreviewActive && second.PreviewActive, "Second controller regains preview demand without starvation.");
            }
            Check(first.Enabled && second.Enabled && first.ConfigureCalls == firstConfigure && second.ConfigureCalls == secondConfigure &&
                first.DisableCalls == firstDisable && second.DisableCalls == secondDisable && first.ModeCalls == firstMode && second.ModeCalls == secondMode,
                "Preview visibility and selection do not configure, pause, disable or reconnect either output.");
            runtime.SetPreviewActive(false);
            runtime.Configure(ControllerRouting.Add(TwoPlayers(), "third", "Third", ControllerKind.DualSense), Calibration());
            Check(!first.PreviewActive && !second.PreviewActive && !fixture.Slot("third").PreviewActive,
                "Newly configured controllers inherit hidden preview demand.");
            runtime.SelectedControllerId = "third"; runtime.SetPreviewActive(true);
            Check(!first.PreviewActive && !second.PreviewActive && fixture.Slot("third").PreviewActive,
                "A new selected controller receives display demand when shown.");
            runtime.Configure(TwoPlayers(), Calibration());
            Check(runtime.SelectedControllerId == "main" && first.PreviewActive && !second.PreviewActive,
                "Removing the selected controller transfers preview demand to the valid fallback selection.");
        }
    }
    static void ControllerOperationsById()
    {
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime;
            runtime.Configure(ControllerRouting.Add(TwoPlayers(), "third", "Third player", ControllerKind.DualSense), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2"), third = fixture.Slot("third");
            first.CurrentFrame.LeftX = .25; first.CurrentPreview.LeftX = .5;
            second.CurrentFrame.RightTrigger = .75; second.CurrentPreview.RightTrigger = .9;
            runtime.Enable(); runtime.EnableController("third");
            Check(runtime.SelectedControllerId == "main" && first.Enabled && third.Enabled, "Connecting another ID preserves the selected controller and its existing output.");
            int firstEnables = first.EnableCalls, firstDisables = first.DisableCalls, firstConfigures = first.ConfigureCalls;
            int thirdEnables = third.EnableCalls, thirdDisables = third.DisableCalls, thirdConfigures = third.ConfigureCalls;
            string selectedDuringEnable = null, selectedDuringDisable = null;
            second.EnableHook = delegate { selectedDuringEnable = runtime.SelectedControllerId; };
            second.DisableHook = delegate { selectedDuringDisable = runtime.SelectedControllerId; };
            runtime.EnableController("player2");
            Check(selectedDuringEnable == "main" && runtime.SelectedControllerId == "main", "ID-based enable never temporarily selects its target inside the endpoint callback.");
            Check(runtime.IsControllerEnabled("main") && runtime.IsControllerEnabled("player2") && runtime.IsControllerEnabled("third"), "Connection queries address each controller independently of the selection.");
            Check(runtime.Frame.LeftX == .25 && runtime.Frame.RightTrigger == 0 && runtime.Preview.LeftX == .5 && runtime.Preview.RightTrigger == 0, "ID-based enable preserves the selected frame and preview.");
            int secondEnables = second.EnableCalls;
            runtime.EnableController("player2");
            Check(second.EnableCalls == secondEnables, "Enabling an already connected controller ID does not recreate its output.");
            Check(!runtime.IsControllerEnabled("missing") && !runtime.IsControllerEnabled(null), "Unknown or null controller IDs have no active connection.");
            Reject(delegate { runtime.EnableController("missing"); }, "Enabling an unknown controller ID must be rejected.");
            Reject(delegate { runtime.EnableController(null); }, "Enabling a null controller ID must be rejected.");
            Check(second.EnableCalls == secondEnables && runtime.SelectedControllerId == "main", "Rejected controller IDs cannot reach an endpoint or change the selection.");
            runtime.DisableController("player2", "row unplug");
            Check(selectedDuringDisable == "main" && runtime.SelectedControllerId == "main", "ID-based disable never temporarily selects its target inside the endpoint callback.");
            Check(!runtime.IsControllerEnabled("player2") && first.Enabled && third.Enabled && second.LastReason == "row unplug", "ID-based disconnect reaches only its named controller with the requested reason.");
            Check(first.EnableCalls == firstEnables && first.DisableCalls == firstDisables && first.ConfigureCalls == firstConfigures &&
                third.EnableCalls == thirdEnables && third.DisableCalls == thirdDisables && third.ConfigureCalls == thirdConfigures,
                "Operating another ID never enables, disables or configures existing unrelated outputs.");
            Check(runtime.Frame.LeftX == .25 && runtime.Preview.LeftX == .5 && second.CurrentPreview.RightTrigger == .9, "ID-based disconnect preserves selected and target input previews.");
            runtime.DisableController("missing", "ignored"); runtime.DisableController(null, "ignored");
            Check(first.Enabled && third.Enabled && runtime.SelectedControllerId == "main", "Unknown disconnect IDs are harmless to active controllers and selection.");
            runtime.Dispose();
            Check(!runtime.IsControllerEnabled("main") && !runtime.IsControllerEnabled("player2"), "Disposed orchestration exposes no active ID-based connections.");
        }
        using (var fixture = new Fixture(1))
        {
            var runtime = fixture.Runtime;
            runtime.Configure(ControllerRouting.Add(TwoPlayers(), "ps", "PS player", ControllerKind.DualSense), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2"), playstation = fixture.Slot("ps");
            runtime.Enable();
            Reject(delegate { runtime.EnableController("player2"); }, "ID-based connection obeys the configured Xbox backend limit.");
            Check(second.EnableCalls == 0 && first.Enabled && runtime.SelectedControllerId == "main", "ID-based capacity rejection allocates no output and preserves the active controller and selection.");
            runtime.EnableController("ps");
            Check(playstation.Enabled && first.Enabled && runtime.SelectedControllerId == "main", "Xbox capacity does not reject an independent PlayStation controller ID.");
            runtime.DisableController("main", "free Xbox capacity"); runtime.EnableController("player2");
            Check(!first.Enabled && second.Enabled && playstation.Enabled && runtime.SelectedControllerId == "main", "Freed Xbox capacity is reusable by ID while selection and other controller kinds remain unchanged.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); second.FailEnable = true;
            Reject(delegate { runtime.EnableController("player2"); }, "A partial ID-based connection failure remains visible.");
            Check(!second.Enabled && first.Enabled && runtime.SelectedControllerId == "main", "A failed ID-based enable cleans its partial output and preserves the selected controller.");
            second.FailEnable = false; runtime.EnableController("player2"); second.FailDisable = true;
            Reject(delegate { runtime.DisableController("player2", "row cleanup failure"); }, "An ID-based disconnect failure remains visible.");
            Check(second.Disposed && !runtime.IsControllerEnabled("player2") && first.Enabled && runtime.SelectedControllerId == "main", "A failed ID-based disconnect retires only its target and preserves the selected output.");
            int beforeRetry = second.EnableCalls;
            Reject(delegate { runtime.EnableController("player2"); }, "A retired controller ID rejects reactivation until reconfigured.");
            Check(second.EnableCalls == beforeRetry, "ID-based retry never calls the disposed endpoint.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); second.FailEnable = true; second.FailDisable = true;
            Reject(delegate { runtime.EnableController("player2"); }, "A failed ID-based connection with failed cleanup remains visible.");
            Check(second.Disposed && !runtime.IsControllerEnabled("player2") && first.Enabled && runtime.SelectedControllerId == "main", "Failed partial-connection cleanup disposes only its named slot and leaves existing output connected.");
        }
    }
    static void GroupReleasePhases()
    {
        foreach (bool dispose in new[] { false, true })
        foreach (bool slowNeutral in new[] { false, true })
        using (var fixture = new Fixture())
        using (var entered = new ManualResetEvent(false))
        using (var otherNeutral = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.EnableController("main"); runtime.EnableController("player2");
            Action block = delegate { entered.Set(); if (!release.WaitOne(3000)) throw new TimeoutException("Synthetic release timeout"); };
            if (slowNeutral) first.NeutralHook = block; else first.RemoveHook = block;
            second.NeutralHook = delegate { otherNeutral.Set(); };
            Exception failure = null;
            var stop = new Thread(delegate() { try { if (dispose) runtime.Dispose(); else runtime.Disable("all off"); } catch (Exception error) { failure = error; } });
            stop.Start();
            try
            {
                Check(entered.WaitOne(1000), "Controlled neutral/removal phase starts.");
                Check(otherNeutral.WaitOne(1000), "A slow first output cannot delay another output's neutralization.");
                Check(!first.Active && !second.Active, "All mapping workers release their outputs before slow neutral/removal work.");
                if (slowNeutral) Check(first.RemoveCalls == 0 && second.RemoveCalls == 0, "No output is removed before every neutral attempt has completed.");
            }
            finally { release.Set(); Check(stop.Join(1500), "Group stop finishes after controlled cleanup is released."); }
            Check(failure == null && first.NeutralCalls == 1 && second.NeutralCalls == 1 && first.RemoveCalls == 1 && second.RemoveCalls == 1,
                "Disable and Dispose each neutralize and remove every owned output once.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.EnableController("main"); runtime.EnableController("player2");
            first.NeutralHook = delegate { throw new InvalidOperationException("neutral failed"); };
            Reject(delegate { runtime.Disable("all off"); }, "A per-slot neutral failure stays visible after the group stops.");
            Check(first.Disposed && first.RemoveCalls == 1 && second.NeutralCalls == 1 && second.RemoveCalls == 1 && !second.Enabled,
                "A failed neutral attempt still removes its output and cannot skip another player's cleanup.");
            Check(runtime.Frame.Errors.Count == 1, "Failed group release retires and explains only its failing route.");
        }
    }
    public static string Run()
    {
        checks = 0;
        ControllerOperationsById();
        PreviewDemandFollowsVisibilityAndSelection();
        AsyncReservationsAndRetiredCleanup();
        GroupReleasePhases();
        using (var fixture = new Fixture(1))
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration()); runtime.Enable();
            runtime.SelectedControllerId = "player2";
            Reject(runtime.Enable, "Single-device backend must reject a second controller before starting its output.");
            Check(fixture.Slot("main").Enabled && fixture.Slot("player2").EnableCalls == 0, "Rejected second connection preserves first and allocates nothing.");
            runtime.SelectedControllerId = "main"; runtime.DisableSelected("swap");
            runtime.SelectedControllerId = "player2"; runtime.Enable();
            Check(runtime.Enabled && runtime.ActiveControllerIds.Length == 1 && runtime.ActiveControllerIds[0] == "player2", "Different profile controller can take over after normal disconnect.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime;
            Profile profile = TwoPlayers();
            profile = MappingAssignments.Add(profile, new[] { 1 }, OutputTarget.A, "player2");
            profile = MappingAssignments.Add(profile, new[] { 3 }, OutputTarget.B, "main");
            foreach (Binding binding in profile.Bindings) if (binding.KeyIndex == 3) binding.Enabled = false;
            runtime.Configure(profile, Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            Check(runtime.ActiveKeyIndices.Length == 0, "Disconnected routes cannot request Windows key suppression");
            runtime.Enable();
            Check(runtime.ActiveKeyIndices.Length == 1 && runtime.ActiveKeyIndices[0] == 1, "Active keys include only enabled bindings in connected slots");
            runtime.SelectedControllerId = "player2"; runtime.Enable();
            int[] keys = runtime.ActiveKeyIndices;
            Check(keys.Length == 2 && keys[0] == 1 && keys[1] == 2, "Active-key metadata is a sorted union across all connected slots");
            keys[0] = 254;
            Check(runtime.ActiveKeyIndices[0] == 1, "Suppression metadata is detached from caller mutation");
            int firstEnables = first.EnableCalls, secondEnables = second.EnableCalls;
            runtime.KeyboardMode = true;
            Check(runtime.KeyboardMode && first.KeyboardMode && second.KeyboardMode, "Keyboard-mode transition reaches every slot regardless of selection");
            Check(first.Enabled && second.Enabled && first.EnableCalls == firstEnables && second.EnableCalls == secondEnables, "Pause preserves connected device instances");
            Check(runtime.ActiveKeyIndices.Length == 2, "Connected-key metadata remains available to prepare suppression before resume");
            runtime.KeyboardMode = false;
            Check(!first.KeyboardMode && !second.KeyboardMode && first.Enabled && second.Enabled, "Resume reaches every still-connected slot without recreating devices");
            runtime.KeyboardMode = true;
            profile = ControllerRouting.Add(profile, "third", "Player 3", ControllerKind.DualSense);
            runtime.Configure(profile, Calibration());
            Check(runtime.KeyboardMode && fixture.Slot("third").KeyboardMode && first.KeyboardMode && !runtime.AnyEnabled, "Reconfiguration and new endpoints inherit mode while preserving normal disarm behavior");
            runtime.SelectedControllerId = "main"; runtime.Enable(); runtime.SelectedControllerId = "player2"; runtime.Enable();
            second.FailMode = true;
            Reject(delegate { runtime.KeyboardMode = false; }, "One failed mode transition is reported after fanout");
            Check(!first.KeyboardMode && first.Enabled && second.Disposed && runtime.ActiveKeyIndices.Length == 1, "Mode failure retires its slot while other slots complete and failed keys are excluded");
            runtime.Disable("F8"); Check(!runtime.AnyEnabled && runtime.ActiveKeyIndices.Length == 0, "F8 removes outputs and clears active-key metadata in either mode");
            runtime.KeyboardMode = true; runtime.Configure(profile, Calibration());
            Check(fixture.Slot("player2").KeyboardMode && !fixture.Slot("player2").Disposed, "Replacement for a failed endpoint inherits current mode");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); runtime.SelectedControllerId = "player2"; runtime.Enable(); first.FailMode = true;
            Reject(delegate { runtime.KeyboardMode = true; }, "Neutral failure during keyboard-mode fanout remains visible");
            Check(first.Disposed && second.KeyboardMode && second.Enabled && runtime.KeyboardMode, "A failed first slot cannot prevent another connected slot from becoming neutral");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime;
            Check(runtime.SelectedControllerId == "main" && !runtime.AnyEnabled, "Initial legacy slot must be disconnected.");
            object priorInput = new object(); runtime.SetInputSource(priorInput);
            runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            Check(Object.ReferenceEquals(first.Source, priorInput) && Object.ReferenceEquals(second.Source, priorInput), "Newly configured slots must inherit the existing input source.");
            Check(first.Profile.Bindings.Count == 1 && first.Profile.Bindings[0].KeyIndex == 1, "Player 1 must receive only its own mapping.");
            Check(second.Profile.Bindings.Count == 1 && second.Profile.Bindings[0].KeyIndex == 2, "Player 2 must receive only its own mapping.");
            Check(first.Profile.Controllers[0].Id == "main" && second.Profile.Controllers[0].Id == "player2", "Each worker must keep its own route identity.");
            object input = new object(); runtime.SetInputSource(input);
            Check(Object.ReferenceEquals(first.Source, input) && Object.ReferenceEquals(second.Source, input), "Input source must reach both workers.");
            first.CurrentFrame.LeftX = 0.25; second.CurrentFrame.RightTrigger = 0.9;
            first.CurrentPreview.LeftX = 0.5; first.CurrentPreview.AvailableBindingCount = 1;
            second.CurrentPreview.RightTrigger = 0.75; second.CurrentPreview.AvailableBindingCount = 1;
            second.CurrentPreview.Errors.Add("another key is unknown");
            runtime.Enable();
            int firstConfigure = first.ConfigureCalls, secondConfigure = second.ConfigureCalls;
            runtime.SelectedControllerId = "player2";
            Check(first.Enabled && !runtime.Enabled && runtime.AnyEnabled, "View change must keep player 1 connected.");
            Check(runtime.Frame.RightTrigger == 0.9 && runtime.Frame.LeftX == 0, "Selected frame must belong to player 2.");
            Check(runtime.Preview.RightTrigger == 0.75 && runtime.Preview.HasValidInput && runtime.Preview.Errors.Count == 1, "Disconnected player 2 retains an independent partial preview.");
            runtime.Enable();
            Check(first.Enabled && second.Enabled, "Two controllers must run at the same time.");
            runtime.SelectedControllerId = "main";
            Check(runtime.Frame.LeftX == 0.25 && runtime.Frame.RightTrigger == 0, "Player 1 frame must remain independent.");
            Check(runtime.Preview.LeftX == 0.5 && runtime.Preview.RightTrigger == 0, "Selecting player 1 immediately selects its separate preview.");
            Check(first.ConfigureCalls == firstConfigure && second.ConfigureCalls == secondConfigure, "View changes must never configure either session.");
            runtime.SelectedControllerId = "player2"; runtime.DisableSelected("unplug player 2");
            Check(first.Enabled && !second.Enabled && runtime.AnyEnabled, "Unplug must affect only selected player.");
            Check(runtime.Preview.RightTrigger == 0.75 && runtime.Preview.HasValidInput, "Unplugging output does not blank the selected input preview.");
            second.FailEnable = true;
            Reject(runtime.Enable, "Partially allocated output failure must propagate.");
            Check(!second.Enabled && first.Enabled, "Failed connect must clean selected output without stopping another player.");
            second.FailEnable = false; runtime.Enable(); runtime.Disable("F8");
            Check(!first.Enabled && !second.Enabled && !runtime.AnyEnabled, "F8 must stop all players.");
            Check(first.LastReason == "F8" && second.LastReason == "F8", "Global stop reason must reach every worker.");
            runtime.SelectedControllerId = "main"; runtime.Enable(); runtime.SelectedControllerId = "player2"; runtime.Enable();
            runtime.Configure(ControllerRouting.Rename(TwoPlayers(), "player2", "Guest"), Calibration());
            Check(!runtime.AnyEnabled && runtime.SelectedControllerId == "player2", "Profile edits disarm every slot and preserve valid selection.");
            Check(fixture.Slot("player2").Profile.Controllers[0].Name == "Guest", "Renamed profile must reach the correct route.");
            runtime.Enable(); runtime.SetInputSource(null);
            Check(!runtime.AnyEnabled && first.Source == null && second.Source == null, "Input disconnect must disarm and reach every worker.");
            runtime.Configure(ControllerRouting.Remove(TwoPlayers(), "player2"), Calibration());
            Check(second.Disposed && runtime.SelectedControllerId == "main", "Removing selected route must dispose it and select surviving route.");
            Reject(delegate { runtime.SelectedControllerId = "missing"; }, "Unknown selection must be rejected.");
            Check(runtime.SelectedControllerId == "main", "Rejected selection must preserve previous selection.");
            runtime.Dispose(); runtime.Dispose();
            Check(first.Disposed && !runtime.AnyEnabled && !runtime.Enabled, "Dispose must remove all endpoints and be repeatable.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; Profile profile = new Profile();
            for (int i = 2; i <= 5; i++) profile = ControllerRouting.Add(profile, "p" + i, "Player " + i, ControllerKind.Xbox360);
            profile = ControllerRouting.Add(profile, "ps", "PS player", ControllerKind.DualSense);
            runtime.Configure(profile, Calibration());
            foreach (string id in new[] { "main", "p2", "p3", "p4" }) { runtime.SelectedControllerId = id; runtime.Enable(); }
            runtime.SelectedControllerId = "p5";
            Reject(runtime.Enable, "Fifth owned Xbox controller must be rejected before backend connect.");
            Check(fixture.Slot("p5").EnableCalls == 0, "Capacity rejection must not call backend.");
            Check(fixture.Slot("main").Enabled && fixture.Slot("p4").Enabled, "Capacity failure must preserve existing players.");
            runtime.SelectedControllerId = "ps"; runtime.Enable();
            Check(runtime.Enabled && fixture.Slot("ps").Profile.Controller == ControllerKind.DualSense, "PS route must retain its own device kind.");
            runtime.SelectedControllerId = "main"; runtime.DisableSelected("free slot");
            runtime.SelectedControllerId = "p5"; runtime.Enable();
            Check(runtime.Enabled && fixture.Slot("ps").Enabled, "Freed Xbox capacity must be reusable without stopping PS output.");
            Fake failing = fixture.Slot("p3"); failing.FailDisable = true;
            Reject(delegate { runtime.Disable("emergency"); }, "Cleanup errors must remain visible.");
            Check(failing.Disposed && !runtime.AnyEnabled, "One failing cleanup must not prevent all other slots from stopping.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); runtime.SelectedControllerId = "player2"; runtime.Enable(); second.FailDisable = true;
            Reject(delegate { runtime.DisableSelected("unplug failure"); }, "Selected cleanup failure must remain visible.");
            Check(second.Disposed && first.Enabled, "Failed selected cleanup must dispose that output and preserve other players.");
            Check(runtime.Frame.Errors.Count == 1 && runtime.Status.Contains("Einstellungen erneut anwenden"), "Disposed slot must present an explicit failed state rather than stale output.");
            int beforeRetry = second.EnableCalls;
            Reject(runtime.Enable, "Disposed slot must reject reconnect with its explicit failure.");
            Check(second.EnableCalls == beforeRetry, "Reconnect must not call a disposed endpoint.");
            object newSource = new object(); runtime.SetInputSource(newSource);
            runtime.Configure(TwoPlayers(), Calibration());
            Check(fixture.Created.Count == 3 && !fixture.Slot("player2").Disposed && Object.ReferenceEquals(fixture.Slot("player2").Source, newSource), "Next explicit configuration must rebuild failed slot with current input.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); second.FailConfigure = true;
            Reject(delegate { runtime.Configure(TwoPlayers(), Calibration()); }, "Partial configuration failure must propagate.");
            Check(first.Disposed && second.Disposed && !runtime.AnyEnabled, "Partial configuration failure must close every old endpoint.");
            Check(runtime.Frame.Errors.Count == 1, "Failed configuration must be reported in preview state.");
            runtime.Configure(TwoPlayers(), Calibration());
            Check(fixture.Created.Count == 4 && !runtime.AnyEnabled, "Configuration must recover with fresh disconnected endpoints.");
            second = fixture.Slot("player2"); first = fixture.Slot("main"); second.FailInput = true;
            Reject(delegate { runtime.SetInputSource(new object()); }, "Input fanout failure must propagate.");
            Check(first.Disposed && second.Disposed && !runtime.AnyEnabled, "Input failure must close all endpoints, not leave a stale player.");
        }
        int factories = 0;
        var duplicate = new Fake();
        using (var runtime = new MultiControllerSession(delegate { factories++; return duplicate; }))
        {
            Reject(delegate { runtime.Configure(TwoPlayers(), Calibration()); }, "Aliased output session across two slots must be rejected.");
            Check(factories == 2 && duplicate.Disposed && !runtime.AnyEnabled, "Aliased factory must fail closed before sharing any output.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); first.FailDisable = true;
            Profile renamed = ControllerRouting.Rename(TwoPlayers(), "main", "Changed route");
            Reject(delegate { runtime.Configure(renamed, Calibration()); }, "Pre-configuration disable failure must propagate.");
            Check(first.Disposed && second.Disposed && !runtime.AnyEnabled, "Failed pre-configuration disable must discard every old route.");
            Check(runtime.Frame.Errors.Count == 1, "Pre-configuration failure must be reported instead of showing old frame.");
            runtime.Configure(renamed, Calibration());
            Check(fixture.Created.Count == 4 && fixture.Slot("main").Profile.Controllers[0].Name == "Changed route", "A later configure must build the new profile, not reuse stale routes.");
        }
        using (var fixture = new Fixture())
        {
            var runtime = fixture.Runtime; runtime.Configure(TwoPlayers(), Calibration());
            object oldSource = new object(); runtime.SetInputSource(oldSource);
            Fake first = fixture.Slot("main"), second = fixture.Slot("player2");
            runtime.Enable(); first.FailDisable = true;
            Reject(delegate { runtime.SetInputSource(new object()); }, "Pre-input-change disable failure must propagate.");
            Check(first.Disposed && second.Disposed && !runtime.AnyEnabled, "Failed pre-input disable must discard every old route.");
            Check(runtime.Frame.Errors.Count == 1, "Pre-input failure must be reported instead of showing old frame.");
            runtime.Configure(TwoPlayers(), Calibration());
            Check(fixture.Slot("main").Source == null && fixture.Slot("player2").Source == null, "Failed input change must clear old input before later recovery.");
        }
        return checks + " multi-controller orchestration checks passed; only synthetic endpoints and input tokens used.";
    }
}
