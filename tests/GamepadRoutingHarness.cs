using System;
using System.Collections.Generic;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

// The real routing and mapping pipeline runs against synthetic retained input
// values. This stand-in cannot open a hardware handle or produce virtual output.
namespace Tk75.Diagnostics { public sealed class CollectionInfo { public string devicePath; } }
namespace Tk75.App
{
    public sealed class ReaderSession
    {
        public readonly CollectionInfo Device = new CollectionInfo { devicePath = "test-keyboard" };
        public string Fingerprint = new string('f', 64);
        public bool IsReading = true, HasReceivedSamples = true;
        public event Action<TravelSample, double> Sample { add { } remove { } }
        public KeyStateSnapshot[] GetUiSnapshot(double age) { return new KeyStateSnapshot[0]; }
        internal void CopyRawSnapshot(double age, Dictionary<int, double> result)
        { result.Clear(); result[0] = 385; result[10] = 385; result[42] = 385; }
    }
}

public static class GamepadRoutingHarness
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }
    static void Near(double expected, double actual, string message)
    { Check(Math.Abs(expected - actual) < 1e-12, message + ": " + actual); }
    sealed class Source : ILearnedInputDeviceSource
    {
        public string DeviceId { get; private set; }
        public string DisplayName { get { return "Synthetic controller"; } }
        public bool IsReading { get; set; }
        public InputControlDescriptor[] Controls { get; private set; }
        public string Status { get { return IsReading ? "Connected" : "Disconnected"; } }
        public bool Disposed;
        public readonly Dictionary<string, double> Values = new Dictionary<string, double>();
        public event Action<InputControlSample> Sample;
        public Source(string id)
        {
            DeviceId = id; IsReading = true; Controls = new InputControlDescriptor[24];
            for (int i = 0; i < Controls.Length; i++)
            {
                string control = GamepadInputProfile.ControlId(i);
                Controls[i] = new InputControlDescriptor(control, control, GamepadInputProfile.IsAnalog(i) ? InputControlKind.Absolute : InputControlKind.Button, 0, 1, 0, null);
                Values.Add(control, 0);
            }
        }
        public void Send(OutputTarget target, double value)
        {
            string control = target.ToString(); Values[control] = value;
            var handler = Sample; if (handler != null) handler(new InputControlSample(control, value, 0));
        }
        public bool TryGetValue(string controlId, out double value)
        { value = 0; return IsReading && Values.TryGetValue(controlId, out value); }
        public void Dispose() { Disposed = true; IsReading = false; }
    }
    static Dictionary<int, double> Snapshot(LearnedInputRouting router)
    { var values = new Dictionary<int, double>(); router.CopyRawSnapshot(500, values); return values; }
    static ControllerFrame Compose(Profile profile, Dictionary<int, double> values, double scale)
    {
        var calibration = new Dictionary<int, Calibration>();
        for (int key = 0; key < 24; key++) calibration.Add(key, new Calibration(0, scale));
        var frame = MappingEngine.Compose(values, calibration, profile, new Dictionary<string, SignalState>(), .005);
        Check(frame.Errors.Count == 0, "Snapshot composes without mapping errors"); return frame;
    }
    public static void Run()
    {
        Profile profile = GamepadInputProfile.WithDevice(GamepadInputProfile.Create(InputMode.XboxController, "Precision"), new string('a', 64), "Synthetic controller");
        var source = new Source(profile.InputDeviceId);
        foreach (double scale in new[] { 385.0, 100.0, 65535.0 })
            using (var router = new LearnedInputRouting(null, profile.LearnedInputs, scale, new[] { source }))
            {
                source.Send(OutputTarget.LeftXPositive, .123456); source.Send(OutputTarget.RightTrigger, .0000123);
                source.Send(OutputTarget.A, 1);
                var snapshot = Snapshot(router);
                Check(snapshot.Count == 24 && !snapshot.ContainsKey(42), "Controller mode reads only its controller controls without physical-keyboard fallback");
                Near(.123456 * scale, snapshot[0], "Analog precision is retained in snapshot");
                Near(.0000123 * scale, snapshot[9], "Small trigger motion is not rounded away by keyboard pressure range");
                var frame = Compose(profile, snapshot, scale);
                Near(.123456, frame.LeftX, "Analog precision survives mapping normalization");
                Near(.0000123, frame.RightTrigger, "Fine trigger movement survives complete routing pipeline");
                Check((frame.Buttons & 0x1000) != 0, "Controller button reaches output");
                for (int key = 0; key < 24; key++) Check(router.ResolvePhysicalKey(key) == null, "Controller controls do not claim keyboard suppression or lighting keys");
                source.Send(OutputTarget.A, 0);
                Near(0, Snapshot(router)[10], "Button releases immediately to exact zero");
                source.IsReading = false;
                Check(!router.IsReading && Snapshot(router).Count == 0, "Disconnect removes retained held inputs");
                foreach (var state in router.GetUiSnapshot(500)) Check(!state.Known && state.Stale, "Disconnected controls appear unknown in UI");
                source.IsReading = true;
            }
        Check(!source.Disposed, "Router leaves physical source lifetime with connection owner");
        using (var missing = new LearnedInputRouting(null, profile.LearnedInputs, 385, new[] { new Source(new string('b', 64)) }))
            Check(!missing.IsReading && Snapshot(missing).Count == 0, "Another controller cannot silently replace saved source identity");
        using (var unselected = new LearnedInputRouting(null, GamepadInputProfile.Create(InputMode.PlayStationController, "Unselected").LearnedInputs, 385, new[] { source }))
            Check(!unselected.IsReading && Snapshot(unselected).Count == 0, "Unselected profile remains inert even with a connected gamepad");

        // Existing learned-HID behavior retains integer values. This explicitly
        // guards the conservative scope of the gamepad precision improvement.
        var legacy = new LearnedKeyBinding { KeyIndex = 0, Backend = "hid", SourceDeviceId = source.DeviceId, SourceName = "Legacy HID", ControlId = "LeftXPositive", Kind = 1, Minimum = 0, Maximum = 1, Rest = 0, Active = 1, Direction = 1 };
        source.Send(OutputTarget.LeftXPositive, .123456);
        using (var hid = new LearnedInputRouting(null, new[] { legacy }, 385, new[] { source }))
            Near(Math.Round(.123456 * 385), Snapshot(hid)[0], "Learned-HID pressure precision is unchanged");
        Console.WriteLine("PASS: " + checks + " controller routing assertions.");
    }
}
