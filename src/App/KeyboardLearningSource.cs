using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    // One process-wide passive Raw Input registration; each subscriber is
    // restricted to the exact explicitly selected physical device handle.
    public sealed class KeyboardLearningSource : ILearnedInputDeviceSource
    {
        readonly object gate = new object();
        readonly Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly HashSet<string> controlsById = new HashSet<string>(StringComparer.Ordinal);
        readonly InputControlDescriptor[] controls;
        readonly IntPtr deviceHandle;
        KeyboardLearningBroker broker;
        Action<InputControlSample> sample;
        bool disposed, reading;
        string status = "Ready; standard keyboard keys are recognized automatically.";

        public string DeviceId { get; private set; }
        public string DisplayName { get; private set; }
        public bool IsReading { get { lock (gate) return reading && !disposed; } }
        public string Status { get { lock (gate) return status; } }
        public InputControlDescriptor[] Controls { get { return (InputControlDescriptor[])controls.Clone(); } }
        internal IntPtr DeviceHandle { get { return deviceHandle; } }
        public event Action<InputControlSample> Sample
        {
            add { lock (gate) sample += value; }
            remove { lock (gate) sample -= value; }
        }

        public static bool IsEligible(CollectionInfo device)
        {
            return device != null && string.IsNullOrEmpty(device.error) && !string.IsNullOrEmpty(device.devicePath) && device.usagePage == 1 && device.usage == 6;
        }

        public static string IdentifyDevice(CollectionInfo device)
        {
            if (!IsEligible(device)) throw new ArgumentException("Select a standard keyboard collection.", "device");
            string identity = "raw-keyboard-input-v1\n" + ProtocolFingerprint.Calculate(device, "raw-keyboard-input-v1") + "\n" + device.devicePath.ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }

        public static KeyboardLearningSource Open(CollectionInfo device)
        {
            if (!IsEligible(device)) throw new NotSupportedException("Select a standard keyboard collection.");
            HidLearningNativeSession.RejectVirtualOutput(device.devicePath);
            return new KeyboardLearningSource(device, KeyboardLearningBroker.FindDevice(device.devicePath));
        }

        public static bool TryParseControlId(string controlId, out SuppressionKey key)
        {
            int scan; bool extended;
            if (KeyboardLearningControls.TryParseControlId(controlId, out scan, out extended))
            { key = new SuppressionKey(scan, extended); return true; }
            key = new SuppressionKey(); return false;
        }

        KeyboardLearningSource(CollectionInfo device, IntPtr rawDevice)
        {
            deviceHandle = rawDevice; DeviceId = IdentifyDevice(device);
            DisplayName = string.IsNullOrWhiteSpace(device.product) ? "Keyboard" : device.product.Trim();
            controls = CreateControls();
            foreach (InputControlDescriptor control in controls) controlsById.Add(control.ControlId);
            reading = true;
            try
            {
                broker = KeyboardLearningBroker.Acquire(this);
                // Close the removal race between enumeration and registration.
                if (KeyboardLearningBroker.FindDevice(device.devicePath) != deviceHandle) throw new IOException("The selected keyboard changed during connection.");
            }
            catch { Dispose(); throw; }
        }

        static InputControlDescriptor[] CreateControls()
        {
            var known = new Dictionary<string, KeyboardKeyDefinition>(StringComparer.Ordinal);
            foreach (KeyboardKeyDefinition key in KeyboardLayout.Tk75Iso().Keys)
            {
                SuppressionKey scan;
                if (key.KeyIndex.HasValue && !key.IsKnob && KeyboardScanCodes.TryGet(key.Code, out scan))
                    known[KeyboardLearningControls.ControlId(scan.ScanCode, scan.Extended)] = key;
            }
            var result = new List<InputControlDescriptor>();
            for (int extended = 0; extended < 2; extended++)
                for (int scan = 1; scan <= 127; scan++)
                {
                    if (extended == 1 && (scan == 0x2a || scan == 0x36)) continue;
                    string id = KeyboardLearningControls.ControlId(scan, extended != 0);
                    KeyboardKeyDefinition key;
                    bool identified = known.TryGetValue(id, out key);
                    string label = identified ? key.GetLegend(KeyboardLegendStyle.Qwerty) : "Key " + scan.ToString("X2", CultureInfo.InvariantCulture) + (extended != 0 ? " E0" : "");
                    if (identified && key.Code.EndsWith("Left", StringComparison.Ordinal)) label += " L";
                    else if (identified && key.Code.EndsWith("Right", StringComparison.Ordinal)) label += " R";
                    result.Add(new InputControlDescriptor(id, label, InputControlKind.Button, 0, 1, 0, identified ? key.KeyIndex : null));
                }
            return result.ToArray();
        }

        public bool TryGetValue(string controlId, out double value)
        {
            lock (gate)
            {
                value = 0;
                return !disposed && reading && controlId != null && values.TryGetValue(controlId, out value);
            }
        }

        internal void Feed(ushort makeCode, ushort flags, ushort virtualKey)
        {
            if (makeCode == 255) { Disconnect("Keyboard input overflowed; reconnect this input source."); return; }
            string id; bool down;
            if (!KeyboardLearningControls.TryDecode(makeCode, flags, virtualKey, out id, out down)) return;
            Action<InputControlSample> handler;
            double next = down ? 1 : 0, previous;
            lock (gate)
            {
                if (disposed || !reading || !controlsById.Contains(id)) return;
                if (values.TryGetValue(id, out previous) && previous == next) return; // Do not turn key repeat into repeated presses.
                values[id] = next; handler = sample;
            }
            if (handler != null)
                try { handler(new InputControlSample(id, next, HidLearningSource.TimestampMilliseconds)); }
                catch (Exception) { Disconnect("The keyboard input subscriber stopped unexpectedly."); }
        }

        internal void Disconnect(string reason)
        {
            lock (gate) { if (disposed) return; reading = false; values.Clear(); status = reason; }
        }

        public void Dispose()
        {
            KeyboardLearningBroker previous;
            lock (gate)
            {
                if (disposed) return;
                disposed = true; reading = false; values.Clear(); sample = null; status = "Keyboard input closed.";
                previous = broker; broker = null;
            }
            if (previous != null) KeyboardLearningBroker.Release(previous, this);
        }
    }

    internal sealed class KeyboardLearningBroker
    {
        static readonly object sharedGate = new object();
        static KeyboardLearningBroker shared;
        volatile KeyboardLearningSource[] sources = new KeyboardLearningSource[0];
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread worker;
        volatile bool stopping;
        Exception startupError;
        NativeKeyboardWindow window;
        IntPtr windowHandle;

        [StructLayout(LayoutKind.Sequential)] struct RawDevice { internal IntPtr Device; internal uint Type; }
        [StructLayout(LayoutKind.Sequential)] struct Registration { internal ushort UsagePage, Usage; internal uint Flags; internal IntPtr Target; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList(IntPtr devices, ref uint count, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder buffer, ref uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRegisteredRawInputDevices([Out] Registration[] devices, ref uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        KeyboardLearningBroker()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "Selected keyboard input" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        internal static KeyboardLearningBroker Acquire(KeyboardLearningSource source)
        {
            lock (sharedGate)
            {
                if (shared != null && shared.stopping)
                {
                    if (Thread.CurrentThread != shared.worker) shared.worker.Join(1500);
                    if (shared.worker.IsAlive) throw new InvalidOperationException("The previous keyboard input is still closing.");
                    shared.ready.Dispose();
                    shared = null;
                }
                if (shared == null) shared = new KeyboardLearningBroker();
                KeyboardLearningBroker value = shared;
                if (!value.ready.WaitOne(2500)) { value.Stop(); throw new TimeoutException("Keyboard input did not start in time."); }
                if (value.startupError != null) { value.Stop(); throw new InvalidOperationException("Cannot start keyboard input.", value.startupError); }
                if (value.stopping) throw new InvalidOperationException("Keyboard input is unavailable.");
                var updated = new KeyboardLearningSource[value.sources.Length + 1];
                Array.Copy(value.sources, updated, value.sources.Length); updated[updated.Length - 1] = source; value.sources = updated;
                return value;
            }
        }

        internal static void Release(KeyboardLearningBroker owner, KeyboardLearningSource source)
        {
            lock (sharedGate)
            {
                var updated = new List<KeyboardLearningSource>(owner.sources); updated.Remove(source); owner.sources = updated.ToArray();
                if (owner.sources.Length != 0) return;
                owner.Stop();
                // The worker never locks sharedGate, so old registration removal
                // completes before a new broker can register the same class.
                if (Thread.CurrentThread != owner.worker) owner.worker.Join(1500);
                if (!owner.worker.IsAlive && ReferenceEquals(shared, owner)) { owner.ready.Dispose(); shared = null; }
            }
        }

        void Stop()
        {
            stopping = true;
            IntPtr handle = windowHandle;
            if (handle != IntPtr.Zero) PostMessage(handle, 0x8012, IntPtr.Zero, IntPtr.Zero);
        }

        void Run()
        {
            try
            {
                window = new NativeKeyboardWindow(this);
                windowHandle = window.Handle;
                IntPtr existing;
                if (FindKeyboardRegistration(out existing)) throw new InvalidOperationException("Another component already owns raw keyboard input in this process.");
                Register(windowHandle, 0x100 | 0x2000); // INPUTSINK + DEVNOTIFY; no NOLEGACY, suppression or exclusive access.
                ready.Set();
                if (!stopping) Application.Run();
            }
            catch (Exception ex)
            {
                startupError = ex;
                foreach (KeyboardLearningSource source in sources) source.Disconnect("Keyboard input stopped: " + ex.Message);
            }
            finally
            {
                stopping = true;
                try
                {
                    // Never unregister another component's later registration.
                    IntPtr existing;
                    if (windowHandle != IntPtr.Zero && FindKeyboardRegistration(out existing) && existing == windowHandle) Register(IntPtr.Zero, 1);
                }
                catch (Exception) { }
                if (window != null) window.Dispose();
                windowHandle = IntPtr.Zero;
                foreach (KeyboardLearningSource source in sources) source.Disconnect("Keyboard input closed.");
                ready.Set();
                // ready is only disposed after the finished thread has no waiter.
            }
        }

        static void Register(IntPtr target, uint flags)
        {
            if (!RegisterRawInputDevices(new[] { new Registration { UsagePage = 1, Usage = 6, Flags = flags, Target = target } }, 1, (uint)Marshal.SizeOf(typeof(Registration))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Register passive keyboard input");
        }

        static bool FindKeyboardRegistration(out IntPtr target)
        {
            target = IntPtr.Zero;
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(Registration));
            uint first = GetRegisteredRawInputDevices(null, ref count, size);
            int error = Marshal.GetLastWin32Error();
            // Unlike GetRawInputDeviceList, this API documents -1 / error 122
            // for its successful NULL-buffer sizing query.
            if (first == uint.MaxValue && error != 122 || count > 1024) throw new Win32Exception(error, "Read raw input registrations");
            if (count == 0) return false;
            var registrations = new Registration[count];
            uint result = GetRegisteredRawInputDevices(registrations, ref count, size);
            if (result == uint.MaxValue || result > registrations.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Read raw input registrations");
            for (int i = 0; i < result; i++)
                if (registrations[i].UsagePage == 1 && registrations[i].Usage == 6) { target = registrations[i].Target; return true; }
            return false;
        }

        internal static IntPtr FindDevice(string path)
        {
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(RawDevice));
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, size) == uint.MaxValue || count > 4096) throw new Win32Exception(Marshal.GetLastWin32Error(), "Enumerate keyboard inputs");
            if (count == 0) throw new IOException("The selected keyboard is no longer available.");
            IntPtr memory = Marshal.AllocHGlobal(checked((int)(count * size)));
            try
            {
                uint capacity = count, result = GetRawInputDeviceList(memory, ref count, size);
                if (result == uint.MaxValue || result > capacity) throw new IOException("The keyboard list changed; select the device again.");
                IntPtr match = IntPtr.Zero;
                for (int i = 0; i < result; i++)
                {
                    var item = (RawDevice)Marshal.PtrToStructure(IntPtr.Add(memory, checked(i * (int)size)), typeof(RawDevice));
                    if (item.Type != 1) continue;
                    var name = new StringBuilder(4096); uint characters = (uint)name.Capacity;
                    uint returned = GetRawInputDeviceInfo(item.Device, 0x20000007, name, ref characters);
                    if (returned == uint.MaxValue || !string.Equals(name.ToString(), path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (match != IntPtr.Zero) throw new IOException("The keyboard collection is ambiguous.");
                    match = item.Device;
                }
                if (match == IntPtr.Zero) throw new IOException("The selected keyboard does not expose a matching standard input collection.");
                return match;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        sealed class NativeKeyboardWindow : NativeWindow, IDisposable
        {
            readonly KeyboardLearningBroker owner;
            readonly IntPtr buffer;
            readonly uint headerSize = (uint)(8 + IntPtr.Size * 2);
            internal NativeKeyboardWindow(KeyboardLearningBroker owner)
            {
                this.owner = owner; buffer = Marshal.AllocHGlobal(128);
                try { CreateHandle(new CreateParams { Caption = "", Parent = new IntPtr(-3) }); }
                catch { Marshal.FreeHGlobal(buffer); throw; }
            }

            protected override void WndProc(ref Message message)
            {
                try
                {
                    if (message.Msg == 0x8012) { Application.ExitThread(); return; }
                    if (message.Msg == 0xfe && message.WParam == new IntPtr(2))
                    {
                        foreach (KeyboardLearningSource source in owner.sources)
                            if (source.DeviceHandle == message.LParam) source.Disconnect("Keyboard disconnected; select it again to reconnect.");
                    }
                    else if (message.Msg == 0xff)
                    {
                        uint size = 128;
                        uint copied = GetRawInputData(message.LParam, 0x10000003, buffer, ref size, headerSize);
                        if (copied == uint.MaxValue || copied < headerSize + 16 || copied > 128) throw new IOException("Invalid raw keyboard input packet.");
                        if (Marshal.ReadInt32(buffer) == 1)
                        {
                            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
                            foreach (KeyboardLearningSource source in owner.sources)
                                if (source.DeviceHandle == device)
                                    source.Feed((ushort)Marshal.ReadInt16(buffer, (int)headerSize), (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 2), (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 6));
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (KeyboardLearningSource source in owner.sources) source.Disconnect("Keyboard input unavailable: " + ex.Message);
                    owner.Stop();
                }
                // Required WM_INPUT cleanup stays with DefWindowProc.
                base.WndProc(ref message);
            }

            public void Dispose() { DestroyHandle(); Marshal.FreeHGlobal(buffer); }
        }
    }
}
