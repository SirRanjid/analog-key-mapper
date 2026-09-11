using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Real MainForm worker, ReaderSession and durable journals; only the report
    // source is synthetic. No production helper, HID handle or output is opened.
    public static class RgbLifecycleHarness
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static int checks;
        static void Check(bool condition, string message)
        { Interlocked.Increment(ref checks); if (!condition) throw new InvalidOperationException(message); }
        static T Get<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Fields).GetValue(owner); }
        static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Fields).SetValue(owner, value); }
        static object Call(MainForm form, string name, params object[] args)
        {
            try { return typeof(MainForm).GetMethod(name, Fields).Invoke(form, args); }
            catch (TargetInvocationException error) { throw new InvalidOperationException(name, error.InnerException); }
        }
        static bool Same(Tk75RgbSnapshot a, Tk75RgbSnapshot b)
        { return a != null && b != null && Tk75RgbProtocol.Equal(Tk75RgbProtocol.EncodeSnapshot(a), Tk75RgbProtocol.EncodeSnapshot(b)); }
        static void Await(Func<bool> condition, string message, int timeout = 4000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(2); }
            Check(condition(), message);
        }
        static void Until(Stopwatch watch, int milliseconds)
        { while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(4); } }
        sealed class Barrier : IDisposable
        {
            public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            public void Pass(int timeout, FakeSource source)
            {
                Entered.Set(); int result = WaitHandle.WaitAny(new WaitHandle[] { Release, source.Shutdown }, timeout);
                if (result == WaitHandle.WaitTimeout) throw new TimeoutException("Synthetic operation exceeded its supplied timeout.");
                if (result == 1) throw new OperationCanceledException("Synthetic source stopped.");
            }
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }
        sealed class FakeSource : IReportSource, IIdentifiedReportSource, IRgbCompareExchangeSource
        {
            readonly object gate = new object();
            public readonly ManualResetEvent Shutdown = new ManualResetEvent(false);
            public readonly Tk75RgbSnapshot Original;
            Tk75RgbSnapshot current;
            public readonly List<Tk75RgbSnapshot> Written = new List<Tk75RgbSnapshot>();
            public Action<int, int> BeforeRead;
            public Action<int, Tk75RgbSnapshot, Tk75RgbSnapshot, int> BeforeWrite, AfterWrite;
            public Action<Tk75RgbSnapshot, Tk75RgbSnapshot> RequireJournal;
            public int Reads, Writes;
            public volatile bool Disposed;
            public uint? DeviceModelId { get { return 3591; } }
            public bool RgbReadAvailable { get { return !Disposed; } }
            public bool RgbWriteAvailable { get { return !Disposed; } }
            public Tk75RgbSnapshot Current { get { lock (gate) return current; } }
            public FakeSource()
            {
                byte[] settings = new byte[64], picture = new byte[384];
                settings[0] = 0x87; settings[1] = 1; settings[2] = 2; settings[3] = 3; settings[4] = 7;
                settings[5] = 12; settings[6] = 34; settings[7] = 56; settings[63] = 91;
                for (int i = 0; i < picture.Length; i++) picture[i] = (byte)((i * 23 + 17) & 255);
                Original = current = new Tk75RgbSnapshot(3591, 2, 4, settings, picture);
            }
            public byte[] Read(int timeoutMs) { Shutdown.WaitOne(timeoutMs); return null; }
            public Tk75RgbSnapshot ReadRgbSnapshot(int layer, int timeoutMs)
            { int count = Interlocked.Increment(ref Reads); if (BeforeRead != null) BeforeRead(count, timeoutMs); return Current; }
            public Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeoutMs)
            {
                int count = Interlocked.Increment(ref Writes); RequireJournal(expected, desired);
                if (BeforeWrite != null) BeforeWrite(count, expected, desired, timeoutMs);
                lock (gate)
                {
                    if (!Tk75RgbExchange.Equivalent(current, expected)) throw new InvalidDataException("Synthetic stale expected lighting.");
                    current = desired; Written.Add(desired);
                }
                if (AfterWrite != null) AfterWrite(count, expected, desired, timeoutMs);
                return desired;
            }
            public void Dispose() { Disposed = true; Shutdown.Set(); }
        }
        sealed class Fixture : IDisposable
        {
            public readonly MainForm Form;
            public readonly ReaderSession Reader;
            public readonly FakeSource Source = new FakeSource();
            public readonly string Root, Lighting;
            public object Work { get { return Get<object>(Form, "rgbBackupWork"); } }
            public Fixture(string root)
            {
                Root = root; Lighting = Path.Combine(root, "lighting");
                Form = new MainForm(root, true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
                Form.Show(); Application.DoEvents();
                var device = new CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "TK75 TMR", manufacturer = "Synthetic", serial = "offline", devicePath = "synthetic://rgb-lifecycle", version = 0x0403, usagePage = 65535, usage = 1, inputReportLength = 32 };
                device.reportCapabilities.Add(new Dictionary<string, object> { { "reportType", "input" }, { "kind", "value" }, { "reportId", 5 }, { "usagePage", 65535 }, { "bitSize", 8 }, { "reportCount", 31 } });
                Reader = new ReaderSession(device, new RongYuanTravel32(), delegate { return Source; });
                Reader.Start(); Await(delegate { return Reader.IsReading; }, "Injected reader starts without a hardware transport.");
                Set(Form, "reader", Reader);
                Source.RequireJournal = delegate(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired)
                {
                    string before = Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(expected)), after = Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(desired));
                    bool found = Directory.GetFiles(Lighting, "*.transaction.json").Any(path =>
                    { var journal = ReadJournal(path); return (string)journal["ExpectedBase64"] == before && (string)journal["DesiredBase64"] == after; });
                    Check(found, "A complete matching transaction is durably published before each synthetic device write.");
                };
            }
            public void Start() { Call(Form, "RefreshRgbLighting"); }
            public T State<T>(string field) { object work = Work; lock (Get<object>(work, "Gate")) return Get<T>(work, field); }
            public void Ready()
            {
                Await(delegate { return Work != null && (!State<bool>("Busy") || State<bool>("Stopped")); }, "Lighting initialization completes.");
                Check(State<int>("State") == 1 && !State<bool>("Stopped"), "Lighting is ready: " + State<string>("Error"));
                Check(Same(State<Tk75RgbSnapshot>("Original"), Source.Original), "The real worker retains the exact original snapshot.");
            }
            public Tk75RgbSnapshot Color(int rgb)
            { return (Tk75RgbSnapshot)typeof(MainForm).GetMethod("BuildRgbDesired", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Source.Original, new Dictionary<int, int> { { 14, rgb } } }); }
            public void Queue(Tk75RgbSnapshot desired)
            {
                object work = Work; lock (Get<object>(work, "Gate")) { Set(work, "Desired", desired); Monitor.PulseAll(Get<object>(work, "Gate")); }
            }
            public void Idle()
            { Await(delegate { return !State<bool>("Busy") && State<Tk75RgbSnapshot>("Desired") == null && !State<bool>("RestoreRequested"); }, "The worker consumes all queued work."); }
            public void CloseWithRestore()
            {
                Check((bool)Call(Form, "BeginRgbCloseRestore"), "Close starts the real asynchronous restore path.");
                Await(delegate { return State<bool>("QuitAfterRestore"); }, "The restore request reaches the active worker.");
            }
            public Dictionary<string, byte[]> Files()
            { return Directory.GetFiles(Lighting, "*.json").ToDictionary(path => path, File.ReadAllBytes); }
            public void Preserved(Dictionary<string, byte[]> before)
            { foreach (var file in before) Check(File.Exists(file.Key) && Tk75RgbProtocol.Equal(file.Value, File.ReadAllBytes(file.Key)), "Published backup/journal remains byte-for-byte unchanged: " + Path.GetFileName(file.Key)); }
            public void Restored()
            {
                Check(Same(Source.Current, Source.Original), "Restoration reconstructs every original byte, including reserved data.");
                string backup = Directory.GetFiles(Lighting, "*.backup.json").Single();
                Check((string)ReadJournal(backup)["SnapshotBase64"] == Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(Source.Original)), "The durable original backup remains the initial device state.");
                Check(Directory.GetFiles(Lighting, "*.confirmed.json").Any(path => (string)ReadJournal(path)["ConfirmedBase64"] == Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(Source.Original))), "A durable confirmation records restored original lighting.");
                Check(!State<bool>("Applied") && !State<bool>("RecoveryRequired"), "The worker only clears recovery state after confirmation.");
            }
            public void Dispose()
            {
                object work = Work;
                if (work != null) { lock (Get<object>(work, "Gate")) { Set(work, "Abort", true); Monitor.PulseAll(Get<object>(work, "Gate")); } }
                Source.Shutdown.Set();
                if (work != null) { Thread worker = Get<Thread>(work, "Worker"); if (worker != null) worker.Join(2000); }
                if (!Form.IsDisposed) Form.Dispose(); Reader.Dispose(); Source.Shutdown.Dispose();
            }
        }
        static Dictionary<string, object> ReadJournal(string path)
        { return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
        static void CloseDuringWrite(string root, bool longBudget)
        {
            using (var paint = new Barrier()) using (var restoreRead = new Barrier()) using (var fixture = new Fixture(root))
            {
                fixture.Source.BeforeWrite = delegate(int count, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeout) { if (count == 1) paint.Pass(timeout, fixture.Source); };
                if (longBudget) fixture.Source.BeforeRead = delegate(int count, int timeout) { if (count == 2) restoreRead.Pass(timeout, fixture.Source); };
                fixture.Start(); fixture.Ready(); var preserved = fixture.Files();
                fixture.Queue(fixture.Color(0xAA2211)); Await(delegate { return paint.Entered.WaitOne(0); }, "Color change is held inside the actual worker.");
                foreach (var item in fixture.Files()) preserved[item.Key] = item.Value;
                fixture.CloseWithRestore(); Stopwatch closing = Stopwatch.StartNew();
                if (longBudget) Until(closing, 3300);
                paint.Release.Set();
                if (longBudget)
                {
                    Await(delegate { return restoreRead.Entered.WaitOne(0); }, "Close performs a fresh restore read after the in-flight write.");
                    Until(closing, 6500);
                    Check(!fixture.Form.IsDisposed && !fixture.Source.Disposed, "Close retains the source beyond six seconds while bounded restore work is still in progress.");
                    restoreRead.Release.Set();
                }
                Await(delegate { return fixture.Form.IsDisposed; }, "Close finishes after restoring the original.", 5000);
                Check(fixture.Source.Writes == 2, "Close confirms the active color write and then a distinct restore write.");
                fixture.Restored(); fixture.Preserved(preserved);
                Console.WriteLine(longBudget ? "PASS close: cumulative work exceeds six seconds" : "PASS close: blocked color write is followed by restore");
            }
        }
        static void RestoreSurvivesFailure(string root)
        {
            using (var paint = new Barrier()) using (var fixture = new Fixture(root))
            {
                fixture.Source.BeforeWrite = delegate(int count, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeout) { if (count == 1) paint.Pass(timeout, fixture.Source); };
                fixture.Source.AfterWrite = delegate(int count, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeout) { if (count == 1) throw new IOException("Synthetic lost confirmation after color reached the device."); };
                fixture.Start(); fixture.Ready(); var preserved = fixture.Files(); fixture.Queue(fixture.Color(0x1122AA));
                Await(delegate { return paint.Entered.WaitOne(0); }, "The failing color operation is active.");
                Call(fixture.Form, "RestoreKeyboardLighting");
                Check(fixture.State<bool>("RestoreRequested"), "Manual restore is queued while color work is blocked.");
                paint.Release.Set();
                Await(delegate { return fixture.Source.Writes == 2 && !fixture.State<bool>("Busy"); }, "A newer manual restore survives the failed color operation.");
                Check(Directory.GetFiles(fixture.Lighting, "*.failed.json").Length == 1, "The uncertain first write keeps its failed transaction record.");
                fixture.Restored(); fixture.Preserved(preserved);
                Console.WriteLine("PASS restore: request survives an in-flight write failure");
            }
        }
        static void LatestColors(string root)
        {
            using (var paint = new Barrier()) using (var fixture = new Fixture(root))
            {
                fixture.Source.BeforeWrite = delegate(int count, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeout) { if (count == 1) paint.Pass(timeout, fixture.Source); };
                fixture.Start(); fixture.Ready(); var preserved = fixture.Files();
                Tk75RgbSnapshot first = fixture.Color(0x881100), superseded = fixture.Color(0x008811), latest = fixture.Color(0x110088);
                fixture.Queue(first); Await(delegate { return paint.Entered.WaitOne(0); }, "First color is in flight.");
                fixture.Queue(superseded); fixture.Queue(latest); paint.Release.Set();
                Await(delegate { return fixture.Source.Writes == 2 && !fixture.State<bool>("Busy"); }, "The worker applies the latest queued color after the active write.");
                Check(Same(fixture.Source.Current, latest) && !fixture.Source.Written.Any(value => Same(value, superseded)), "Superseded colors are coalesced; the newest colors win.");
                Call(fixture.Form, "RestoreKeyboardLighting");
                Await(delegate { return fixture.Source.Writes == 3 && !fixture.State<bool>("Busy"); }, "Latest-color work can still restore the original.");
                fixture.Restored(); fixture.Preserved(preserved);
                Console.WriteLine("PASS colors: newest queued plan wins");
            }
        }
        static void InitializationRetries(string root, int mode)
        {
            using (var fixture = new Fixture(root))
            {
                fixture.Source.BeforeRead = delegate(int count, int timeout)
                {
                    if (mode == 2) throw new InvalidDataException("Synthetic malformed snapshot.");
                    if (mode == 1 || count < 3) throw new TimeoutException("Synthetic transient initial read timeout.");
                };
                fixture.Start();
                if (mode == 0)
                {
                    fixture.Ready(); Check(fixture.Source.Reads == 3, "Transient initialization succeeds on the third bounded attempt.");
                    Check(Directory.GetFiles(fixture.Lighting, "*.backup.json").Length == 1, "Retries publish exactly one original backup.");
                    Check(Directory.GetFiles(fixture.Lighting, "*.failure.json").Length == 2, "Each unsuccessful read retains its own immutable failure journal.");
                    var preserved = fixture.Files(); Call(fixture.Form, "RefreshRgbLighting"); fixture.Preserved(preserved);
                }
                else
                {
                    Await(delegate { return fixture.Work != null && fixture.State<bool>("Stopped"); }, "Rejected initialization stops safely.");
                    Check(fixture.Source.Reads == (mode == 1 ? 3 : 1), mode == 1 ? "Transient failures stop after three attempts." : "Malformed snapshots are never retried automatically.");
                    Check(Directory.GetFiles(fixture.Lighting, "*.backup.json").Length == 0, "Failed initialization never fabricates an original backup.");
                    var preserved = fixture.Files(); for (int i = 0; i < 10; i++) Call(fixture.Form, "RefreshRgbLighting"); fixture.Preserved(preserved);
                    Check(fixture.Source.Reads == (mode == 1 ? 3 : 1), "Routine UI refresh cannot restart a stopped initialization loop.");
                }
                Check(fixture.Source.Writes == 0, "Initialization and its retry paths perform no writes.");
                Console.WriteLine("PASS initialization: " + mode);
            }
        }
        static void PlanCache(string root)
        {
            using (var fixture = new Fixture(root))
            {
                fixture.Start(); fixture.Ready();
                Profile profile = ControllerRouting.Add(Get<EditHistory>(fixture.Form, "history").Current, "other", "Other", ControllerKind.Xbox360);
                profile.RgbOverrideEnabled = true; profile.ModeSwitchLightingEnabled = true;
                KeyboardLayout layout = KeyboardLayout.Tk75Iso(); object work = fixture.Work;
                object first = Call(fixture.Form, "GetRgbLightingPlan", work, profile, new[] { "main", "other" }, false, false, null, layout);
                for (int tick = 0; tick < 20; tick++)
                    Check(Object.ReferenceEquals(first, Call(fixture.Form, "GetRgbLightingPlan", work, profile, new[] { "other", "main" }, false, false, null, layout)), "Unchanged active-controller sets reuse the lighting plan regardless of order.");
                object active = Call(fixture.Form, "GetRgbLightingPlan", work, profile, new[] { "main" }, false, false, null, layout);
                Check(!Object.ReferenceEquals(first, active), "Changing active controllers rebuilds the plan.");
                object mode = Call(fixture.Form, "GetRgbLightingPlan", work, profile, new[] { "main" }, true, false, null, layout);
                Check(!Object.ReferenceEquals(active, mode), "Changing keyboard mode rebuilds the plan.");
                Profile changed = ControllerRouting.SetRgbColor(profile, "main", 0x224466);
                object color = Call(fixture.Form, "GetRgbLightingPlan", work, changed, new[] { "main" }, true, false, null, layout);
                Check(!Object.ReferenceEquals(mode, color), "Changing profile colors rebuilds the plan.");
                KeyboardLayout anotherLayout = KeyboardLayout.Tk75Ansi();
                object keyboard = Call(fixture.Form, "GetRgbLightingPlan", work, changed, new[] { "main" }, true, false, null, anotherLayout);
                Check(!Object.ReferenceEquals(color, keyboard), "Changing physical layout rebuilds the plan.");
                object shortcut = Call(fixture.Form, "GetRgbLightingPlan", work, changed, new[] { "main" }, true, true, (int?)14, anotherLayout);
                Check(!Object.ReferenceEquals(keyboard, shortcut), "Activating the effective shortcut rebuilds the plan.");
                object physical = Call(fixture.Form, "GetRgbLightingPlan", work, changed, new[] { "main" }, true, true, (int?)9, anotherLayout);
                Check(!Object.ReferenceEquals(shortcut, physical), "Changing the shortcut's physical key rebuilds the plan.");

                Profile disabled = Get<EditHistory>(fixture.Form, "history").Current;
                disabled.RgbOverrideEnabled = false; disabled.ModeSwitchLightingEnabled = false; Call(fixture.Form, "Commit", disabled);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                object live = Get<object>(fixture.Form, "rgbPlanCache");
                for (int tick = 0; tick < 20; tick++) Call(fixture.Form, "RefreshRgbLighting");
                Check(Object.ReferenceEquals(live, Get<object>(fixture.Form, "rgbPlanCache")), "Actual unchanged UI refreshes reuse the cached plan.");
                int reads = fixture.Source.Reads; Call(fixture.Form, "RestoreKeyboardLighting"); Call(fixture.Form, "RefreshRgbLighting");
                Await(delegate { return fixture.Source.Reads > reads; }, "Manual restore still runs its fresh read even when the color plan is cached."); fixture.Idle();
                lock (Get<object>(work, "Gate")) Set(work, "RecoveryRequired", true);
                Call(fixture.Form, "RefreshRgbLighting");
                Check(Get<object>(fixture.Form, "rgbPlanCache") == null, "Losing RGB readiness invalidates the plan cache.");
                lock (Get<object>(work, "Gate")) Set(work, "RecoveryRequired", false);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                Check(Get<object>(fixture.Form, "rgbPlanCache") != null, "Recovered readiness rebuilds the plan cache.");
                Check(fixture.Source.Writes == 0, "Cache checks and original-state restore never write simulated lighting.");
                Console.WriteLine("PASS cache: input changes invalidate; unchanged refreshes and restore remain correct");
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length != 1) throw new ArgumentException("One dedicated short artifact root is required.");
                string root = Path.GetFullPath(args[0]);
                CloseDuringWrite(Path.Combine(root, "a"), false);
                RestoreSurvivesFailure(Path.Combine(root, "b"));
                LatestColors(Path.Combine(root, "c"));
                CloseDuringWrite(Path.Combine(root, "d"), true);
                InitializationRetries(Path.Combine(root, "e"), 0);
                InitializationRetries(Path.Combine(root, "f"), 1);
                InitializationRetries(Path.Combine(root, "g"), 2);
                PlanCache(Path.Combine(root, "h"));
                Console.WriteLine("PASS: " + checks + " RGB lifecycle assertions; actual MainForm worker and ReaderSession, synthetic source only.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
