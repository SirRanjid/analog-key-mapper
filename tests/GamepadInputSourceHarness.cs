using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

public static class GamepadInputSourceHarness
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < .000001, message); }
    static void I16(byte[] data, int offset, short value) { Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 2); }
    static void ExpectInvalid(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }
    static byte[] Sony(bool ds4, bool bluetooth, bool simple)
    {
        var data = new byte[simple ? 10 : bluetooth ? 78 : 64];
        data[0] = (byte)(bluetooth && !simple ? ds4 ? 0x11 : 0x31 : 1);
        int axis = bluetooth && !simple ? ds4 ? 3 : 2 : 1;
        for (int i = 0; i < 4; i++) data[axis + i] = 128;
        if (bluetooth && ds4 && !simple) data[1] = 0x80;
        data[axis + (ds4 || simple ? 4 : 7)] = 8;
        if (bluetooth && !simple) WriteCrc(data);
        return data;
    }
    static void WriteCrc(byte[] data)
    {
        // Independent straightforward CRC fixture builder, not production helper.
        uint crc = 0xffffffff;
        for (int i = -1; i < 74; i++)
        {
            crc ^= i == -1 ? (byte)0xa1 : data[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }
        Array.Copy(BitConverter.GetBytes(~crc), 0, data, 74, 4);
    }

    sealed class FakeReader : IGamepadInputReader
    {
        internal readonly AutoResetEvent Advance = new AutoResetEvent(false);
        internal bool Fail;
        internal ControllerFrame Frame = new ControllerFrame { Buttons = 0x1000, LeftX = -.5, RightTrigger = 1 };
        internal readonly ManualResetEvent Closed = new ManualResetEvent(false);
        public bool Read(WaitHandle stop, out ControllerFrame frame)
        {
            frame = null;
            if (WaitHandle.WaitAny(new[] { stop, Advance }) == 0) return false;
            if (Fail) throw new IOException("Synthetic disconnect.");
            frame = Frame; return true;
        }
        public void Dispose() { Closed.Set(); }
    }
    static bool Await(Func<bool> ready)
    {
        for (int i = 0; i < 200; i++) { if (ready()) return true; Thread.Sleep(5); }
        return ready();
    }

    public static string Run()
    {
        foreach (ControllerKind kind in new[] { ControllerKind.Xbox360, ControllerKind.DualSense })
        {
            InputControlDescriptor[] controls = GamepadInputDecoder.CreateControls(kind);
            Check(controls.Length == 24, "Every remappable input has a descriptor.");
            for (int i = 0; i < controls.Length; i++)
            {
                Check(controls[i].ControlId == ((OutputTarget)i).ToString(), "Stable named control identity " + i);
                Check(controls[i].Kind == (i < 10 ? InputControlKind.Absolute : InputControlKind.Button) && controls[i].LogicalMinimum == 0 && controls[i].LogicalMaximum == 1 && controls[i].Neutral == 0,
                    "Normalized unipolar descriptor " + i);
            }
        }
        var xbox = new byte[29]; xbox[0] = 1; xbox[1] = 1; xbox[2] = 1;
        I16(xbox, 11, unchecked((short)0xf7ff)); xbox[13] = 64; xbox[14] = 255;
        I16(xbox, 15, short.MinValue); I16(xbox, 17, short.MaxValue); I16(xbox, 19, 0); I16(xbox, 21, -16384);
        var frame = GamepadInputDecoder.DecodeXusb(xbox);
        Near(frame.LeftX, -1, "Xbox negative stick reaches exactly -1."); Near(frame.LeftY, 1, "Xbox positive stick reaches exactly 1.");
        Near(frame.RightY, -.5, "Xbox signed axis preserves precision."); Near(frame.LeftTrigger, 64 / 255.0, "Independent analog trigger.");
        Check(frame.Buttons == 0xf3ff, "Only the 14 mapped Xbox buttons are retained.");
        var normalized = GamepadInputDecoder.Values(frame);
        Near(normalized[0], 0, "Opposite stick direction stays zero."); Near(normalized[1], 1, "Negative stick becomes unipolar control.");
        Near(normalized[2], 1, "Up retains native Xbox positive Y."); Near(normalized[7], .5, "Down retains analog magnitude.");
        for (int i = 10; i < 24; i++) Near(normalized[i], 1, "Xbox button mask " + i);
        ExpectInvalid(delegate { GamepadInputDecoder.DecodeXusb(new byte[28]); }, "Truncated Xbox reply rejected.");
        ExpectInvalid(delegate { GamepadInputDecoder.DecodeXusb(new byte[30]); }, "Unexpected Xbox reply ABI rejected.");
        foreach (byte inactiveStatus in new byte[] { 0, 2, 3, 255 })
        {
            xbox[2] = inactiveStatus; bool rejected = false;
            try { GamepadInputDecoder.DecodeXusb(xbox); } catch (IOException) { rejected = true; }
            Check(rejected, "Successful Xbox reply with inactive status cannot latch cached held buttons: " + inactiveStatus);
        }
        xbox[2] = 1;

        foreach (bool ds4 in new[] { true, false }) foreach (bool bluetooth in new[] { false, true }) foreach (bool simple in new[] { false, true })
        {
            byte[] sony = Sony(ds4, bluetooth, simple);
            frame = GamepadInputDecoder.DecodeSony(sony, ds4);
            Check(frame.Buttons == 0 && frame.LeftX == 0 && frame.LeftY == 0 && frame.RightX == 0 && frame.RightY == 0 && frame.LeftTrigger == 0 && frame.RightTrigger == 0,
                "Sony neutral report " + ds4 + "/" + bluetooth + "/" + simple);
            int axis = bluetooth && !simple ? ds4 ? 3 : 2 : 1;
            int buttons = axis + (ds4 || simple ? 4 : 7), triggers = axis + (ds4 || simple ? 7 : 4);
            sony[axis] = 255; sony[axis + 1] = 0; sony[axis + 2] = 0; sony[axis + 3] = 255;
            sony[buttons] = 0xf1; sony[buttons + 1] = 0xf3; sony[triggers] = 255; sony[triggers + 1] = 63;
            if (bluetooth && !simple) WriteCrc(sony);
            frame = GamepadInputDecoder.DecodeSony(sony, ds4);
            Near(frame.LeftX, 1, "Sony right reaches 1."); Near(frame.LeftY, 1, "Sony Y polarity corrected to up-positive.");
            Near(frame.RightX, -1, "Sony left reaches -1."); Near(frame.RightY, -1, "Sony down reaches -1.");
            Near(frame.LeftTrigger, 1, "Sony L2 analog value."); Near(frame.RightTrigger, 63 / 255.0, "Sony R2 remains independent.");
            Check(frame.Buttons == 0xf3f9, "Sony face, shoulder, menu, sticks and diagonal D-pad mapped.");
            if (bluetooth && !simple)
            {
                sony[10] ^= 1;
                ExpectInvalid(delegate { GamepadInputDecoder.DecodeSony(sony, ds4); }, "Bluetooth corruption rejected.");
            }
        }
        byte[] paddedSimple = new byte[78]; Array.Copy(Sony(false, false, true), paddedSimple, 10);
        Check(GamepadInputDecoder.DecodeSony(paddedSimple, false).Buttons == 0, "Windows padded DualSense simple report decoded as simple.");
        byte[] dpad = Sony(false, false, false);
        ushort[] expectedHats = { 1, 9, 8, 10, 2, 6, 4, 5, 0 };
        for (int i = 0; i < 9; i++) { dpad[8] = (byte)i; Check(GamepadInputDecoder.DecodeSony(dpad, false).Buttons == expectedHats[i], "D-pad direction " + i); }
        dpad[8] = 9; ExpectInvalid(delegate { GamepadInputDecoder.DecodeSony(dpad, false); }, "Malformed D-pad rejected.");
        ExpectInvalid(delegate { GamepadInputDecoder.DecodeSony(new byte[9], false); }, "Truncated Sony report rejected.");
        ExpectInvalid(delegate { GamepadInputDecoder.DecodeSony(new byte[64], false); }, "Unknown Sony report id rejected.");
        byte[] dongle = Sony(true, false, false); dongle[31] = 4;
        bool disconnected = false; try { GamepadInputDecoder.DecodeSony(dongle, true); } catch (IOException) { disconnected = true; }
        Check(disconnected, "Disconnected official DS4 adapter invalidates input.");

        var values = new GamepadInputValues(); var changes = new List<InputControlSample>(); double value;
        Check(!values.TryGetValue("A", out value), "Unseen controller buttons stay unknown.");
        frame = new ControllerFrame { LeftX = -.5, LeftTrigger = .25, Buttons = 0x1000 };
        values.Update(frame, 100, changes); Check(changes.Count == 24 && changes[0].TimestampMilliseconds == 100, "Initial complete frame is published.");
        changes.Clear(); values.Update(frame, 101, changes); Check(changes.Count == 0, "Unchanged input does not emit samples.");
        frame.LeftX = .5; frame.Buttons = 0; values.Update(frame, 102, changes);
        Check(changes.Count == 3 && values.TryGetValue("LeftXNegative", out value) && value == 0, "Direction reversal releases former half-axis and button.");
        ControllerFrame snapshot; Check(values.TryGetFrame(out snapshot) && snapshot.LeftX == .5, "Complete frame can be read for preview.");
        snapshot.LeftX = -1; Check(values.TryGetFrame(out snapshot) && snapshot.LeftX == .5, "Caller cannot mutate retained state.");
        frame.LeftX = double.NaN; ExpectInvalid(delegate { values.Update(frame, 103, changes); }, "Nonfinite frame rejected before partial publication.");
        Check(values.TryGetValue("LeftXPositive", out value) && value == .5, "Invalid frame does not partially change retained state.");
        values.Clear(); Check(!values.TryGetValue("A", out value) && !values.TryGetFrame(out snapshot), "Disconnect invalidates digital and analog snapshots.");
        Check(!values.TryGetValue("10", out value), "Control ids never accept numeric enum aliases.");

        string id = GamepadInputSource.IdentifyDevice("device-one", ControllerKind.Xbox360);
        Check(id.Length == 64 && id == GamepadInputSource.IdentifyDevice("DEVICE-ONE", ControllerKind.Xbox360), "Case-insensitive stable SHA256 identity.");
        Check(id != GamepadInputSource.IdentifyDevice("device-two", ControllerKind.Xbox360), "Identical models at different ports remain distinct.");
        Check(id != GamepadInputSource.IdentifyDevice("device-one", ControllerKind.DualSense), "Different backend layouts never share identity.");
        Check(GamepadInputNative.IsRejectedAncestor("ROOT\\USBIP_WIN2\\UDE", null), "Own USB/IP bus cannot become an input.");
        Check(GamepadInputNative.IsRejectedAncestor("ROOT\\VIGEMBUS\\0000", null), "ViGEm bus cannot become an input.");
        Check(GamepadInputNative.IsRejectedAncestor("ROOT\\UNKNOWNVIRTUALBUS\\0000", null), "Unknown root-enumerated virtual bus cannot disguise itself as physical USB.");
        Check(GamepadInputNative.IsRejectedAncestor("SWD\\GAMEPAD\\0000", null), "Software-enumerated input cannot become a physical controller.");
        Check(!GamepadInputNative.IsRejectedAncestor("ROOT\\ACPI_HAL\\0000", null), "Hardware ACPI root remains allowed.");
        Check(!GamepadInputNative.IsRejectedAncestor("ROOT\\PCI\\0000", null), "Legacy hardware PCI root remains allowed.");
        Check(GamepadInputNative.IsRejectedAncestor("USB\\VID_045E&PID_028E", "usbip2_ude"), "Virtual device service is checked independently of real-looking VID/PID.");
        Check(!GamepadInputNative.IsRejectedAncestor("USB\\VID_054C&PID_0CE6\\real", "HidUsb"), "Real Sony VID/PID remains eligible.");
        bool isDs4;
        var sonyDevice = new CollectionInfo { vendorId = 0x054c, productId = 0x0ce6, usagePage = 1, usage = 5, inputReportLength = 64, devicePath = "controller" };
        Check(GamepadInputDecoder.IsSupportedSony(sonyDevice, out isDs4) && !isDs4, "Official DualSense admitted.");
        sonyDevice.productId = 0x09cc; Check(GamepadInputDecoder.IsSupportedSony(sonyDevice, out isDs4) && isDs4, "Official DS4 v2 admitted.");
        sonyDevice.productId = 123; Check(!GamepadInputDecoder.IsSupportedSony(sonyDevice, out isDs4), "Unknown Sony layout never guessed.");
        sonyDevice.productId = 0x0ce6; sonyDevice.usage = 6; Check(!GamepadInputDecoder.IsSupportedSony(sonyDevice, out isDs4), "Non-gamepad collection excluded.");

        var device = new GamepadInputDevice("synthetic", "Synthetic Xbox", ControllerKind.Xbox360, Guid.Empty, null, false);
        var fake = new FakeReader(); var source = new GamepadInputSource(device, fake);
        Check(source.IsReading && !source.TryGetValue("A", out value), "Source starts reading without inventing initial state.");
        fake.Advance.Set(); Check(Await(delegate { double v; return source.TryGetValue("A", out v) && v == 1; }), "Background reader publishes input.");
        fake.Fail = true; fake.Advance.Set(); Check(fake.Closed.WaitOne(1000) && !source.IsReading && !source.TryGetValue("A", out value), "Disconnect disposes native reader and invalidates held buttons.");
        Check(source.Status.Contains("Synthetic disconnect"), "Disconnect status preserves diagnostic."); source.Dispose();
        fake = new FakeReader(); source = new GamepadInputSource(device, fake); source.Dispose();
        Check(fake.Closed.WaitOne(1000) && !source.IsReading && !source.TryGetFrame(out snapshot), "Disposal wakes pending read and clears frame.");
        return "PASS GamepadInputSource (" + checks + " checks; synthetic reports only, no hardware opened)";
    }
}
