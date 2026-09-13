using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    public sealed class HidLearningSource : ILearnedInputDeviceSource
    {
        readonly object gate = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly HidLearningNativeSession session;
        readonly Thread worker;
        bool disposed, reading;
        string status = "Ready; waiting for input.";
        Action<InputControlSample> sample;

        public string DeviceId { get; private set; }
        public string DisplayName { get; private set; }
        public bool IsReading { get { lock (gate) return reading && !disposed; } }
        public string Status { get { lock (gate) return status; } }
        public InputControlDescriptor[] Controls { get { return session.Decoder.Controls; } }
        public static long TimestampMilliseconds
        {
            get
            {
                long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
                return ticks / frequency * 1000 + ticks % frequency * 1000 / frequency;
            }
        }

        public event Action<InputControlSample> Sample
        {
            add { lock (gate) sample += value; }
            remove { lock (gate) sample -= value; }
        }

        public static bool IsEligible(CollectionInfo device)
        {
            if (device == null || !string.IsNullOrEmpty(device.error) || string.IsNullOrEmpty(device.devicePath) ||
                device.inputReportLength < 2 || device.inputReportLength > 4096) return false;
            // Standard controller/simulation collections only. System keyboard,
            // mouse, consumer and vendor-defined collections are not guessed.
            return device.usagePage == 1 && (device.usage == 4 || device.usage == 5 || device.usage == 8) ||
                device.usagePage == 2 && device.usage >= 1 && device.usage <= 0x0c;
        }

        public static string IdentifyDevice(CollectionInfo device)
        {
            if (device == null || string.IsNullOrEmpty(device.devicePath)) throw new ArgumentException("The input device has no collection path.", "device");
            // Port-specific identity is deliberately conservative. Two identical
            // unnumbered devices must never silently exchange learned bindings.
            string identity = "standard-hid-input-v1\n" + ProtocolFingerprint.Calculate(device, "standard-hid-input-v1") +
                "\n" + device.devicePath.ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }

        public static HidLearningSource Open(CollectionInfo device)
        {
            if (!IsEligible(device)) throw new NotSupportedException("This collection does not expose supported standard controller inputs.");
            HidLearningNativeSession.RejectVirtualOutput(device.devicePath);
            return new HidLearningSource(device);
        }

        HidLearningSource(CollectionInfo device)
        {
            DeviceId = IdentifyDevice(device);
            DisplayName = string.IsNullOrWhiteSpace(device.product) ? "HID controller" : device.product.Trim();
            try
            {
                session = new HidLearningNativeSession(device);
                reading = true;
                worker = new Thread(ReadLoop) { IsBackground = true, Name = "Learned HID input" };
                worker.Start();
            }
            catch
            {
                if (session != null) session.Dispose();
                stop.Dispose();
                throw;
            }
        }

        public bool TryGetValue(string controlId, out double value)
        {
            lock (gate)
            {
                value = 0;
                return !disposed && reading && session.Decoder.TryGetValue(controlId, out value);
            }
        }

        void ReadLoop()
        {
            var changes = new List<InputControlSample>();
            try
            {
                while (session.Read(stop))
                {
                    Action<InputControlSample> handler;
                    lock (gate)
                    {
                        if (disposed) break;
                        changes.Clear();
                        session.Decode(TimestampMilliseconds, changes);
                        status = "Receiving standard HID input.";
                        handler = sample;
                    }
                    if (handler != null)
                        foreach (InputControlSample change in changes)
                        {
                            if (stop.WaitOne(0)) break;
                            // A subscriber failure must not strand the read or
                            // native buffers; invalidate the source on failure.
                            handler(change);
                        }
                }
            }
            catch (Exception ex)
            {
                lock (gate) if (!disposed) status = "Input disconnected or unavailable: " + ex.Message;
            }
            finally
            {
                lock (gate) { reading = false; session.Decoder.Clear(); }
                try { session.Dispose(); }
                finally { stop.Dispose(); }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; reading = false; status = "Input closed.";
                session.Decoder.Clear(); sample = null;
                try { stop.Set(); } catch (ObjectDisposedException) { }
            }
            // Native memory is owned and drained by the reader thread. If a
            // broken driver delays cancellation, never free its pending buffer.
            if (Thread.CurrentThread != worker) worker.Join(1500);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidLearningData
    {
        internal ushort DataIndex, Reserved;
        internal uint RawValue;
        internal HidLearningData(ushort index, uint value) { DataIndex = index; Reserved = 0; RawValue = value; }
    }

    internal sealed class HidLearningControl
    {
        internal byte ReportId;
        internal ushort DataIndex;
        internal int BitSize;
        internal bool Signed, HasNull;
        internal InputControlDescriptor Descriptor;
    }

    // Pure descriptor/report interpretation, also exercised without HID DLLs.
    // Caps layout is the Windows HIDP_*_CAPS 72-byte ABI, not device packet bytes.
    internal sealed class HidLearningReportDecoder
    {
        internal const int MaximumControls = 1024;
        readonly HidLearningControl[] controls;
        readonly Dictionary<string, int> byId = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<int, int> byData = new Dictionary<int, int>();
        readonly List<int>[] byReport = new List<int>[256];
        readonly double[] values;
        readonly bool[] seen, present;
        readonly uint[] raw;

        internal InputControlDescriptor[] Controls
        {
            get
            {
                var result = new InputControlDescriptor[controls.Length];
                for (int i = 0; i < controls.Length; i++) result[i] = controls[i].Descriptor;
                return result;
            }
        }

        internal HidLearningReportDecoder(IEnumerable<HidLearningControl> source)
        {
            var list = new List<HidLearningControl>();
            foreach (HidLearningControl control in source)
            {
                if (control == null || control.Descriptor == null || list.Count >= MaximumControls) throw new InvalidDataException("Too many or invalid HID controls.");
                int index = list.Count, key = control.ReportId << 16 | control.DataIndex;
                if (byData.ContainsKey(key) || byId.ContainsKey(control.Descriptor.ControlId)) throw new InvalidDataException("Ambiguous HID control indices.");
                byId.Add(control.Descriptor.ControlId, index); byData.Add(key, index);
                if (byReport[control.ReportId] == null) byReport[control.ReportId] = new List<int>();
                byReport[control.ReportId].Add(index); list.Add(control);
            }
            if (list.Count == 0) throw new NotSupportedException("No supported buttons, axes or hat controls were found in this collection.");
            controls = list.ToArray(); values = new double[controls.Length]; seen = new bool[controls.Length];
            present = new bool[controls.Length]; raw = new uint[controls.Length];
        }

        internal bool TryGetValue(string controlId, out double value)
        {
            value = 0; int index;
            if (controlId == null || !byId.TryGetValue(controlId, out index)) return false;
            if (controls[index].Descriptor.Kind == InputControlKind.Relative) return true;
            if (!seen[index]) return false;
            value = values[index]; return true;
        }

        internal void Clear() { Array.Clear(seen, 0, seen.Length); Array.Clear(values, 0, values.Length); }

        internal void Decode(byte reportId, HidLearningData[] data, int count, long timestamp, List<InputControlSample> changes)
        {
            if (data == null || changes == null || count < 0 || count > data.Length) throw new ArgumentException("Invalid HID data list.");
            var indices = byReport[reportId];
            if (indices == null) return; // An unrelated report may be vendor-defined.
            foreach (int index in indices) { present[index] = false; raw[index] = 0; }
            for (int i = 0; i < count; i++)
            {
                int index;
                if (!byData.TryGetValue(reportId << 16 | data[i].DataIndex, out index)) continue;
                if (present[index]) throw new InvalidDataException("Duplicate HID data index in one report.");
                present[index] = true; raw[index] = data[i].RawValue;
            }
            // Validate a complete report before publishing any of its changes.
            foreach (int index in indices) ReadValue(index);
            foreach (int index in indices)
            {
                double value = ReadValue(index);
                bool relative = controls[index].Descriptor.Kind == InputControlKind.Relative;
                if (!seen[index] || value != values[index] || relative && value != 0)
                    changes.Add(new InputControlSample(controls[index].Descriptor.ControlId, value, timestamp));
                values[index] = value; seen[index] = true;
            }
        }

        double ReadValue(int index)
        {
            HidLearningControl control = controls[index];
            InputControlDescriptor descriptor = control.Descriptor;
            if (descriptor.Kind == InputControlKind.Button)
                return present[index] && (raw[index] & 0xff) != 0 ? 1 : 0;
            if (!present[index]) throw new InvalidDataException("A scalar HID control is missing from its report.");
            double value = DecodeScalar(raw[index], control.BitSize, control.Signed);
            if (value < descriptor.LogicalMinimum || value > descriptor.LogicalMaximum)
            {
                if (descriptor.Kind == InputControlKind.Hat && control.HasNull && descriptor.Neutral.HasValue) return descriptor.Neutral.Value;
                throw new InvalidDataException("A HID scalar is null or outside its declared range.");
            }
            return value;
        }

        internal static double DecodeScalar(uint value, int bits, bool signed)
        {
            if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException("bits");
            uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
            value &= mask;
            if (signed && (value & (1u << (bits - 1))) != 0)
                return (long)value - (1L << bits);
            return value;
        }

        internal static List<HidLearningControl> ParseCapabilities(IEnumerable<byte[]> buttons, IEnumerable<byte[]> scalars)
        {
            var controls = new List<HidLearningControl>();
            AddCapabilities(controls, buttons, false); AddCapabilities(controls, scalars, true);
            return controls;
        }

        static void AddCapabilities(List<HidLearningControl> controls, IEnumerable<byte[]> capabilities, bool scalar)
        {
            foreach (byte[] cap in capabilities)
            {
                if (cap == null || cap.Length != 72) throw new InvalidDataException("Invalid HID capability size.");
                if (cap[3] != 0 || (BitConverter.ToUInt16(cap, 4) & 1) != 0) continue; // Alias or constant.
                int page = BitConverter.ToUInt16(cap, 0), minimumUsage = BitConverter.ToUInt16(cap, 56);
                int maximumUsage = cap[12] == 0 ? minimumUsage : BitConverter.ToUInt16(cap, 58);
                int minimumIndex = BitConverter.ToUInt16(cap, 68), maximumIndex = cap[12] == 0 ? minimumIndex : BitConverter.ToUInt16(cap, 70);
                if (maximumUsage < minimumUsage || maximumIndex - minimumIndex != maximumUsage - minimumUsage)
                    throw new InvalidDataException("HID usage and data index ranges do not agree.");
                // GetData does not return usage value arrays (one usage repeated).
                if (scalar && cap[12] == 0 && BitConverter.ToUInt16(cap, 20) != 1) continue;
                if (!scalar && (page != 9 || cap[15] == 0)) continue;
                if (maximumUsage - minimumUsage + controls.Count >= MaximumControls) throw new InvalidDataException("This HID collection has too many controls.");
                for (int usage = minimumUsage; usage <= maximumUsage; usage++)
                {
                    if (scalar && !IsSupportedScalar(page, usage)) continue;
                    int bits = scalar ? BitConverter.ToUInt16(cap, 18) : 1;
                    if (bits < 1 || bits > 32) continue;
                    double minimum = scalar ? BitConverter.ToInt32(cap, 40) : 0;
                    double maximum = scalar ? BitConverter.ToInt32(cap, 44) : 1;
                    if (scalar && minimum >= 0 && maximum < 0) maximum = BitConverter.ToUInt32(cap, 44);
                    if (maximum <= minimum) continue;
                    bool relative = scalar && cap[15] == 0, hat = scalar && page == 1 && usage == 0x39;
                    if (hat && (relative || maximum - minimum > 31)) continue;
                    InputControlKind kind = !scalar ? InputControlKind.Button : hat ? InputControlKind.Hat : relative ? InputControlKind.Relative : InputControlKind.Absolute;
                    double? neutral = !scalar || relative ? (double?)0 : hat && cap[16] != 0 ? (double?)(maximum + 1) : null;
                    if (relative && (minimum > 0 || maximum < 0)) continue;
                    int dataIndex = minimumIndex + usage - minimumUsage;
                    // Include descriptor semantics as well as the parser index:
                    // a firmware that rearranges indices must not silently map
                    // an old binding to a different usage with identical bounds.
                    string id = "hid:" + cap[2].ToString(CultureInfo.InvariantCulture) + ":" + page.ToString("x", CultureInfo.InvariantCulture) + ":" + usage.ToString("x", CultureInfo.InvariantCulture) +
                        ":" + BitConverter.ToUInt16(cap, 6).ToString(CultureInfo.InvariantCulture) + ":" + dataIndex.ToString(CultureInfo.InvariantCulture);
                    string label = !scalar ? "Button " + usage.ToString(CultureInfo.InvariantCulture) : UsageLabel(page, usage);
                    if (BitConverter.ToUInt16(cap, 6) != 0) label += " · " + BitConverter.ToUInt16(cap, 6).ToString(CultureInfo.InvariantCulture);
                    controls.Add(new HidLearningControl { ReportId = cap[2], DataIndex = (ushort)dataIndex, BitSize = bits, Signed = minimum < 0, HasNull = scalar && cap[16] != 0,
                        Descriptor = new InputControlDescriptor(id, label, kind, minimum, maximum, neutral, null) });
                }
            }
        }

        static bool IsSupportedScalar(int page, int usage)
        {
            return page == 1 && usage >= 0x30 && usage <= 0x38 || page == 1 && usage == 0x39 ||
                page == 2 && usage >= 0xb0 && usage <= 0xd0;
        }

        static string UsageLabel(int page, int usage)
        {
            if (page == 1) return new[] { "X axis", "Y axis", "Z axis", "X rotation", "Y rotation", "Z rotation", "Slider", "Dial", "Wheel", "Hat switch" }[usage - 0x30];
            switch (usage)
            {
                case 0xba: return "Rudder";
                case 0xbb: return "Throttle";
                case 0xc4: return "Accelerator";
                case 0xc5: return "Brake";
                case 0xc6: return "Clutch";
                case 0xc8: return "Steering";
                default: return "Simulation axis " + usage.ToString("X2", CultureInfo.InvariantCulture);
            }
        }
    }

    // ReadFile, metadata and HidP parser calls only. No feature/output reports,
    // activation commands, exclusive access, keyboard hooks or input injection.
    internal sealed class HidLearningNativeSession : IDisposable
    {
        [DllImport("hid.dll")] static extern uint HidP_MaxDataListLength(int type, IntPtr preparsed);
        [DllImport("hid.dll")] static extern int HidP_GetData(int type, [Out] HidLearningData[] data, ref uint count, IntPtr preparsed, IntPtr report, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref Native.InterfaceData data);
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoData { internal int Size; internal Guid ClassGuid; internal uint DevInst; internal IntPtr Reserved; }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint node, StringBuilder buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_DevNode_Registry_Property(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);

        SafeFileHandle handle;
        IntPtr preparsed, report, overlapped, ready;
        bool pending;
        readonly int reportLength;
        readonly IntPtr[] waitHandles = new IntPtr[2];
        HidLearningData[] data;
        uint dataCount;
        byte reportId;
        internal HidLearningReportDecoder Decoder { get; private set; }

        internal HidLearningNativeSession(CollectionInfo device)
        {
            try
            {
                handle = Native.CreateFile(device.devicePath, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw Native.Error("Open selected HID input read-only");
                Native.Attributes attributes = new Native.Attributes { Size = Marshal.SizeOf(typeof(Native.Attributes)) };
                if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != device.vendorId || attributes.ProductId != device.productId || attributes.VersionNumber != device.version)
                    throw new InvalidDataException("The selected HID device changed; choose the input device again.");
                if (!Native.HidD_GetPreparsedData(handle, out preparsed)) throw Native.Error("Read HID metadata");
                Native.Caps caps;
                if (Native.HidP_GetCaps(preparsed, out caps) != Native.HidSuccess) throw new InvalidDataException("Cannot parse this HID collection.");
                reportLength = caps.InputLength;
                if (reportLength != device.inputReportLength || caps.UsagePage != device.usagePage || caps.Usage != device.usage)
                    throw new InvalidDataException("The HID collection descriptor changed.");
                uint capacity = HidP_MaxDataListLength(0, preparsed);
                if (capacity < 1 || capacity > 4096 || caps.InputIndices > 4096 || caps.InputButtons > 1024 || caps.InputValues > 1024)
                    throw new NotSupportedException("This HID descriptor exceeds the supported input limits.");
                data = new HidLearningData[capacity];
                Decoder = new HidLearningReportDecoder(HidLearningReportDecoder.ParseCapabilities(ReadCaps(false, caps.InputButtons), ReadCaps(true, caps.InputValues)));
                ready = Native.CreateEvent(IntPtr.Zero, true, false, null);
                if (ready == IntPtr.Zero) throw Native.Error("Create HID input event");
                report = Marshal.AllocHGlobal(reportLength);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
            }
            catch { Dispose(); throw; }
        }

        IEnumerable<byte[]> ReadCaps(bool scalar, ushort requested)
        {
            var result = new List<byte[]>();
            if (requested == 0) return result;
            ushort count = requested;
            IntPtr memory = Marshal.AllocHGlobal(requested * 72);
            try
            {
                int status = scalar ? Native.HidP_GetValueCaps(0, memory, ref count, preparsed) : Native.HidP_GetButtonCaps(0, memory, ref count, preparsed);
                if (status != Native.HidSuccess || count > requested) throw new InvalidDataException("Cannot read HID input capabilities.");
                for (int i = 0; i < count; i++)
                {
                    byte[] bytes = new byte[72]; Marshal.Copy(IntPtr.Add(memory, i * 72), bytes, 0, bytes.Length); result.Add(bytes);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return result;
        }

        internal bool Read(WaitHandle stop)
        {
            if (stop.WaitOne(0)) return false;
            Native.ResetEvent(ready);
            var state = new Native.Overlapped { Event = ready };
            Marshal.StructureToPtr(state, overlapped, false);
            bool completed = Native.ReadFile(handle, report, (uint)reportLength, IntPtr.Zero, overlapped);
            int error = Marshal.GetLastWin32Error();
            if (!completed && error != 997) throw new Win32Exception(error, "Read selected HID input");
            pending = true;
            waitHandles[0] = stop.SafeWaitHandle.DangerousGetHandle(); waitHandles[1] = ready;
            uint wait = WaitForMultipleObjects(2, waitHandles, false, uint.MaxValue);
            if (wait == 0) return false;
            if (wait != 1) throw Native.Error("Wait for selected HID input");
            uint count;
            bool success = Native.GetOverlappedResult(handle, overlapped, out count, false);
            pending = false;
            if (!success) throw Native.Error("Selected HID input disconnected");
            if (count != reportLength) throw new InvalidDataException("Unexpected HID input report length.");
            dataCount = (uint)data.Length;
            int status = HidP_GetData(0, data, ref dataCount, preparsed, report, (uint)reportLength);
            if (status != Native.HidSuccess || dataCount > data.Length) throw new InvalidDataException("Cannot decode the selected HID input report.");
            reportId = Marshal.ReadByte(report);
            return true;
        }

        internal void Decode(long timestamp, List<InputControlSample> changes) { Decoder.Decode(reportId, data, (int)dataCount, timestamp, changes); }

        public void Dispose()
        {
            if (pending && handle != null && !handle.IsInvalid)
            {
                Native.CancelIoEx(handle, overlapped); uint ignored;
                Native.GetOverlappedResult(handle, overlapped, out ignored, true); pending = false;
            }
            if (preparsed != IntPtr.Zero) { Native.HidD_FreePreparsedData(preparsed); preparsed = IntPtr.Zero; }
            if (handle != null) { handle.Dispose(); handle = null; }
            if (report != IntPtr.Zero) { Marshal.FreeHGlobal(report); report = IntPtr.Zero; }
            if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
            if (ready != IntPtr.Zero) { Native.CloseHandle(ready); ready = IntPtr.Zero; }
        }

        internal static bool IsVirtualOutputAncestor(string instanceId, string service)
        {
            string id = (instanceId ?? "").ToUpperInvariant(), driver = (service ?? "").ToUpperInvariant();
            return id.StartsWith("ROOT\\VIGEMBUS\\", StringComparison.Ordinal) || id.StartsWith("ROOT\\USBIP", StringComparison.Ordinal) ||
                driver == "VIGEMBUS" || driver == "USBIP2_UDE" || driver == "USBIP_VHCI" || driver == "USBIP2_VHCI";
        }

        internal static void RejectVirtualOutput(string path)
        {
            Guid guid; Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw Native.Error("Inspect selected input device");
            IntPtr detail = IntPtr.Zero, info = IntPtr.Zero;
            try
            {
                var item = new Native.InterfaceData { Size = Marshal.SizeOf(typeof(Native.InterfaceData)) };
                if (!SetupDiOpenDeviceInterface(set, path, 0, ref item)) throw Native.Error("Find selected input device");
                uint needed;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                if (needed < 6 || needed > 65536) throw new InvalidDataException("Invalid selected device metadata length.");
                detail = Marshal.AllocHGlobal((int)needed); Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                var device = new DeviceInfoData { Size = Marshal.SizeOf(typeof(DeviceInfoData)) };
                info = Marshal.AllocHGlobal(device.Size); Marshal.StructureToPtr(device, info, false);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref item, detail, needed, out needed, info)) throw Native.Error("Read selected device ancestry");
                device = (DeviceInfoData)Marshal.PtrToStructure(info, typeof(DeviceInfoData));
                uint node = device.DevInst;
                for (int depth = 0; depth < 32; depth++)
                {
                    var instance = new StringBuilder(1024);
                    if (CM_Get_Device_ID(node, instance, (uint)instance.Capacity, 0) != 0) throw new InvalidDataException("Cannot verify selected input device ancestry.");
                    byte[] serviceBytes = new byte[1024]; uint length = (uint)serviceBytes.Length, type;
                    uint serviceResult = CM_Get_DevNode_Registry_Property(node, 5, out type, serviceBytes, ref length, 0);
                    string service = serviceResult == 0 && type == 1 && length <= serviceBytes.Length ? Encoding.Unicode.GetString(serviceBytes, 0, (int)length).TrimEnd('\0') : null;
                    if (IsVirtualOutputAncestor(instance.ToString(), service))
                        throw new NotSupportedException("Virtual controller outputs cannot be learned as inputs; choose the physical device.");
                    if (instance.ToString().StartsWith("HTREE\\ROOT\\", StringComparison.OrdinalIgnoreCase)) return;
                    uint parent;
                    if (CM_Get_Parent(out parent, node, 0) != 0) throw new InvalidDataException("The input device ancestry changed; choose it again.");
                    node = parent;
                }
                throw new InvalidDataException("The selected input device ancestry is too deep.");
            }
            finally
            {
                if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail);
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                Native.SetupDiDestroyDeviceInfoList(set);
            }
        }
    }
}
