using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Runs the real form and physical-source owner against a synthetic reader.
    // Preview mode prevents hardware discovery, global hooks and output creation.
    public static class GamepadSourceLifecycleUiHarness
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Private).SetValue(owner, value); }
        static object Call(object owner, string name, params object[] args)
        {
            try { return owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
            catch (TargetInvocationException error) { throw new InvalidOperationException(name + " failed.", error.InnerException); }
        }
        static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < 1e-10, message + ": " + actual); }
        static Profile Current(MainForm form) { return Field<EditHistory>(form, "history").Current; }
        static Dictionary<string, ILearnedInputDeviceSource> Sources(MainForm form) { return Field<Dictionary<string, ILearnedInputDeviceSource>>(form, "learnedSources"); }
        static LearnedInputRouting Routing(MainForm form) { return Field<LearnedInputRouting>(form, "learnedInputRouting"); }
        static void Pump() { Application.DoEvents(); }
        static bool Await(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++) { if (ready()) return true; Thread.Sleep(5); }
            return ready();
        }
        sealed class SyntheticReader : IGamepadInputReader
        {
            internal readonly AutoResetEvent Next = new AutoResetEvent(false);
            internal readonly ManualResetEvent Closed = new ManualResetEvent(false);
            internal ControllerFrame Frame = new ControllerFrame { Buttons = 0x1000, LeftX = .123456, RightTrigger = .4321 };
            internal bool Disconnect;
            internal int Disposals;
            public bool Read(WaitHandle stop, out ControllerFrame frame)
            {
                frame = null;
                if (WaitHandle.WaitAny(new[] { stop, Next }) == 0) return false;
                if (Disconnect) throw new IOException("Synthetic physical disconnect.");
                frame = Frame; return true;
            }
            public void Dispose() { Interlocked.Increment(ref Disposals); Closed.Set(); }
        }
        static GamepadInputSource Attach(MainForm form, string path, SyntheticReader reader, bool pump = true)
        {
            var device = new GamepadInputDevice(path, "Synthetic " + path, ControllerKind.Xbox360, Guid.Empty, null, false);
            var source = new GamepadInputSource(device, reader);
            Sources(form).Add(source.DeviceId, source);
            Call(form, "Commit", GamepadInputProfile.WithDevice(Current(form), source.DeviceId, source.DisplayName));
            if (pump) Pump(); reader.Next.Set();
            Check(Await(delegate { double value; return source.TryGetValue("A", out value) && value == 1; }), "Synthetic native reader publishes held button.");
            return source;
        }
        static Dictionary<int, double> Snapshot(MainForm form)
        { var values = new Dictionary<int, double>(); Routing(form).CopyRawSnapshot(500, values); return values; }
        static ControllerFrame Compose(MainForm form, bool available = true)
        {
            var frame = MappingEngine.Compose(Snapshot(form), Field<KeyboardPressureRange>(form, "sharedPressureRange").Resolve(),
                Current(form), new Dictionary<string, SignalState>(), .005);
            Check(available ? frame.Errors.Count == 0 : frame.Errors.Count == Current(form).Bindings.Count,
                available ? "Current real form configuration composes without errors: " + string.Join("; ", frame.Errors.ToArray()) : "Disconnected mappings report unknown input."); return frame;
        }
        static void Passive(MainForm form)
        {
            var runtime = Field<MultiControllerSession>(form, "runtime");
            Check(!runtime.AnyEnabled && runtime.ActiveControllerIds.Length == 0, "Lifecycle regression never creates virtual outputs.");
            Check(Field<object>(form, "reader") == null, "Lifecycle regression never opens a physical keyboard.");
            Check(!Field<System.Windows.Forms.Timer>(form, "uiTimer").Enabled, "Passive form never starts device polling.");
        }
        sealed class FakeController : IControllerSession
        {
            internal int Disconnects;
            public bool Enabled { get; private set; }
            public ControllerFrame Frame { get { return new ControllerFrame(); } }
            public PreviewSnapshot Preview { get { return new PreviewSnapshot(); } }
            public string Status { get { return "Synthetic output state only"; } }
            public void Configure(Profile profile, IDictionary<int, Calibration> calibration) { }
            public void SetInputSource(object source) { }
            public void SetKeyboardMode(bool value) { }
            public void Enable() { Enabled = true; }
            public void Disable(string reason) { if (Enabled) Disconnects++; Enabled = false; }
            public ControllerRelease PrepareDisable(string reason) { Disable(reason); return null; }
            public void Dispose() { Enabled = false; }
        }
        static ReaderSession InertKeyboard()
        {
            var device = new CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "Synthetic TK75", version = 0x0403, usagePage = 65535, usage = 1, inputReportLength = 32 };
            device.reportCapabilities.Add(new Dictionary<string, object> { { "reportType", "input" }, { "kind", "value" }, { "reportId", 5 }, { "usagePage", 65535 }, { "bitSize", 8 }, { "reportCount", 31 } });
            return new ReaderSession(device, new RongYuanTravel32(), delegate { throw new InvalidOperationException("This regression must never open a keyboard reader."); });
        }
        static void PostFault(MainForm form, ReaderSession reader)
        {
            // Use the production callback from a worker, then deliberately keep
            // the UI queue unpumped so a source switch can overtake the fault.
            Exception failure = null;
            var worker = new Thread(delegate() { try { Call(form, "OnReaderFault", reader, "Synthetic keyboard failure"); } catch (Exception error) { failure = error; } });
            worker.IsBackground = true; worker.Start();
            Check(worker.Join(2000), "Reader fault callback does not wait for the UI thread.");
            if (failure != null) throw failure;
        }
        static void FaultIsolation(string root)
        {
            string data = Path.Combine(root, "synthetic-reader-fault-data");
            Check(!Directory.Exists(data), "Fault regression uses fresh profile data.");
            using (var keyboard = InertKeyboard())
            using (var replacement = InertKeyboard())
            using (var form = new MainForm(data, true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
            {
                form.Show(); Pump();
                var endpoints = new List<FakeController>();
                var runtime = new MultiControllerSession(delegate { var endpoint = new FakeController(); endpoints.Add(endpoint); return endpoint; });
                Field<MultiControllerSession>(form, "runtime").Dispose(); Set(form, "runtime", runtime);
                Set(form, "reader", keyboard); Call(form, "Configure");
                runtime.Enable(); int before = endpoints.Sum(endpoint => endpoint.Disconnects);
                PostFault(form, keyboard);
                Check(runtime.AnyEnabled, "Keyboard worker posts a fault without touching UI state directly.");
                Pump();
                Check(!runtime.AnyEnabled && endpoints.Sum(endpoint => endpoint.Disconnects) == before + 1, "An active keyboard fault still disconnects its output on the UI thread.");

                Call(form, "SwitchInputSourceMode", InputMode.XboxController);
                var gamepad = Attach(form, "fault-isolation-controller", new SyntheticReader());
                Check(Object.ReferenceEquals(Field<ReaderSession>(form, "reader"), keyboard), "Controller mode retains the old keyboard session.");
                runtime.Enable(); before = endpoints.Sum(endpoint => endpoint.Disconnects);
                PostFault(form, keyboard); Pump();
                Check(runtime.AnyEnabled && endpoints.Sum(endpoint => endpoint.Disconnects) == before, "A retained keyboard fault cannot disconnect current gamepad output.");
                Check(gamepad.IsReading && Snapshot(form).Count == 24, "An unrelated keyboard fault leaves gamepad routing live.");

                Call(form, "SwitchInputSourceMode", InputMode.Keyboard); runtime.Enable();
                PostFault(form, keyboard);
                Call(form, "SwitchInputSourceMode", InputMode.XboxController);
                gamepad = Attach(form, "queued-fault-controller", new SyntheticReader(), false);
                runtime.Enable(); before = endpoints.Sum(endpoint => endpoint.Disconnects); Pump();
                Check(runtime.AnyEnabled && endpoints.Sum(endpoint => endpoint.Disconnects) == before && gamepad.IsReading,
                    "A keyboard fault queued before a mode switch cannot disconnect the newly active gamepad output.");

                Call(form, "SwitchInputSourceMode", InputMode.Keyboard); runtime.Enable(); PostFault(form, keyboard);
                Set(form, "reader", replacement); Call(form, "Configure"); runtime.Enable(); before = endpoints.Sum(endpoint => endpoint.Disconnects); Pump();
                Check(runtime.AnyEnabled && endpoints.Sum(endpoint => endpoint.Disconnects) == before, "A queued fault from a replaced reader cannot disconnect its replacement.");
                PostFault(form, replacement); Set(form, "closing", true); Pump();
                Check(runtime.AnyEnabled && endpoints.Sum(endpoint => endpoint.Disconnects) == before, "Queued faults leave closing-time output cleanup to the shutdown owner.");
                Set(form, "closing", false); form.Dispose(); PostFault(form, replacement);
                Check(!runtime.AnyEnabled, "A late callback after form disposal completes without reactivating resources.");
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            MainForm form = null;
            try
            {
                if (args.Length != 1) throw new ArgumentException("Pass a fresh test directory.");
                string data = Path.Combine(Path.GetFullPath(args[0]), "synthetic-lifecycle-data");
                Check(!Directory.Exists(data), "Never reuse real or existing profile data.");
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                form = new MainForm(data, true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
                form.Show(); Pump(); Passive(form);
                string keyboardProfile = ProfileJson.Serialize(Current(form));
                var keyboardCalibration = new CalibrationDocument { GlobalMinimum = 50, GlobalMaximum = 900, ScaleMaximum = 1000 };
                Set(form, "calibration", keyboardCalibration); Call(form, "Configure");
                Call(form, "SwitchInputSourceMode", InputMode.XboxController); Pump();
                var native = new SyntheticReader(); var source = Attach(form, "first-controller", native);
                Check(Sources(form).Count == 1 && Routing(form).IsReading, "Exactly the explicitly chosen source becomes active.");
                var routed = Routing(form);
                Check(Object.ReferenceEquals(Field<object>(Field<MultiControllerSession>(form, "runtime"), "inputSource"), routed), "Output runtime owns the current logical input router.");
                var range = Field<KeyboardPressureRange>(form, "sharedPressureRange");
                Check(range.Minimum == 0 && range.Maximum == DefaultCalibration.Bottom, "Keyboard calibration cannot distort normalized controller inputs.");
                var frame = Compose(form); Near(frame.LeftX, .123456, "Fine stick movement survives the actual form configuration.");
                Near(frame.RightTrigger, .4321, "Analog trigger survives the actual form configuration."); Check((frame.Buttons & 0x1000) != 0, "Button reaches mapped frame.");

                // Reconfiguring mapping and output type must retain source identity
                // and refresh endpoint input wiring even when routes do not change.
                var edited = Current(form);
                edited.Bindings.Single(binding => binding.KeyIndex == GamepadInputProfile.KeyIndex(OutputTarget.A)).Target = OutputTarget.B;
                Call(form, "Commit", edited); Pump();
                Check(source.IsReading && native.Disposals == 0 && Object.ReferenceEquals(source, Sources(form)[source.DeviceId]), "Mapping edit retains the physical source.");
                frame = Compose(form); Check((frame.Buttons & 0x2000) != 0 && (frame.Buttons & 0x1000) == 0, "Mapping edit takes effect while input remains connected.");
                Call(form, "Commit", ControllerRouting.SetKind(Current(form), ControllerRouting.DefaultControllerId, ControllerKind.DualSense)); Pump();
                Check(source.IsReading && native.Disposals == 0, "Output protocol change keeps the physical input handle.");
                Check(Object.ReferenceEquals(Field<object>(Field<MultiControllerSession>(form, "runtime"), "inputSource"), Routing(form)), "Output protocol change remains attached to active logical routing.");
                Near(Compose(form).LeftX, .123456, "Configuration round trip does not lose retained analog values.");

                native.Disconnect = true; native.Next.Set();
                Check(native.Closed.WaitOne(1000), "Disconnect closes synthetic native reader.");
                Check(!source.IsReading && !Routing(form).IsReading && Snapshot(form).Count == 0, "Disconnect removes every held input from real form routing.");
                frame = Compose(form, false); Check(frame.Buttons == 0 && frame.LeftX == 0 && frame.RightTrigger == 0, "Disconnected input composes neutral instead of latching held controls.");
                foreach (var state in Routing(form).GetUiSnapshot(500)) Check(!state.Known && state.Stale, "Disconnected source is visibly unknown.");
                Call(form, "Configure"); Check(Snapshot(form).Count == 0 && native.Disposals == 1, "Reconfiguration cannot resurrect a disconnected source.");

                var secondNative = new SyntheticReader(); var second = Attach(form, "second-controller", secondNative);
                Check(Sources(form).Count == 1 && !Sources(form).ContainsKey(source.DeviceId) && Sources(form).ContainsKey(second.DeviceId), "Changing explicit source removes the previous identity.");
                Check(Snapshot(form).Count == 24, "New explicitly selected source restores all logical inputs.");
                Call(form, "SwitchInputSourceMode", InputMode.Keyboard); Pump();
                Check(secondNative.Closed.WaitOne(1000) && secondNative.Disposals == 1 && !second.IsReading, "Keyboard mode disposes the controller input exactly once.");
                Check(Sources(form).Count == 0 && Routing(form) == null, "Keyboard mode detaches controller routes and ownership.");
                Check(ProfileJson.Serialize(Current(form)) == keyboardProfile, "Keyboard mappings survive switching input families.");
                Check(Object.ReferenceEquals(Field<CalibrationDocument>(form, "calibration"), keyboardCalibration), "Keyboard calibration is restored after controller mode.");
                Call(form, "SwitchInputSourceMode", InputMode.XboxController); Pump();
                Check(Current(form).InputDeviceId == second.DeviceId && Snapshot(form).Count == 0, "Saved source selection returns but disposed input is not silently reused.");
                Check(!Routing(form).IsReading, "Preview does not reopen real hardware on saved-profile switch.");
                var thirdNative = new SyntheticReader(); Attach(form, "third-controller", thirdNative); Passive(form);
                form.Dispose(); form = null;
                Check(thirdNative.Closed.WaitOne(1000) && thirdNative.Disposals == 1, "Form disposal closes its remaining source exactly once.");
                FaultIsolation(Path.GetFullPath(args[0]));
                Console.WriteLine("PASS: " + checks + " MainForm controller source lifecycle assertions; synthetic input, no hardware or output.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (form != null) form.Dispose(); }
        }
    }
}
