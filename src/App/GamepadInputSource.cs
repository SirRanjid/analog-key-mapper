using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    // The identity is the explicitly chosen physical interface, never an XInput
    // player number (which Windows can reassign to one of our virtual outputs).
    public sealed class GamepadInputDevice
    {
        public string DeviceId { get; private set; }
        public string DisplayName { get; private set; }
        public ControllerKind Kind { get; private set; }
        internal string Path;
        internal Guid InterfaceGuid;
        internal CollectionInfo Hid;
        internal bool DualShock4;

        internal GamepadInputDevice(string path, string name, ControllerKind kind, Guid interfaceGuid, CollectionInfo hid, bool dualShock4)
        {
            Path = path; DisplayName = name; Kind = kind; InterfaceGuid = interfaceGuid; Hid = hid; DualShock4 = dualShock4;
            DeviceId = GamepadInputSource.IdentifyDevice(path, kind);
        }
        public override string ToString() { return DisplayName; }
    }

    public sealed class GamepadInputSource : ILearnedInputDeviceSource
    {
        readonly object gate = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly IGamepadInputReader reader;
        readonly Thread worker;
        readonly GamepadInputValues values = new GamepadInputValues();
        readonly InputControlDescriptor[] controls;
        bool disposed, reading;
        string status = "Ready; waiting for controller input.";
        Action<InputControlSample> sample;

        public string DeviceId { get; private set; }
        public string DisplayName { get; private set; }
        public ControllerKind Kind { get; private set; }
        public bool IsReading { get { lock (gate) return reading && !disposed; } }
        public string Status { get { lock (gate) return status; } }
        public InputControlDescriptor[] Controls { get { return (InputControlDescriptor[])controls.Clone(); } }
        public event Action<InputControlSample> Sample
        {
            add { lock (gate) sample += value; }
            remove { lock (gate) sample -= value; }
        }

        public static string IdentifyDevice(string path, ControllerKind kind)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The controller has no device path.", "path");
            string identity = "physical-gamepad-v1\n" + kind + "\n" + path.ToUpperInvariant();
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }

        public static GamepadInputDevice[] Enumerate()
        {
            var result = new List<GamepadInputDevice>();
            Guid hidGuid; Native.HidD_GetHidGuid(out hidGuid);
            foreach (var device in HidInventory.Enumerate())
            {
                bool ds4;
                if (!GamepadInputDecoder.IsSupportedSony(device, out ds4)) continue;
                try
                {
                    GamepadInputNative.VerifyPhysical(device.devicePath, hidGuid);
                    string name = ds4 ? "DualShock 4" : device.productId == 0x0df2 ? "DualSense Edge" : "DualSense";
                    result.Add(new GamepadInputDevice(device.devicePath, name + " · " + ShortIdentity(device.devicePath),
                        ControllerKind.DualSense, hidGuid, device, ds4));
                }
                catch (Exception ex) { if (!GamepadInputNative.ExpectedDeviceError(ex)) throw; }
            }
            foreach (string path in GamepadInputNative.EnumerateXusb())
            {
                try
                {
                    GamepadInputNative.VerifyPhysical(path, GamepadInputNative.XusbGuid);
                    result.Add(new GamepadInputDevice(path, "Xbox controller · " + ShortIdentity(path),
                        ControllerKind.Xbox360, GamepadInputNative.XusbGuid, null, false));
                }
                catch (Exception ex) { if (!GamepadInputNative.ExpectedDeviceError(ex)) throw; }
            }
            result.Sort(delegate(GamepadInputDevice a, GamepadInputDevice b) { return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal); });
            return result.ToArray();
        }

        static string ShortIdentity(string path) { return IdentifyDevice(path, ControllerKind.Xbox360).Substring(0, 6); }

        public static GamepadInputSource Open(GamepadInputDevice device)
        {
            if (device == null) throw new ArgumentNullException("device");
            GamepadInputNative.VerifyPhysical(device.Path, device.InterfaceGuid);
            IGamepadInputReader native = new GamepadInputNative(device);
            try { return new GamepadInputSource(device, native); }
            catch { native.Dispose(); throw; }
        }

        internal GamepadInputSource(GamepadInputDevice device, IGamepadInputReader reader)
        {
            if (device == null || reader == null) throw new ArgumentNullException("device");
            DeviceId = device.DeviceId; DisplayName = device.DisplayName; Kind = device.Kind; this.reader = reader;
            controls = GamepadInputDecoder.CreateControls(Kind);
            reading = true;
            worker = new Thread(ReadLoop) { IsBackground = true, Name = "Physical controller input" };
            try { worker.Start(); }
            catch { reading = false; stop.Dispose(); throw; }
        }

        public bool TryGetValue(string controlId, out double value)
        {
            lock (gate) { value = 0; return !disposed && reading && values.TryGetValue(controlId, out value); }
        }

        public bool TryGetFrame(out ControllerFrame frame)
        {
            lock (gate) { frame = null; return !disposed && reading && values.TryGetFrame(out frame); }
        }

        void ReadLoop()
        {
            var changes = new List<InputControlSample>(24);
            try
            {
                ControllerFrame frame;
                while (reader.Read(stop, out frame))
                {
                    if (frame == null) continue;
                    Action<InputControlSample> handler;
                    lock (gate)
                    {
                        if (disposed) break;
                        changes.Clear(); values.Update(frame, TimestampMilliseconds, changes);
                        status = "Receiving physical controller input."; handler = sample;
                    }
                    if (handler != null) foreach (var change in changes)
                    {
                        if (stop.WaitOne(0)) break;
                        handler(change);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate) if (!disposed) status = "Controller disconnected or unavailable: " + ex.Message;
            }
            finally
            {
                lock (gate) { reading = false; values.Clear(); }
                try { reader.Dispose(); }
                finally { stop.Dispose(); }
            }
        }

        static long TimestampMilliseconds
        {
            get { long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency; return ticks / frequency * 1000 + ticks % frequency * 1000 / frequency; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; reading = false; values.Clear(); sample = null; status = "Controller input closed.";
                try { stop.Set(); } catch (ObjectDisposedException) { }
            }
            // The worker cancels and drains its pending native I/O before freeing
            // memory. A slow driver cannot keep the UI open indefinitely.
            if (Thread.CurrentThread != worker) worker.Join(1500);
        }
    }

    internal interface IGamepadInputReader : IDisposable
    {
        bool Read(WaitHandle stop, out ControllerFrame frame);
    }

    // One complete input frame is published atomically. Unknown/disconnected is
    // distinct from a known neutral frame, including for digital buttons.
    internal sealed class GamepadInputValues
    {
        readonly double[] values = new double[24];
        bool known;
        internal void Clear() { known = false; Array.Clear(values, 0, values.Length); }
        internal bool TryGetValue(string id, out double value)
        {
            value = 0; int index;
            if (!known || !GamepadInputDecoder.TryControlIndex(id, out index)) return false;
            value = values[index]; return true;
        }
        internal void Update(ControllerFrame frame, long timestamp, List<InputControlSample> changes)
        {
            double[] next = GamepadInputDecoder.Values(frame);
            for (int i = 0; i < next.Length; i++)
            {
                if (!known || values[i] != next[i]) changes.Add(new InputControlSample(GamepadInputDecoder.ControlId(i), next[i], timestamp));
                values[i] = next[i];
            }
            known = true;
        }
        internal bool TryGetFrame(out ControllerFrame frame)
        {
            frame = null; if (!known) return false;
            frame = new ControllerFrame { LeftX = values[0] - values[1], LeftY = values[2] - values[3],
                RightX = values[4] - values[5], RightY = values[6] - values[7], LeftTrigger = values[8], RightTrigger = values[9] };
            for (int i = 10; i < values.Length; i++) if (values[i] != 0) frame.Buttons |= GamepadInputDecoder.ButtonMask(i);
            return true;
        }
    }

    // Wire-layout references (protocol facts; no external code dependency):
    // https://gist.github.com/mmozeiko/b8ccc54037a5eaf35432396feabbe435
    // https://github.com/nefarius/XInputHooker/blob/master/XInputHooker/XUSB.h
    // https://github.com/Nemirtingas/OpenXinput/blob/OpenXinput1_4/src/OpenXinput.cpp
    // https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps4.c
    // https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c
    internal static class GamepadInputDecoder
    {
        static readonly ushort[] Buttons = { 0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080, 0x0001, 0x0002, 0x0004, 0x0008 };
        static readonly string[] ControlIds = new string[24];
        static readonly Dictionary<string, int> ControlIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        static GamepadInputDecoder()
        {
            for (int i = 0; i < ControlIds.Length; i++) { ControlIds[i] = ((OutputTarget)i).ToString(); ControlIndices.Add(ControlIds[i], i); }
        }
        internal static string ControlId(int index) { return ControlIds[index]; }
        internal static bool TryControlIndex(string id, out int index) { index = -1; return id != null && ControlIndices.TryGetValue(id, out index); }
        internal static ushort ButtonMask(int index) { return Buttons[index - 10]; }

        internal static bool IsSupportedSony(CollectionInfo device, out bool ds4)
        {
            ds4 = false;
            if (device == null || device.vendorId != 0x054c || device.usagePage != 1 || device.usage != 5 ||
                !string.IsNullOrEmpty(device.error) || string.IsNullOrEmpty(device.devicePath) || device.inputReportLength < 10 || device.inputReportLength > 128) return false;
            ds4 = device.productId == 0x05c4 || device.productId == 0x09cc || device.productId == 0x0ba0;
            return ds4 || device.productId == 0x0ce6 || device.productId == 0x0df2;
        }

        internal static InputControlDescriptor[] CreateControls(ControllerKind kind)
        {
            string[] labels = { "Left stick right", "Left stick left", "Left stick up", "Left stick down", "Right stick right", "Right stick left", "Right stick up", "Right stick down",
                "LT", "RT", "A", "B", "X", "Y", "LB", "RB", "View", "Menu", "Left stick click", "Right stick click", "D-pad up", "D-pad down", "D-pad left", "D-pad right" };
            if (kind == ControllerKind.DualSense)
            {
                labels[8] = "L2"; labels[9] = "R2"; labels[10] = "Cross"; labels[11] = "Circle"; labels[12] = "Square"; labels[13] = "Triangle";
                labels[14] = "L1"; labels[15] = "R1"; labels[16] = "Share / Create"; labels[17] = "Options"; labels[18] = "L3"; labels[19] = "R3";
            }
            var controls = new InputControlDescriptor[24];
            for (int i = 0; i < controls.Length; i++) controls[i] = new InputControlDescriptor(ControlId(i), labels[i],
                i < 10 ? InputControlKind.Absolute : InputControlKind.Button, 0, 1, 0, null, i < 10 ? (double?).08 : null);
            return controls;
        }

        internal static ControllerFrame DecodeXusb(byte[] report)
        {
            if (report == null || report.Length != 29) throw new InvalidDataException("Unsupported Xbox input response.");
            // GET_STATE can succeed for a wireless receiver while its pad is
            // inactive. The status byte, not the I/O result or cached payload,
            // determines whether buttons and axes may still be published.
            if (report[2] != 1) throw new IOException("Xbox controller is disconnected from its receiver.");
            return new ControllerFrame { Buttons = (ushort)(BitConverter.ToUInt16(report, 11) & 0xf3ff), LeftTrigger = report[13] / 255.0, RightTrigger = report[14] / 255.0,
                LeftX = SignedAxis(BitConverter.ToInt16(report, 15)), LeftY = SignedAxis(BitConverter.ToInt16(report, 17)),
                RightX = SignedAxis(BitConverter.ToInt16(report, 19)), RightY = SignedAxis(BitConverter.ToInt16(report, 21)) };
        }

        internal static ControllerFrame DecodeSony(byte[] report, bool ds4)
        {
            if (report == null || report.Length < 10) throw new InvalidDataException("Incomplete PlayStation input report.");
            int axis, buttons, trigger;
            if (ds4)
            {
                if (report[0] == 1 && (report.Length == 10 || report.Length == 64 || report.Length == 78 || report.Length == 128))
                {
                    if (report.Length >= 64 && (report[31] & 4) != 0) throw new IOException("DualShock controller is disconnected from its adapter.");
                    axis = 1;
                }
                else if (report[0] >= 0x11 && report[0] <= 0x19 && report.Length >= 78 && (report[1] & 0x80) != 0)
                {
                    if (!ValidBluetoothCrc(report)) throw new InvalidDataException("Invalid PlayStation Bluetooth checksum.");
                    axis = 3;
                }
                else throw new InvalidDataException("Unsupported DualShock input report.");
                buttons = axis + 4; trigger = axis + 7;
            }
            else if (report[0] == 1 && (report.Length == 10 || report.Length == 78))
            { axis = 1; buttons = 5; trigger = 8; }
            else if (report[0] == 1 && report.Length == 64)
            { axis = 1; buttons = 8; trigger = 5; }
            else if (report[0] == 0x31 && report.Length == 78)
            {
                if (!ValidBluetoothCrc(report)) throw new InvalidDataException("Invalid PlayStation Bluetooth checksum.");
                axis = 2; buttons = 9; trigger = 6;
            }
            else throw new InvalidDataException("Unsupported DualSense input report.");
            int hat = report[buttons] & 15;
            if (hat > 8) throw new InvalidDataException("Invalid controller D-pad direction.");
            ushort mask = 0;
            if ((report[buttons] & 0x10) != 0) mask |= 0x4000;
            if ((report[buttons] & 0x20) != 0) mask |= 0x1000;
            if ((report[buttons] & 0x40) != 0) mask |= 0x2000;
            if ((report[buttons] & 0x80) != 0) mask |= 0x8000;
            byte b = report[buttons + 1];
            if ((b & 1) != 0) mask |= 0x0100;
            if ((b & 2) != 0) mask |= 0x0200;
            if ((b & 0x10) != 0) mask |= 0x0020;
            if ((b & 0x20) != 0) mask |= 0x0010;
            if ((b & 0x40) != 0) mask |= 0x0040;
            if ((b & 0x80) != 0) mask |= 0x0080;
            if (hat == 7 || hat == 0 || hat == 1) mask |= 1;
            if (hat == 3 || hat == 4 || hat == 5) mask |= 2;
            if (hat == 5 || hat == 6 || hat == 7) mask |= 4;
            if (hat == 1 || hat == 2 || hat == 3) mask |= 8;
            return new ControllerFrame { Buttons = mask, LeftX = ByteAxis(report[axis]), LeftY = -ByteAxis(report[axis + 1]),
                RightX = ByteAxis(report[axis + 2]), RightY = -ByteAxis(report[axis + 3]), LeftTrigger = report[trigger] / 255.0, RightTrigger = report[trigger + 1] / 255.0 };
        }

        internal static bool ValidBluetoothCrc(byte[] report)
        {
            if (report == null || report.Length < 78) return false;
            uint crc = CrcByte(0xffffffff, 0xa1);
            for (int i = 0; i < 74; i++) crc = CrcByte(crc, report[i]);
            return ~crc == BitConverter.ToUInt32(report, 74);
        }
        static uint CrcByte(uint crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            return crc;
        }
        static double SignedAxis(short value) { return value < 0 ? value / 32768.0 : value / 32767.0; }
        static double ByteAxis(byte value) { return value < 128 ? (value - 128) / 128.0 : (value - 128) / 127.0; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static double[] Values(ControllerFrame frame)
        {
            if (frame == null || !Finite(frame.LeftX) || !Finite(frame.LeftY) || !Finite(frame.RightX) || !Finite(frame.RightY) ||
                !Finite(frame.LeftTrigger) || !Finite(frame.RightTrigger) || Math.Abs(frame.LeftX) > 1 || Math.Abs(frame.LeftY) > 1 ||
                Math.Abs(frame.RightX) > 1 || Math.Abs(frame.RightY) > 1 || frame.LeftTrigger < 0 || frame.LeftTrigger > 1 || frame.RightTrigger < 0 || frame.RightTrigger > 1)
                throw new InvalidDataException("Controller frame is outside its declared range.");
            var result = new double[24];
            result[0] = Math.Max(0, frame.LeftX); result[1] = Math.Max(0, -frame.LeftX);
            result[2] = Math.Max(0, frame.LeftY); result[3] = Math.Max(0, -frame.LeftY);
            result[4] = Math.Max(0, frame.RightX); result[5] = Math.Max(0, -frame.RightX);
            result[6] = Math.Max(0, frame.RightY); result[7] = Math.Max(0, -frame.RightY);
            result[8] = frame.LeftTrigger; result[9] = frame.RightTrigger;
            for (int i = 10; i < result.Length; i++) result[i] = (frame.Buttons & ButtonMask(i)) != 0 ? 1 : 0;
            return result;
        }
    }

    internal sealed class GamepadInputNative : IGamepadInputReader
    {
        internal static readonly Guid XusbGuid = new Guid("ec87f1e3-c13b-4100-b5f7-8b84d54260cb");
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref Native.InterfaceData data);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint node, StringBuilder buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_DevNode_Registry_Property(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, IntPtr returned, IntPtr overlapped);

        readonly GamepadInputDevice device;
        readonly int reportLength;
        readonly IntPtr[] waitHandles = new IntPtr[2];
        SafeFileHandle handle;
        IntPtr report, request, overlapped, ready;
        bool pending, haveRead, periodicSonyReports;

        internal GamepadInputNative(GamepadInputDevice device)
        {
            this.device = device;
            reportLength = device.Kind == ControllerKind.Xbox360 ? 29 : device.Hid.inputReportLength;
            try
            {
                // XUSB GET_STATE has FILE_READ_ACCESS | FILE_WRITE_ACCESS in its
                // control-code ABI, hence this handle's required access mask.
                // Only that input query is issued; no SET_STATE/output commands.
                uint access = device.Kind == ControllerKind.Xbox360 ? 0xc0000000u : 0x80000000u;
                handle = Native.CreateFile(device.Path, access, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw Native.Error("Open selected physical controller");
                VerifyPhysical(device.Path, device.InterfaceGuid);
                if (device.Hid != null) VerifyHidMetadata();
                ready = Native.CreateEvent(IntPtr.Zero, true, false, null);
                if (ready == IntPtr.Zero) throw Native.Error("Create controller input event");
                report = Marshal.AllocHGlobal(reportLength); overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
                if (device.Kind == ControllerKind.Xbox360)
                {
                    request = Marshal.AllocHGlobal(3); Marshal.Copy(new byte[] { 1, 1, 0 }, 0, request, 3);
                }
            }
            catch { Dispose(); throw; }
        }

        void VerifyHidMetadata()
        {
            var attributes = new Native.Attributes { Size = Marshal.SizeOf(typeof(Native.Attributes)) };
            if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != device.Hid.vendorId ||
                attributes.ProductId != device.Hid.productId || attributes.VersionNumber != device.Hid.version)
                throw new InvalidDataException("The selected controller changed; select it again.");
            IntPtr preparsed;
            if (!Native.HidD_GetPreparsedData(handle, out preparsed)) throw Native.Error("Read controller metadata");
            try
            {
                Native.Caps caps;
                if (Native.HidP_GetCaps(preparsed, out caps) != Native.HidSuccess || caps.InputLength != reportLength || caps.UsagePage != 1 || caps.Usage != 5)
                    throw new InvalidDataException("The selected controller descriptor changed.");
            }
            finally { Native.HidD_FreePreparsedData(preparsed); }
        }

        public bool Read(WaitHandle stop, out ControllerFrame frame)
        {
            frame = null;
            if (stop.WaitOne(device.Kind == ControllerKind.Xbox360 && haveRead ? 4 : 0)) return false;
            Native.ResetEvent(ready);
            Marshal.StructureToPtr(new Native.Overlapped { Event = ready }, overlapped, false);
            bool completed = device.Kind == ControllerKind.Xbox360
                ? DeviceIoControl(handle, 0x8000e00c, request, 3, report, (uint)reportLength, IntPtr.Zero, overlapped)
                : Native.ReadFile(handle, report, (uint)reportLength, IntPtr.Zero, overlapped);
            int error = Marshal.GetLastWin32Error();
            if (!completed && error != 997) throw new Win32Exception(error, "Read selected controller input");
            pending = true;
            waitHandles[0] = stop.SafeWaitHandle.DangerousGetHandle(); waitHandles[1] = ready;
            uint wait;
            do
            {
                wait = WaitForMultipleObjects(2, waitHandles, false, 2000);
                if (wait != 258) break;
                // Enhanced Bluetooth reports are a stream, as is a pending Xbox
                // GET_STATE query. A stalled transport must not latch held input.
                if (device.Kind == ControllerKind.Xbox360 || periodicSonyReports)
                    throw new IOException("Controller input timed out; reconnect the controller.");
                // Basic HID reports may be sent only on a change. Preserve an
                // ordinary held button while checking that the interface is
                // still active; never infer release from mere event silence.
                VerifyPhysical(device.Path, device.InterfaceGuid);
            } while (true);
            if (wait == 0) return false;
            if (wait != 1) throw Native.Error("Wait for controller input");
            uint count;
            bool success = Native.GetOverlappedResult(handle, overlapped, out count, false); pending = false;
            if (!success) throw Native.Error("Controller input disconnected");
            if (count < 1 || count > reportLength) throw new InvalidDataException("Invalid controller report length.");
            var bytes = new byte[(int)count]; Marshal.Copy(report, bytes, 0, bytes.Length);
            frame = device.Kind == ControllerKind.Xbox360 ? GamepadInputDecoder.DecodeXusb(bytes) : GamepadInputDecoder.DecodeSony(bytes, device.DualShock4);
            periodicSonyReports = device.Kind == ControllerKind.DualSense && (bytes[0] == 0x31 || bytes[0] >= 0x11 && bytes[0] <= 0x19);
            haveRead = true; return true;
        }

        public void Dispose()
        {
            if (pending && handle != null && !handle.IsInvalid)
            {
                Native.CancelIoEx(handle, overlapped); uint ignored;
                Native.GetOverlappedResult(handle, overlapped, out ignored, true); pending = false;
            }
            if (handle != null) { handle.Dispose(); handle = null; }
            if (report != IntPtr.Zero) { Marshal.FreeHGlobal(report); report = IntPtr.Zero; }
            if (request != IntPtr.Zero) { Marshal.FreeHGlobal(request); request = IntPtr.Zero; }
            if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
            if (ready != IntPtr.Zero) { Native.CloseHandle(ready); ready = IntPtr.Zero; }
        }

        internal static bool ExpectedDeviceError(Exception ex) { return ex is Win32Exception || ex is IOException || ex is NotSupportedException; }
        internal static bool IsRejectedAncestor(string instanceId, string service)
        {
            string id = (instanceId ?? "").ToUpperInvariant(), driver = (service ?? "").ToUpperInvariant();
            // Physical USB/Bluetooth is attached through hardware buses. Reject
            // arbitrary root-enumerated software buses, including unknown virtual
            // pads advertising a real-looking USB VID/PID farther down the tree.
            bool softwareRoot = id.StartsWith("ROOT\\", StringComparison.Ordinal) &&
                !id.StartsWith("ROOT\\ACPI_HAL\\", StringComparison.Ordinal) && !id.StartsWith("ROOT\\PCI\\", StringComparison.Ordinal);
            return softwareRoot ||
                id.StartsWith("SWD\\", StringComparison.Ordinal) || driver == "VIGEMBUS" || driver == "USBIP2_UDE" ||
                driver == "USBIP_VHCI" || driver == "USBIP2_VHCI" || driver == "HIDMAESTRO" || driver == "VJOY";
        }

        internal static void VerifyPhysical(string path, Guid guid)
        {
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw Native.Error("Inspect physical controller");
            IntPtr detail = IntPtr.Zero, info = IntPtr.Zero;
            try
            {
                var item = new Native.InterfaceData { Size = Marshal.SizeOf(typeof(Native.InterfaceData)) };
                if (!SetupDiOpenDeviceInterface(set, path, 0, ref item)) throw Native.Error("Find selected physical controller");
                // OpenDeviceInterface may add a remembered interface to the set.
                // SPINT_ACTIVE is needed in addition to DIGCF_PRESENT above.
                if ((item.Flags & 1) == 0 || (item.Flags & 4) != 0) throw new IOException("The selected controller interface is disconnected.");
                uint needed; Native.SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                if (needed < 6 || needed > 65536) throw new InvalidDataException("Invalid controller interface metadata.");
                detail = Marshal.AllocHGlobal((int)needed); Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                var data = new DeviceInfoData { Size = Marshal.SizeOf(typeof(DeviceInfoData)) };
                info = Marshal.AllocHGlobal(data.Size); Marshal.StructureToPtr(data, info, false);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref item, detail, needed, out needed, info)) throw Native.Error("Read controller ancestry");
                uint node = ((DeviceInfoData)Marshal.PtrToStructure(info, typeof(DeviceInfoData))).DevInst;
                bool physical = false;
                for (int depth = 0; depth < 32; depth++)
                {
                    var id = new StringBuilder(1024);
                    if (CM_Get_Device_ID(node, id, (uint)id.Capacity, 0) != 0) throw new InvalidDataException("Cannot verify controller ancestry.");
                    byte[] buffer = new byte[1024]; uint length = (uint)buffer.Length, type;
                    uint result = CM_Get_DevNode_Registry_Property(node, 5, out type, buffer, ref length, 0);
                    string service = result == 0 && type == 1 && length <= buffer.Length ? Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0') : null;
                    string instance = id.ToString();
                    if (IsRejectedAncestor(instance, service)) throw new NotSupportedException("Virtual controller outputs cannot be used as physical inputs.");
                    if (instance.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                        instance.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase)) physical = true;
                    if (instance.StartsWith("HTREE\\ROOT\\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (physical) return;
                        throw new NotSupportedException("This controller has no verified physical USB or Bluetooth connection.");
                    }
                    uint parent;
                    if (CM_Get_Parent(out parent, node, 0) != 0) throw new InvalidDataException("The controller disconnected while checking its identity.");
                    node = parent;
                }
                throw new InvalidDataException("Controller ancestry is too deep.");
            }
            finally
            {
                if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail);
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                Native.SetupDiDestroyDeviceInfoList(set);
            }
        }

        internal static string[] EnumerateXusb()
        {
            Guid guid = XusbGuid;
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw Native.Error("Find Xbox input interfaces");
            var paths = new List<string>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var item = new Native.InterfaceData { Size = Marshal.SizeOf(typeof(Native.InterfaceData)) };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item))
                    {
                        if (Marshal.GetLastWin32Error() == 259) break;
                        throw Native.Error("Read Xbox input interface");
                    }
                    uint needed; Native.SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed < 6 || needed > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref item, detail, needed, out needed, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return paths.ToArray();
        }
    }
}
