using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static class ShutdownHarness
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Private).SetValue(owner, value); }
        static FormClosingEventArgs Closing(MainForm form, CloseReason reason)
        {
            var args = new FormClosingEventArgs(reason, false);
            try { typeof(Form).GetMethod("OnFormClosing", Private).Invoke(form, new object[] { args }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
            return args;
        }
        sealed class FakeController : IControllerSession, IAsyncControllerSession
        {
            internal bool Active;
            internal int NeutralCalls, DisposeCalls;
            internal Action OnNeutral;
            internal TaskCompletionSource<object> PendingEnable, PendingCleanup;
            internal int CancelCalls;
            public bool Connecting { get; private set; }
            public Task EnableAsync(CancellationToken token)
            {
                if (PendingEnable == null) { Enable(); return Task.FromResult(0); }
                Connecting = true; return PendingEnable.Task;
            }
            public Task CancelPendingConnection()
            {
                Interlocked.Increment(ref CancelCalls); Connecting = false;
                if (PendingEnable != null) PendingEnable.TrySetCanceled();
                return PendingCleanup == null ? (Task)Task.FromResult(0) : PendingCleanup.Task;
            }
            public bool Enabled { get { return Active; } }
            public ControllerFrame Frame { get { return new ControllerFrame(); } }
            public PreviewSnapshot Preview { get { return new PreviewSnapshot(); } }
            public string Status { get { return "Synthetic shutdown endpoint"; } }
            public void Configure(Profile profile, IDictionary<int, Calibration> calibration) { Active = false; }
            public void SetInputSource(object source) { Active = false; }
            public void SetKeyboardMode(bool value) { }
            public void Enable() { Active = true; }
            public void Disable(string reason) { Active = false; }
            public ControllerRelease PrepareDisable(string reason)
            {
                if (!Active) return null;
                Active = false;
                return new ControllerRelease(delegate { Interlocked.Increment(ref NeutralCalls); if (OnNeutral != null) OnNeutral(); }, delegate { });
            }
            public void Dispose() { Active = false; Interlocked.Increment(ref DisposeCalls); }
        }
        sealed class Fixture : IDisposable
        {
            internal readonly MainForm Form;
            internal readonly FakeController Controller;
            internal readonly string Root;
            internal Fixture(string root)
            {
                Root = root;
                Form = new MainForm(root, true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
                // Exercise bounded waits without consuming the production 25s
                // RGB/helper allowance for every intentionally blocked fixture.
                Set(Form, "systemShutdownWaitMilliseconds", 3000);
                Form.StartInBackground();
                Controller = new FakeController();
                var runtime = new MultiControllerSession(delegate { return Controller; });
                Field<MultiControllerSession>(Form, "runtime").Dispose(); Set(Form, "runtime", runtime);
                runtime.Enable();
            }
            public void Dispose() { Form.Dispose(); }
        }

        static void NormalClose(string directory)
        {
            foreach (CloseReason reason in new[] { CloseReason.UserClosing, CloseReason.ApplicationExitCall })
                using (var fixture = new Fixture(Path.Combine(directory, reason.ToString())))
                {
                    FormClosingEventArgs result = Closing(fixture.Form, reason);
                    Check(!result.Cancel && Field<bool>(fixture.Form, "closing"), "Ordinary X/tray/application exit is accepted after cleanup.");
                    Check(fixture.Controller.NeutralCalls == 1 && fixture.Controller.DisposeCalls == 1, "Normal exit neutralizes and removes its output once.");
                    Check(Field<bool>(fixture.Form, "closeProfileSaved"), "Normal exit retains a successful profile-save result.");
                    Check(Field<ShutdownWork>(fixture.Form, "systemShutdownWork") == null, "Ordinary exit keeps the full normal RGB lifecycle rather than using the OS deadline.");
                }
            foreach (string pending in new[] { "rgbClosePending", "deviceDetachInProgress" })
                using (var fixture = new Fixture(Path.Combine(directory, pending)))
                {
                    Set(fixture.Form, pending, true);
                    Check(Closing(fixture.Form, CloseReason.UserClosing).Cancel, "Normal exit still waits for an existing " + pending + " operation.");
                    Check(fixture.Controller.DisposeCalls == 0, "A pending normal close does not dispose resources underneath its worker.");
                    Set(fixture.Form, pending, false);
                }
        }

        static void SystemClose(string directory, string pending, bool saveFailure)
        {
            using (var fixture = new Fixture(directory))
            {
                if (pending != null) Set(fixture.Form, pending, true);
                if (saveFailure) Set(fixture.Form, "profilePath", Path.Combine(directory, "absent-directory", "profile.json"));
                var watch = Stopwatch.StartNew();
                FormClosingEventArgs result = Closing(fixture.Form, CloseReason.WindowsShutDown);
                Check(!result.Cancel, "Windows shutdown is never cancelled by pending work or a profile-save error.");
                Check(watch.ElapsedMilliseconds < 4500, "The system-close handler returns within its overall bounded budget.");
                var work = Field<ShutdownWork>(fixture.Form, "systemShutdownWork");
                Check(work != null && work.Wait(1000), "Healthy independent shutdown lanes complete.");
                Check(fixture.Controller.NeutralCalls == 1 && fixture.Controller.DisposeCalls == 1, "System shutdown neutralizes and removes the output exactly once.");
                Check(!Field<bool>(fixture.Form, "closeAfterDeviceDetach"), "System shutdown does not depend on a deferred detach callback.");
                Check(!Field<System.Windows.Forms.Timer>(fixture.Form, "uiTimer").Enabled && !Field<bool>(fixture.Form, "discoveryEnabled"), "Shutdown stops maintenance and discovery from starting new work.");
                Check(Field<object>(fixture.Form, "reader") == null && Field<bool>(fixture.Form, "closing"), "System shutdown seals the UI lifecycle and transfers reader ownership.");
                if (saveFailure)
                    Check(File.ReadAllText(Path.Combine(directory, "events.log")).Contains("Windows shutdown profile save"), "A failed OS-shutdown save is recorded without a modal prompt.");
                watch.Restart(); fixture.Form.Dispose();
                Check(watch.ElapsedMilliseconds < 1000 && fixture.Controller.DisposeCalls == 1, "Form.Dispose neither retries nor waits again for owned shutdown resources.");
                Check(!Closing(fixture.Form, CloseReason.WindowsShutDown).Cancel && Object.ReferenceEquals(work, Field<ShutdownWork>(fixture.Form, "systemShutdownWork")), "Repeated system-close notification does not start new cleanup lanes.");
            }
        }

        static void NormalCloseConfirmation(string directory)
        {
            foreach (bool failNeutral in new[] { false, true })
                using (var fixture = new Fixture(Path.Combine(directory, failNeutral ? "failed" : "healthy")))
                {
                    var files = new Dictionary<string, string>();
                    Func<string, string> read = delegate(string path) { string value; return files.TryGetValue(path, out value) ? value : null; };
                    Action<string, string> write = delegate(string path, string value) { files[path] = value; };
                    var journal = new ControllerReconnectStore(read, write); journal.BeginSession(); journal.SetEnabled(true);
                    Set(fixture.Form, "controllerReconnectStore", journal); Set(fixture.Form, "controllerStartup", journal.Preferences);
                    // Initialization already finished in passive preview mode.
                    // Enable only the close-time persistence branch, with fake
                    // output and in-memory journal; no startup is invoked again.
                    Set(fixture.Form, "previewMode", false);
                    if (failNeutral) fixture.Controller.OnNeutral = delegate { throw new IOException("Synthetic neutral failure"); };
                    Check(!Closing(fixture.Form, CloseReason.UserClosing).Cancel, "A failed neutral attempt does not trap an accepted ordinary close.");
                    Check(Field<bool>(fixture.Form, "closeProfileSaved"), "Controller cleanup failure does not prevent saving the profile.");
                    Check(Field<bool>(fixture.Form, "resourceCleanupFailed") == failNeutral, "A failed neutral phase remains recorded after subsequent disposal.");
                    var nextSession = new ControllerReconnectStore(read, write); nextSession.BeginSession();
                    Check((nextSession.StartupSnapshot != null) == !failNeutral, "Only successful ordinary cleanup can confirm a reconnect snapshot for the next process.");
                    Check(nextSession.Preferences.Enabled, "Refusing a clean-exit confirmation preserves the user's startup preference.");
                }
        }

        static void BlockedCleanup(string directory)
        {
            using (var release = new ManualResetEvent(false))
            using (var entered = new ManualResetEvent(false))
            using (var fixture = new Fixture(directory))
            {
                fixture.Controller.OnNeutral = delegate { entered.Set(); release.WaitOne(); };
                ShutdownWork work = null;
                try
                {
                    var watch = Stopwatch.StartNew();
                    Check(!Closing(fixture.Form, CloseReason.WindowsShutDown).Cancel, "A blocked output cannot veto Windows shutdown.");
                    Check(entered.WaitOne(0), "The test actually blocks inside output neutralization.");
                    Check(watch.ElapsedMilliseconds >= 2500 && watch.ElapsedMilliseconds < 4500, "The OS handler respects the injected three-second test budget.");
                    Check(Field<bool>(fixture.Form, "systemShutdownTimedOut"), "Incomplete work is reported as timed out, never as confirmed cleanup.");
                    work = Field<ShutdownWork>(fixture.Form, "systemShutdownWork");
                    Check(File.Exists(Field<string>(fixture.Form, "profilePath")), "The independent profile lane still writes while output cleanup is blocked.");
                    watch.Restart(); fixture.Form.Dispose();
                    Check(watch.ElapsedMilliseconds < 1000, "Disposal does not turn a bounded shutdown into another unbounded wait.");
                }
                finally { release.Set(); if (work != null) work.Wait(3000); }
                Check(fixture.Controller.DisposeCalls == 1 && fixture.Controller.NeutralCalls == 1, "After unblocking, the original lane alone finishes output cleanup.");
            }
        }

        static void IndependentLanes()
        {
            using (var release = new ManualResetEvent(false))
            using (var independentDone = new ManualResetEvent(false))
            {
                int completed = 0;
                Action complete = delegate { if (Interlocked.Increment(ref completed) == 2) independentDone.Set(); };
                var work = new ShutdownWork(delegate { release.WaitOne(); }, delegate { complete(); throw new IOException("Synthetic save failure"); }, complete);
                Check(!work.Wait(0) && work.States.All(state => state == ShutdownPhaseState.Pending), "Unstarted cleanup exposes pending phases rather than invented completion.");
                work.Start();
                try { Check(independentDone.WaitOne(2000), "Other cleanup lanes run even when a peer blocks or throws."); Check(!work.Wait(50), "One blocked lane does not report overall success."); }
                finally { release.Set(); work.Wait(1000); }
                Check(work.Errors.Length == 1, "Background failure is retained for diagnostics without crossing onto the UI thread.");
                Check(work.States[0] == ShutdownPhaseState.Completed && work.States[1] == ShutdownPhaseState.Failed && work.States[2] == ShutdownPhaseState.Completed,
                    "Progress distinguishes actual success from a failed completed lane.");
            }
        }

        static void ExistingDetachHoldsCoordinator(string directory)
        {
            using (var release = new ManualResetEvent(false))
            using (var entered = new ManualResetEvent(false))
            using (var fixture = new Fixture(directory))
            {
                fixture.Controller.OnNeutral = delegate { entered.Set(); release.WaitOne(); };
                var runtime = Field<MultiControllerSession>(fixture.Form, "runtime");
                Exception failure = null;
                var detach = new Thread(delegate() { try { runtime.Disable("Synthetic detach"); } catch (Exception error) { failure = error; } });
                detach.Start(); ShutdownWork work = null;
                try
                {
                    Check(entered.WaitOne(2000), "An existing detach really holds the coordinator while neutralizing.");
                    Set(fixture.Form, "deviceDetachInProgress", true);
                    var elapsed = Stopwatch.StartNew();
                    Check(!Closing(fixture.Form, CloseReason.WindowsShutDown).Cancel && elapsed.ElapsedMilliseconds < 4500,
                        "Even acquiring the cancellation/coordinator lock cannot delay the OS handler beyond its budget.");
                    work = Field<ShutdownWork>(fixture.Form, "systemShutdownWork");
                    fixture.Form.Dispose();
                }
                finally { release.Set(); detach.Join(3000); if (work != null) work.Wait(3000); }
                Check(failure == null && fixture.Controller.DisposeCalls == 1, "Existing detach and shutdown agree on one final endpoint owner.");
            }
        }

        static void CancelledWindowsLogoff(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                object[] query = new object[] { Message.Create(fixture.Form.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero) };
                var elapsed = Stopwatch.StartNew();
                typeof(MainForm).GetMethod("WndProc", Private).Invoke(fixture.Form, query);
                Check(((Message)query[0]).Result == new IntPtr(1) && elapsed.ElapsedMilliseconds < 1000, "A synthetic session query is promptly accepted before any cleanup.");
                Check(Field<ShutdownWork>(fixture.Form, "systemShutdownWork") == null && fixture.Controller.Active && fixture.Controller.DisposeCalls == 0,
                    "The query leaves controller resources and the editor usable until Windows commits the session end.");
                Message message = Message.Create(fixture.Form.Handle, 0x0016, IntPtr.Zero, IntPtr.Zero);
                typeof(MainForm).GetMethod("WndProc", Private).Invoke(fixture.Form, new object[] { message });
                Application.DoEvents();
                Check(!fixture.Form.IsDisposed && !Field<bool>(fixture.Form, "closing") && fixture.Controller.Active,
                    "Cancelling logoff after the query preserves the live application.");
                message = Message.Create(fixture.Form.Handle, 0x0016, new IntPtr(1), IntPtr.Zero);
                typeof(MainForm).GetMethod("WndProc", Private).Invoke(fixture.Form, new object[] { message });
                Check(Field<ShutdownWork>(fixture.Form, "systemShutdownWork") != null && fixture.Controller.DisposeCalls == 1,
                    "Only a committed synthetic ENDSESSION starts and waits for actual cleanup.");
            }
        }

        static void PendingConnectionCleanup(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var runtime = Field<MultiControllerSession>(fixture.Form, "runtime");
                runtime.Disable("Prepare synthetic connection");
                fixture.Controller.PendingEnable = new TaskCompletionSource<object>();
                fixture.Controller.PendingCleanup = new TaskCompletionSource<object>();
                Task attempt = runtime.EnableControllerAsync(runtime.SelectedControllerId, CancellationToken.None);
                Check(fixture.Controller.Connecting && !attempt.IsCompleted, "The test has a genuinely pending asynchronous candidate.");
                int previousCancels = fixture.Controller.CancelCalls;
                ShutdownWork work = null;
                try
                {
                    var watch = Stopwatch.StartNew();
                    Check(!Closing(fixture.Form, CloseReason.WindowsShutDown).Cancel && watch.ElapsedMilliseconds < 4500,
                        "A pending connection's slow cleanup cannot veto or indefinitely delay Windows shutdown.");
                    work = Field<ShutdownWork>(fixture.Form, "systemShutdownWork");
                    Check(fixture.Controller.CancelCalls > previousCancels && !fixture.Controller.Connecting && attempt.IsCanceled,
                        "Shutdown cancels pending publication and observes its cancelled enable task.");
                    Check(!fixture.Controller.Active && !work.Wait(0), "Cleanup remains tracked without exposing the cancelled candidate as connected.");
                    fixture.Form.Dispose();
                }
                finally { fixture.Controller.PendingCleanup.TrySetResult(null); if (work != null) work.Wait(3000); }
                Check(fixture.Controller.DisposeCalls == 1, "The tracked candidate cleanup and session disposal do not create a second endpoint owner.");
            }
        }

        static void NormalCloseWaitsForCandidate(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var runtime = Field<MultiControllerSession>(fixture.Form, "runtime");
                runtime.Disable("Prepare synthetic connection");
                fixture.Controller.PendingEnable = new TaskCompletionSource<object>();
                fixture.Controller.PendingCleanup = new TaskCompletionSource<object>();
                Task attempt = runtime.EnableControllerAsync(runtime.SelectedControllerId, CancellationToken.None);
                try
                {
                    var elapsed = Stopwatch.StartNew(); fixture.Form.Close();
                    Check(elapsed.ElapsedMilliseconds < 1000 && !fixture.Form.IsDisposed && Field<bool>(fixture.Form, "rgbClosePending"),
                        "Normal Close remains responsive and waits asynchronously for a candidate even with no RGB worker.");
                    var progress = Field<CloseProgressForm>(fixture.Form, "closeProgress");
                    Check(progress != null && progress.Visible && progress.Left < -10000 && Field<SleekProgressBar>(progress, "progress").Value < 4,
                        "Pending normal cleanup shows an offscreen progress window and cannot claim all phases are complete.");
                    Check(attempt.IsCanceled && fixture.Controller.DisposeCalls == 0,
                        "Normal close cancels the attempt before disposing the session that owns its cleanup.");
                    fixture.Form.Close();
                    Check(!fixture.Form.IsDisposed, "Repeated X cannot bypass a pending candidate's cleanup.");
                    fixture.Controller.PendingCleanup.TrySetResult(null);
                    elapsed.Restart();
                    while (!fixture.Form.IsDisposed && elapsed.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(2); }
                    Check(fixture.Form.IsDisposed && fixture.Controller.DisposeCalls == 1,
                        "The accepted normal exit closes only after the original candidate cleanup is drained.");
                }
                finally { fixture.Controller.PendingCleanup.TrySetResult(null); }
            }
        }

        static void SuspendCancelsStartup(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                Set(fixture.Form, "startupReconnectPending", true);
                Set(fixture.Form, "startupReconnectQueue", new Queue<string>(new[] { "main" }));
                typeof(MainForm).GetMethod("OnPower", Private).Invoke(fixture.Form,
                    new object[] { null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend) });
                Check(!Field<bool>(fixture.Form, "startupReconnectPending") && Field<object>(fixture.Form, "startupReconnectQueue") == null,
                    "Suspend cancels both initial startup waiting and queued automatic connections.");
                Check(!fixture.Controller.Active && fixture.Controller.NeutralCalls == 1, "Suspend also neutralizes the connected controller.");
            }
        }

        static void DetachedReaderCleanupTracked(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var detached = new TaskCompletionSource<object>();
                Set(fixture.Form, "deviceDetachCleanup", detached.Task);
                Set(fixture.Form, "systemShutdownWaitMilliseconds", 150);
                ShutdownWork work = null;
                try
                {
                    Closing(fixture.Form, CloseReason.WindowsShutDown);
                    work = Field<ShutdownWork>(fixture.Form, "systemShutdownWork");
                    Check(!work.Wait(0) && Field<bool>(fixture.Form, "systemShutdownTimedOut"), "A detached reader still owns cleanup after reader becomes null; shutdown waits its completion task.");
                    Check(Field<ShutdownPhaseState>(fixture.Form, "systemReaderPhase") == ShutdownPhaseState.Running,
                        "Reader/helper progress cannot finish while detached cleanup is pending.");
                }
                finally { detached.TrySetResult(null); if (work != null) work.Wait(2000); }
                Check(Field<ShutdownPhaseState>(fixture.Form, "systemReaderPhase") == ShutdownPhaseState.Completed,
                    "The resource lane finishes when detached reader cleanup actually returns.");
            }
        }

        sealed class DrainingSource : IReportSource
        {
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal volatile bool Finished;
            public byte[] Read(int timeoutMs) { Thread.Sleep(10); return null; }
            public void Dispose() { Entered.Set(); Release.WaitOne(); Finished = true; }
        }
        sealed class LearnedCleanupSource : ILearnedInputDeviceSource
        {
            internal int Disposals;
            internal bool Fail;
            public string DeviceId { get { return "synthetic"; } }
            public string DisplayName { get { return "Synthetic cleanup"; } }
            public string Status { get { return "Synthetic"; } }
            public bool IsReading { get { return true; } }
            public InputControlDescriptor[] Controls { get { return new InputControlDescriptor[0]; } }
            public event Action<InputControlSample> Sample { add { } remove { } }
            public bool TryGetValue(string id, out double value) { value = 0; return false; }
            public void Dispose() { Disposals++; if (Fail) throw new IOException("Synthetic learned input cleanup failure"); }
        }
        static ReaderSession AttachDrainingReader(Fixture fixture, DrainingSource source)
        {
            var device = new CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "Synthetic TK75", version = 0x0403,
                usagePage = 65535, usage = 1, inputReportLength = 32 };
            device.reportCapabilities.Add(new Dictionary<string, object> { { "reportType", "input" }, { "kind", "value" },
                { "reportId", 5 }, { "usagePage", 65535 }, { "bitSize", 8 }, { "reportCount", 31 } });
            var reader = new ReaderSession(device, new RongYuanTravel32(), delegate { return source; });
            reader.Start(); var elapsed = Stopwatch.StartNew();
            while (!reader.IsReading && elapsed.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            Check(reader.IsReading, "The real reader owns an open synthetic source before cleanup.");
            Set(fixture.Form, "reader", reader); return reader;
        }
        static void SessionEndWaitsForActualReaderExit(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var source = new DrainingSource(); var reader = AttachDrainingReader(fixture, source);
                var release = new Thread(delegate() { source.Entered.WaitOne(2000); Thread.Sleep(650); source.Release.Set(); });
                release.Start();
                try
                {
                    object[] query = { Message.Create(fixture.Form.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero) };
                    typeof(MainForm).GetMethod("WndProc", Private).Invoke(fixture.Form, query);
                    Check(reader.IsReading && !source.Entered.WaitOne(0), "A session query does not start teardown before Windows commits.");
                    var elapsed = Stopwatch.StartNew();
                    object[] end = { Message.Create(fixture.Form.Handle, 0x0016, new IntPtr(1), IntPtr.Zero) };
                    typeof(MainForm).GetMethod("WndProc", Private).Invoke(fixture.Form, end);
                    Check(source.Finished && elapsed.ElapsedMilliseconds >= 600,
                        "WM_ENDSESSION waits past Stop's 250ms for the source's actual restore/OFF/exit cleanup.");
                    Check(!Field<bool>(fixture.Form, "systemShutdownTimedOut") && Field<ShutdownPhaseState>(fixture.Form, "systemReaderPhase") == ShutdownPhaseState.Completed,
                        "Only the completed reader worker permits successful system resource progress.");
                }
                finally { source.Release.Set(); release.Join(2000); reader.DisposeAndWait(2000); }
            }
        }
        static void NormalCloseWaitsForActualReaderExit(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var source = new DrainingSource(); var reader = AttachDrainingReader(fixture, source);
                try
                {
                    var elapsed = Stopwatch.StartNew(); fixture.Form.Close();
                    Check(elapsed.ElapsedMilliseconds < 1000, "Ordinary close starts source cleanup asynchronously even without an RGB worker.");
                    elapsed.Restart();
                    while (!source.Entered.WaitOne(0) && elapsed.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(2); }
                    Check(source.Entered.WaitOne(0), "The test reaches real source disposal.");
                    elapsed.Restart();
                    while (elapsed.ElapsedMilliseconds < 400) { Application.DoEvents(); Thread.Sleep(2); }
                    Check(!fixture.Form.IsDisposed && !source.Finished, "The editor remains alive after 250ms while helper cleanup is pending.");
                    source.Release.Set(); elapsed.Restart();
                    while (!fixture.Form.IsDisposed && elapsed.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(2); }
                    Check(fixture.Form.IsDisposed && source.Finished, "The ordinary exit completes after the original source actually finishes.");
                }
                finally { source.Release.Set(); reader.DisposeAndWait(2000); }
            }
        }
        static void LearnedFailureStillClosesReader(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var source = new DrainingSource(); source.Release.Set();
                var reader = AttachDrainingReader(fixture, source);
                var broken = new LearnedCleanupSource { Fail = true }; var healthy = new LearnedCleanupSource();
                var inputs = Field<Dictionary<string, ILearnedInputDeviceSource>>(fixture.Form, "learnedSources");
                inputs.Add("broken", broken); inputs.Add("healthy", healthy);
                Closing(fixture.Form, CloseReason.WindowsShutDown);
                Check(source.Finished && broken.Disposals == 1 && healthy.Disposals == 1 && inputs.Count == 0,
                    "One failing learned device cannot skip later sources or the primary helper's cleanup.");
                Check(Field<ShutdownPhaseState>(fixture.Form, "systemReaderPhase") == ShutdownPhaseState.Failed,
                    "A drained resource lane still reports the earlier cleanup failure.");
                reader.DisposeAndWait(2000);
            }
        }
        static void FinalDisposalDoesNotRepeatReaderBudget(string directory)
        {
            using (var fixture = new Fixture(directory))
            {
                var source = new DrainingSource(); var reader = AttachDrainingReader(fixture, source);
                try
                {
                    bool timedOut = false;
                    try { reader.DisposeAndWait(300); } catch (TimeoutException) { timedOut = true; }
                    Check(timedOut && source.Entered.WaitOne(0), "The previous cleanup wait really exhausted its budget in source disposal.");
                    Set(fixture.Form, "normalReaderCleanupWaited", true);
                    Set(fixture.Form, "rgbCloseSucceeded", false);
                    var elapsed = Stopwatch.StartNew();
                    typeof(MainForm).GetMethod("DisposeApplicationResources", Private).Invoke(fixture.Form, new object[0]);
                    Check(elapsed.ElapsedMilliseconds < 1000 && !source.Finished,
                        "Final disposal does not spend another full reader budget after asynchronous cleanup timed out.");
                }
                finally { source.Release.Set(); reader.DisposeAndWait(2000); }
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string root = Path.GetFullPath(args[0]);
                int productionBudget = (int)typeof(MainForm).GetField("SystemShutdownTimeoutMilliseconds", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
                Check(productionBudget >= 17000 && productionBudget <= 30000, "The production session-end budget covers lighting restore while remaining bounded below Windows' final shutdown allowance.");
                IndependentLanes(); NormalClose(Path.Combine(root, "normal"));
                SystemClose(Path.Combine(root, "healthy"), null, false);
                SystemClose(Path.Combine(root, "rgb-pending"), "rgbClosePending", true);
                SystemClose(Path.Combine(root, "detach-pending"), "deviceDetachInProgress", true);
                BlockedCleanup(Path.Combine(root, "blocked"));
                ExistingDetachHoldsCoordinator(Path.Combine(root, "existing-detach"));
                CancelledWindowsLogoff(Path.Combine(root, "cancelled-logoff"));
                PendingConnectionCleanup(Path.Combine(root, "pending-connect"));
                NormalCloseWaitsForCandidate(Path.Combine(root, "normal-pending-connect"));
                SuspendCancelsStartup(Path.Combine(root, "suspend"));
                NormalCloseConfirmation(Path.Combine(root, "confirmation"));
                DetachedReaderCleanupTracked(Path.Combine(root, "detached-reader"));
                SessionEndWaitsForActualReaderExit(Path.Combine(root, "reader-session-end"));
                NormalCloseWaitsForActualReaderExit(Path.Combine(root, "reader-normal-close"));
                LearnedFailureStillClosesReader(Path.Combine(root, "learned-cleanup-failure"));
                FinalDisposalDoesNotRepeatReaderBudget(Path.Combine(root, "reader-budget"));
                Console.WriteLine("PASS: " + checks + " shutdown assertions; synthetic endpoints only, no OS shutdown or hardware.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
