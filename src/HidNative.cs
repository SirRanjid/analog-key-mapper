using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tk75.Diagnostics
{
    // Metadata and interrupt-IN only. No SetFeature, WriteFile or output reports.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
        { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes
        { public int Size; public ushort VendorId, ProductId, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices;
            public ushort OutputButtons, OutputValues, OutputIndices;
            public ushort FeatureButtons, FeatureValues, FeatureIndices;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Overlapped
        { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        // HidD routines return Windows BOOLEAN (one byte), not Win32 BOOL.
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint length, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint size, IntPtr bytesRead, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ResetEvent(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        internal const int HidSuccess = 0x00110000;
        internal static Exception Error(string operation) { return new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    }

    public sealed class CollectionInfo
    {
        public string devicePath, manufacturer, product, serial, error;
        public int vendorId, productId, version, usagePage, usage, inputReportLength, outputReportLength, featureReportLength;
        public List<Dictionary<string, object>> reportCapabilities = new List<Dictionary<string, object>>();
    }

    public static class HidInventory
    {
        static string ReadString(SafeFileHandle handle, int kind)
        {
            byte[] bytes = new byte[512];
            bool ok = kind == 0 ? Native.HidD_GetProductString(handle, bytes, bytes.Length) :
                kind == 1 ? Native.HidD_GetManufacturerString(handle, bytes, bytes.Length) : Native.HidD_GetSerialNumberString(handle, bytes, bytes.Length);
            return ok ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;
        }
        static int U16(byte[] b, int i) { return BitConverter.ToUInt16(b, i); }
        static void ReadCapabilities(CollectionInfo device, IntPtr preparsed, int type, bool values, ushort count)
        {
            if (count == 0) return;
            // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are both 72 bytes in Windows hidpi.h.
            IntPtr memory = Marshal.AllocHGlobal(count * 72);
            try
            {
                int status = values ? Native.HidP_GetValueCaps(type, memory, ref count, preparsed) : Native.HidP_GetButtonCaps(type, memory, ref count, preparsed);
                if (status != Native.HidSuccess) throw new InvalidOperationException("HidP capabilities status 0x" + status.ToString("X8"));
                for (int n = 0; n < count; n++)
                {
                    byte[] b = new byte[72]; Marshal.Copy(IntPtr.Add(memory, n * 72), b, 0, b.Length);
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["reportType"] = new string[] { "input", "output", "feature" }[type];
                    item["kind"] = values ? "value" : "button";
                    item["usagePage"] = U16(b, 0); item["reportId"] = (int)b[2]; item["linkCollection"] = U16(b, 6);
                    item["isRange"] = b[12] != 0; item["isAbsolute"] = b[15] != 0;
                    item["usageMinimum"] = U16(b, 56); item["usageMaximum"] = b[12] != 0 ? U16(b, 58) : U16(b, 56);
                    if (values)
                    {
                        item["bitSize"] = U16(b, 18); item["reportCount"] = U16(b, 20);
                        item["logicalMinimum"] = BitConverter.ToInt32(b, 40); item["logicalMaximum"] = BitConverter.ToInt32(b, 44);
                        item["physicalMinimum"] = BitConverter.ToInt32(b, 48); item["physicalMaximum"] = BitConverter.ToInt32(b, 52);
                        item["unitsExponent"] = BitConverter.ToUInt32(b, 32); item["units"] = BitConverter.ToUInt32(b, 36);
                    }
                    device.reportCapabilities.Add(item);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        public static List<CollectionInfo> Enumerate()
        {
            Guid guid; Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw Native.Error("SetupDiGetClassDevs");
            List<CollectionInfo> result = new List<CollectionInfo>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    Native.InterfaceData data = new Native.InterfaceData(); data.Size = Marshal.SizeOf(data);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    { if (Marshal.GetLastWin32Error() == 259) break; throw Native.Error("SetupDiEnumDeviceInterfaces"); }
                    uint required;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 6) throw Native.Error("SetupDiGetDeviceInterfaceDetail size");
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) throw Native.Error("SetupDiGetDeviceInterfaceDetail");
                        path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    CollectionInfo device = new CollectionInfo(); device.devicePath = path;
                    result.Add(device);
                    try
                    {
                        using (SafeFileHandle handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) throw Native.Error("Open metadata");
                            Native.Attributes attributes = new Native.Attributes(); attributes.Size = Marshal.SizeOf(attributes);
                            if (!Native.HidD_GetAttributes(handle, ref attributes)) throw Native.Error("HidD_GetAttributes");
                            device.vendorId = attributes.VendorId; device.productId = attributes.ProductId; device.version = attributes.VersionNumber;
                            device.product = ReadString(handle, 0); device.manufacturer = ReadString(handle, 1); device.serial = ReadString(handle, 2);
                            IntPtr preparsed;
                            if (!Native.HidD_GetPreparsedData(handle, out preparsed)) throw Native.Error("HidD_GetPreparsedData");
                            try
                            {
                                Native.Caps caps;
                                if (Native.HidP_GetCaps(preparsed, out caps) != Native.HidSuccess) throw new InvalidOperationException("HidP_GetCaps failed");
                                device.usage = caps.Usage; device.usagePage = caps.UsagePage;
                                device.inputReportLength = caps.InputLength; device.outputReportLength = caps.OutputLength; device.featureReportLength = caps.FeatureLength;
                                ReadCapabilities(device, preparsed, 0, false, caps.InputButtons); ReadCapabilities(device, preparsed, 0, true, caps.InputValues);
                                ReadCapabilities(device, preparsed, 1, false, caps.OutputButtons); ReadCapabilities(device, preparsed, 1, true, caps.OutputValues);
                                ReadCapabilities(device, preparsed, 2, false, caps.FeatureButtons); ReadCapabilities(device, preparsed, 2, true, caps.FeatureValues);
                            }
                            finally { Native.HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    catch (Exception ex) { device.error = ex.Message; }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
    }

    // Exactly one outstanding read. Cancel + drain before freeing native buffers.
    internal sealed class InputReader : IDisposable
    {
        SafeFileHandle handle;
        IntPtr buffer, overlapped, ready;
        bool pending;
        readonly int length;
        public InputReader(CollectionInfo device)
        {
            length = device.inputReportLength;
            if (length < 1 || length > 65536) throw new ArgumentException("Invalid input report length");
            try
            {
                handle = Native.CreateFile(device.devicePath, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw Native.Error("Open input read-only");
                ready = Native.CreateEvent(IntPtr.Zero, true, false, null);
                if (ready == IntPtr.Zero) throw Native.Error("CreateEvent");
                buffer = Marshal.AllocHGlobal(length); overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
            }
            catch { Dispose(); throw; }
        }
        public byte[] Read(int timeoutMs)
        {
            if (!pending)
            {
                Native.ResetEvent(ready);
                Native.Overlapped state = new Native.Overlapped(); state.Event = ready;
                Marshal.StructureToPtr(state, overlapped, false);
                bool completed = Native.ReadFile(handle, buffer, (uint)length, IntPtr.Zero, overlapped);
                int error = Marshal.GetLastWin32Error();
                if (!completed && error != 997) throw new Win32Exception(error, "ReadFile input");
                pending = true;
            }
            uint wait = Native.WaitForSingleObject(ready, (uint)timeoutMs);
            if (wait == 258) return null;
            if (wait != 0) throw Native.Error("Wait for input");
            uint count;
            bool ok = Native.GetOverlappedResult(handle, overlapped, out count, false);
            pending = false;
            if (!ok) throw Native.Error("Input completion");
            if (count < 1 || count > length) throw new InvalidOperationException("Invalid input completion length");
            byte[] report = new byte[(int)count]; Marshal.Copy(buffer, report, 0, report.Length);
            return report;
        }
        public void Dispose()
        {
            if (pending && handle != null && !handle.IsInvalid)
            {
                Native.CancelIoEx(handle, overlapped); uint ignored;
                Native.GetOverlappedResult(handle, overlapped, out ignored, true);
                pending = false;
            }
            if (handle != null) { handle.Dispose(); handle = null; }
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
            if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
            if (ready != IntPtr.Zero) { Native.CloseHandle(ready); ready = IntPtr.Zero; }
        }
    }
}
