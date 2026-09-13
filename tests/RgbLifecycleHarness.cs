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
            public void SetCurrent(Tk75RgbSnapshot value) { lock (gate) current = value; }
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
            public void AwaitGate(Barrier barrier, string message)
            {
                Await(delegate
                {
                    if (barrier.Entered.WaitOne(0)) return true;
                    string error = State<string>("Error");
                    if (error != null || State<bool>("Stopped"))
                        throw new InvalidOperationException(message + " Worker failed before entering the synthetic transport: " + error +
                            "; state=" + State<int>("State") + ", stopped=" + State<bool>("Stopped") +
                            ", reads=" + Source.Reads + ", writes=" + Source.Writes);
                    return false;
                }, message);
            }
            public void Idle()
            {
                // These fields form one state: the worker consumes its queue and
                // sets Busy under the same gate. Separate reads could combine
                // old Busy=false with an already-consumed new request and report
                // idle while the next read/write is actually in progress.
                object work = Work, gate = Get<object>(Work, "Gate");
                try
                {
                    Await(delegate
                    {
                        lock (gate) return !Get<bool>(work, "Busy") && Get<Tk75RgbSnapshot>(work, "Desired") == null && !Get<bool>(work, "RestoreRequested");
                    }, "The worker consumes all queued work.");
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException("RGB idle state: busy=" + State<bool>("Busy") + ", stopped=" + State<bool>("Stopped") +
                        ", abort=" + State<bool>("Abort") + ", quit=" + State<bool>("QuitAfterRestore") + ", restore=" + State<bool>("RestoreRequested") +
                        ", state=" + State<int>("State") + ", error=" + State<string>("Error") + ", reader=" + Reader.IsReading +
                        ", writes=" + Source.Writes + ", reads=" + Source.Reads, error);
                }
            }
            public void CloseWithRestore()
            {
                Form.Close();
                Check(!Form.IsDisposed && Get<bool>(Form, "rgbClosePending"), "Actual window close defers disposal for the asynchronous restore path.");
                Check(Get<bool>(Form, "closeProfileSaved"), "Actual window close saves the profile before waiting for restore.");
                object firstWork = Work;
                Form.Close();
                Check(!Form.IsDisposed && Object.ReferenceEquals(firstWork, Work), "Repeated window close neither bypasses restore nor starts a replacement lighting worker.");
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
                fixture.Queue(fixture.Color(0xAA2211)); fixture.AwaitGate(paint, "Color change is held inside the actual worker.");
                foreach (var item in fixture.Files()) preserved[item.Key] = item.Value;
                fixture.CloseWithRestore(); Stopwatch closing = Stopwatch.StartNew();
                if (longBudget) Until(closing, 3300);
                paint.Release.Set();
                if (longBudget)
                {
                    fixture.AwaitGate(restoreRead, "Close performs a fresh restore read after the in-flight write.");
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
                fixture.AwaitGate(paint, "The failing color operation is active.");
                Call(fixture.Form, "RestoreKeyboardLighting");
                Check(fixture.State<bool>("RestoreRequested"), "Manual restore is queued while color work is blocked.");
                paint.Release.Set();
                Await(delegate { return fixture.Source.Writes == 2 && !fixture.State<bool>("Busy"); }, "A newer manual restore survives the failed color operation.");
                Check(Directory.GetFiles(fixture.Lighting, "*.failed.json").Length == 1, "The uncertain first write keeps its failed transaction record.");
                fixture.Restored(); fixture.Preserved(preserved);
                Console.WriteLine("PASS restore: request survives an in-flight write failure");
            }
        }

        static void WindowsShutdownDuringRestore(string root)
        {
            using (var restoreRead = new Barrier()) using (var fixture = new Fixture(root))
            {
                fixture.Start(); fixture.Ready();
                fixture.Queue(fixture.Color(0xAA2211)); fixture.Idle();
                Check(!Same(fixture.Source.Current, fixture.Source.Original), "The synthetic keyboard actually has temporary lighting before shutdown.");
                var preserved = fixture.Files();
                int restoreReadNumber = fixture.Source.Reads + 1;
                fixture.Source.BeforeRead = delegate(int count, int timeout) { if (count == restoreReadNumber) restoreRead.Pass(timeout, fixture.Source); };
                fixture.CloseWithRestore();
                fixture.AwaitGate(restoreRead, "Normal close is genuinely blocked in an in-flight restore read.");
                Set(fixture.Form, "systemShutdownWaitMilliseconds", 3000);
                var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
                Stopwatch elapsed = Stopwatch.StartNew();
                typeof(Form).GetMethod("OnFormClosing", Fields).Invoke(fixture.Form, new object[] { args });
                Check(!args.Cancel && elapsed.ElapsedMilliseconds < 4500, "Windows shutdown does not wait indefinitely or cancel an already pending normal RGB close.");
                Check(Get<bool>(fixture.Form, "closing"), "System shutdown seals the UI even while RGB is pending.");
                Check(!fixture.Source.Disposed && !fixture.State<bool>("Abort"), "The injected short session-end test deadline does not dispose the reader or cancel its pending restore.");
                fixture.Preserved(preserved);
                string backup = Directory.GetFiles(fixture.Lighting, "*.backup.json").Single();
                Check((string)ReadJournal(backup)["SnapshotBase64"] == Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(fixture.Source.Original)), "An interrupted restore retains the exact durable original backup.");
                elapsed.Restart(); fixture.Form.Dispose();
                Check(elapsed.ElapsedMilliseconds < 1000, "Final Form.Dispose does not wait again for the pending RGB operation.");
                restoreRead.Release.Set();
                Check(Get<ShutdownWork>(fixture.Form, "systemShutdownWork").Wait(5000), "The original cleanup lanes can finish after the blocked fake transport is released.");
                Await(delegate { return fixture.State<bool>("Stopped"); }, "The interrupted lighting worker stops without requiring a live UI callback.");
                fixture.Restored();
                fixture.Preserved(preserved);
                Check(Get<bool>(fixture.Form, "systemShutdownStarted"), "An earlier successful ordinary save does not convert interrupted OS shutdown into a normal clean exit.");
                Console.WriteLine("PASS Windows shutdown: pending restore survives the injected test budget and restores the full original");
            }
        }
        static void CloseRetriesTransientRestore(string root)
        {
            using (var fixture = new Fixture(root))
            {
                fixture.Start(); fixture.Ready(); fixture.Queue(fixture.Color(0xCCAABB)); fixture.Idle();
                var preserved = fixture.Files();
                int restoreRead = fixture.Source.Reads + 1;
                fixture.Source.BeforeRead = delegate(int count, int timeout)
                { if (count == restoreRead) throw new IOException("Synthetic transient readback failure."); };
                fixture.Form.Close();
                Await(delegate { return fixture.Form.IsDisposed; }, "Normal exit retries one transient restore read and finishes.");
                Check(fixture.Source.Reads == restoreRead + 1 && Get<bool>(fixture.Form, "rgbCloseSucceeded"),
                    "Only a successful fresh readback marks the retried close as restored.");
                fixture.Restored(); fixture.Preserved(preserved);
            }
        }
        static void LatestColors(string root)
        {
            using (var paint = new Barrier()) using (var fixture = new Fixture(root))
            {
                fixture.Source.BeforeWrite = delegate(int count, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeout) { if (count == 1) paint.Pass(timeout, fixture.Source); };
                fixture.Start(); fixture.Ready(); var preserved = fixture.Files();
                Tk75RgbSnapshot first = fixture.Color(0x881100), superseded = fixture.Color(0x008811), latest = fixture.Color(0x110088);
                fixture.Queue(first); fixture.AwaitGate(paint, "First color is in flight.");
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
        static string RootAtLength(string parent, int length)
        {
            int remaining = length - parent.Length - 1;
            if (remaining < 1) throw new InvalidOperationException("The dedicated temporary RGB root is too long for the path-budget test.");
            return Path.Combine(parent, new string('p', remaining));
        }
        static void JournalPathBudget(string root)
        {
            // Same full data-root length as the ordinary Windows ZIP extraction:
            // C:\Users\sven9\Downloads\AnalogKeyMapper-1.0.0-rc.1-windows-x64\AnalogKeyMapper\data
            using (var fixture = new Fixture(RootAtLength(root, 84)))
            {
                fixture.Start(); fixture.Ready();
                string backup = Directory.GetFiles(fixture.Lighting, "*.backup.json").Single();
                var metadata = ReadJournal(backup);
                string name = Path.GetFileName(backup), identity = (string)metadata["IdentitySha256"];
                string token = name.Substring(("rgb-" + identity + "-").Length);
                token = token.Substring(0, token.Length - ".backup.json".Length);
                byte[] requestBytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "==");
                Check(token.Length == 22 && new Guid(requestBytes).ToString("N") == (string)metadata["RequestId"], "The short file token preserves every request GUID bit; full identity and time remain in the journal.");
                Check(metadata.ContainsKey("StartedUtc") && name.StartsWith("rgb-" + identity + "-"), "Journal shortening preserves the full device identity and recorded timestamp.");
                var preserved = fixture.Files();
                fixture.Queue(fixture.Color(0x665544)); fixture.Idle();
                Check(!Same(fixture.Source.Current, fixture.Source.Original) && fixture.Source.Writes == 1, "A normal download path allows a fully journaled color write.");
                Call(fixture.Form, "RestoreKeyboardLighting"); fixture.Idle(); fixture.Restored();
                int writes = fixture.Source.Writes;
                Call(fixture.Form, "RestoreKeyboardLighting"); fixture.Idle();
                string resolution = Directory.GetFiles(fixture.Lighting, "*.resolved-*.json").Single();
                Check(resolution.Length + ".pending".Length < 260 && fixture.Source.Writes == writes, "The longest resolution journal also fits without a redundant device write.");
                fixture.Preserved(preserved);
                Console.WriteLine("PASS path: typical download data root 84 characters; longest actual staged journal " + (resolution.Length + ".pending".Length));
            }
            using (var fixture = new Fixture(RootAtLength(root, 100)))
            {
                fixture.Start(); Await(delegate { return fixture.Work != null && fixture.State<bool>("Stopped"); }, "An unsupported path budget is rejected during initialization.");
                Check(fixture.Source.Reads == 0 && fixture.Source.Writes == 0 && fixture.State<int>("State") == 2, "Too-long future journal paths are rejected before the initial device read or readiness.");
                Check(Directory.GetFiles(fixture.Lighting, "*.backup.json").Length == 0 && fixture.State<Tk75RgbSnapshot>("Original") == null, "A rejected path never publishes a misleading usable backup.");
            }
        }
        static void LegacyTimestampRecovery(string root)
        {
            Dictionary<string, byte[]> originalFiles;
            Tk75RgbSnapshot applied;
            string compactPrefix, legacyPrefix;
            using (var seed = new Fixture(Path.Combine(root, "seed")))
            {
                seed.Start(); seed.Ready(); seed.Queue(seed.Color(0xAA5544)); seed.Idle();
                Check(!Same(seed.Source.Current, seed.Source.Original), "Legacy recovery fixture contains a real journaled temporary color.");
                applied = seed.Source.Current; originalFiles = seed.Files();
                string backup = originalFiles.Keys.Single(path => path.EndsWith(".backup.json", StringComparison.Ordinal));
                var metadata = ReadJournal(backup);
                compactPrefix = Path.GetFileName(backup).Substring(0, Path.GetFileName(backup).Length - ".backup.json".Length);
                legacyPrefix = "rgb-" + metadata["IdentitySha256"] + "-20260911-000000-" + metadata["RequestId"];
            }
            string directory = Path.Combine(root, "legacy"), lighting = Path.Combine(directory, "lighting");
            Directory.CreateDirectory(lighting);
            // These are new, synthetic historical-format fixtures. The seed
            // journals and every subsequently discovered backup remain immutable.
            foreach (var file in originalFiles)
            {
                string name = Path.GetFileName(file.Key).Replace(compactPrefix, legacyPrefix);
                string json = System.Text.Encoding.UTF8.GetString(file.Value).Replace(compactPrefix, legacyPrefix);
                File.WriteAllText(Path.Combine(lighting, name), json, new System.Text.UTF8Encoding(false));
            }
            using (var fixture = new Fixture(directory))
            {
                var preserved = fixture.Files(); fixture.Source.SetCurrent(applied);
                fixture.Start(); fixture.Ready(); fixture.Idle();
                fixture.Restored(); fixture.Preserved(preserved);
                Check(Directory.GetFiles(lighting, "*.backup.json").Length == 1 && Path.GetFileName(fixture.State<string>("BackupFile")).StartsWith(legacyPrefix), "Recovery reuses the exact timestamp-format backup instead of replacing it.");
                Check(fixture.Source.Writes == 1, "A legacy unfinished override is restored with one journaled exchange.");
                Console.WriteLine("PASS legacy: timestamp-format backup and transaction remain recoverable and unchanged");
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
        static void ShortcutDialogKeepsLighting(string root)
        {
            using (var fixture = new Fixture(root))
            {
                fixture.Start(); fixture.Ready();
                Profile profile = Get<EditHistory>(fixture.Form, "history").Current;
                profile.RgbOverrideEnabled = false; profile.ModeSwitchLightingEnabled = true;
                profile.ModeSwitchHotkey.Enabled = true; profile.ModeSwitchHotkey.KeyCode = (int)Keys.F9;
                profile.ModeSwitchRgbColor = 0x112233;
                Get<EditHistory>(fixture.Form, "history").Commit(profile);
                // Model the successful registration state only. This fixture
                // never invokes RegisterHotKey or sends global keyboard input.
                Set(fixture.Form, "modeShortcutRegistrationActive", true); Set(fixture.Form, "modeHotkey", true);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                Check(fixture.Source.Writes == 1 && !Same(fixture.Source.Current, fixture.Source.Original), "The saved physical shortcut marker is applied through the real journaled worker.");
                object plan = Get<object>(fixture.Form, "rgbPlanCache"); var preserved = fixture.Files();
                Set(fixture.Form, "modeShortcutDialogOpen", true); Set(fixture.Form, "modeHotkey", false);
                for (int tick = 0; tick < 12; tick++) Call(fixture.Form, "RefreshRgbLighting");
                fixture.Idle();
                Check(fixture.Source.Writes == 1 && Object.ReferenceEquals(plan, Get<object>(fixture.Form, "rgbPlanCache")),
                    "Pausing hotkeys inside their editor preserves the applied lighting plan without a restore/reapply flash.");
                profile = Get<EditHistory>(fixture.Form, "history").Current; profile.ModeSwitchRgbColor = 0x445566;
                Get<EditHistory>(fixture.Form, "history").Commit(profile);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                Check(fixture.Source.Writes == 1, "Applying the edited profile while the modal dialog is still open does not insert an unmarked picture.");
                Set(fixture.Form, "modeShortcutDialogOpen", false); Set(fixture.Form, "modeHotkey", true);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                Check(fixture.Source.Writes == 2 && fixture.Source.Written.All(snapshot => !Same(snapshot, fixture.Source.Original)),
                    "Closing the editor writes the final chosen marker directly, without an intermediate original-lighting transaction.");
                fixture.Preserved(preserved);
                Set(fixture.Form, "modeShortcutDialogOpen", true); Set(fixture.Form, "modeHotkey", false);
                Call(fixture.Form, "RefreshRgbLighting");
                Set(fixture.Form, "modeShortcutDialogOpen", false); Set(fixture.Form, "modeHotkey", true);
                Call(fixture.Form, "RefreshRgbLighting"); fixture.Idle();
                Check(fixture.Source.Writes == 2, "Cancelling the unchanged editor causes no physical lighting write.");

                Set(fixture.Form, "modeShortcutDialogOpen", true); Set(fixture.Form, "modeHotkey", false);
                Set(fixture.Form, "reader", null); Call(fixture.Form, "RefreshRgbLighting");
                Check(Get<object>(fixture.Form, "rgbPlanCache") == null, "A missing source still invalidates the lighting plan while the shortcut editor is open.");
                Set(fixture.Form, "reader", fixture.Reader);
                int reads = fixture.Source.Reads;
                Call(fixture.Form, "RestoreKeyboardLighting"); fixture.Idle();
                Check(fixture.Source.Reads > reads, "Explicit restore still performs its fresh read while ordinary dialog color plans are paused.");
                fixture.Restored();
                Set(fixture.Form, "modeShortcutRegistrationActive", false);
                Console.WriteLine("PASS shortcut lighting: edit/cancel preserve colors; final choice writes directly; identity and explicit restore remain live");
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
                ShortcutDialogKeepsLighting(Path.Combine(root, "l"));
                WindowsShutdownDuringRestore(Path.Combine(root, "i"));
                CloseRetriesTransientRestore(Path.Combine(root, "k"));
                JournalPathBudget(root);
                LegacyTimestampRecovery(Path.Combine(root, "j"));
                Console.WriteLine("PASS: " + checks + " RGB lifecycle assertions; actual MainForm worker and ReaderSession, synthetic source only.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
