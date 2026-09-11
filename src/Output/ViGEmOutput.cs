using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Tk75.Mapping;

namespace Tk75.Output
{
    public interface ICancellableControllerConnection
    {
        void Connect(System.Threading.CancellationToken cancellationToken);
    }

    public interface IControllerOutput : IDisposable
    {
        string Status { get; }
        bool IsConnected { get; }
        void Connect();
        void Submit(ControllerFrame frame);
        void Neutral();
    }

    // Standard XInput buttons only. Bits 0x0400 and 0x0800 are reserved.
    // https://learn.microsoft.com/windows/win32/api/xinput/ns-xinput-xinput_gamepad
    [Flags]
    public enum XInputButtons : ushort
    {
        None = 0, DpadUp = 0x0001, DpadDown = 0x0002,
        DpadLeft = 0x0004, DpadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
    }

    // Native XUSB_REPORT is 12 bytes, passed by value. Conversion is pure:
    // it does not load a DLL, open a driver, or create a virtual controller.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct XInputPacket
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;

        public static XInputPacket FromFrame(ControllerFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (frame.Errors.Count != 0)
                throw new ArgumentException("Controllerzustand enthält einen Validierungsfehler: " + frame.Errors[0], "frame");
            var packet = new XInputPacket();
            packet.LeftX = Axis(frame.LeftX, "LeftX");
            packet.LeftY = Axis(frame.LeftY, "LeftY");
            packet.RightX = Axis(frame.RightX, "RightX");
            packet.RightY = Axis(frame.RightY, "RightY");
            packet.LeftTrigger = Trigger(frame.LeftTrigger, "LeftTrigger");
            packet.RightTrigger = Trigger(frame.RightTrigger, "RightTrigger");
            packet.Buttons = (ushort)(frame.Buttons & 0xF3FF);
            return packet;
        }

        private static double Finite(double value, string name)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value))
                throw new ArgumentException("Ungültiger Controllerwert in " + name + ": endlicher Zahlenwert erforderlich.", "frame");
            return value;
        }

        private static short Axis(double value, string name)
        {
            value = Math.Max(-1, Math.Min(1, Finite(value, name)));
            return (short)Math.Round(value * (value < 0 ? 32768 : 32767), MidpointRounding.AwayFromZero);
        }

        private static byte Trigger(double value, string name)
        {
            value = Math.Max(0, Math.Min(1, Finite(value, name)));
            return (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);
        }
    }

    // Optional legacy backend. ViGEmBus and its client SDK are retired; see
    // research/virtual-xinput-backend/FINDINGS.md for source, license and signature evidence.
    // The caller must install the official driver separately and supply an approved
    // local ViGEmClient.dll beside the application. This class never installs either.
    public sealed class ViGEmOutput : IControllerOutput
    {
        private const uint Success = 0x20000000;
        private const string KnownStartupFailure = "Controller-Ausgabe gesperrt: Der offizielle ViGEmBus meldet beim Start vier ausgelenkte Achsen und verwirft reine Nullmeldungen. Ein sicherer neutraler Start ist nicht möglich. Die Tastaturvorschau bleibt verfügbar.";
        private readonly object gate = new object();
        private bool disposed;
        private bool connected;
        private bool targetAdded;
        private string status = KnownStartupFailure;
        private IntPtr module, client, target;
        private NativeApi api;

        public string Status { get { lock (gate) { return status; } } }
        public bool IsConnected { get { lock (gate) { return connected; } } }
        // Static, unconditional production policy; no runtime switch or fallback.
        // Real XInput evidence and exact upstream boot/cache paths are documented in
        // research/virtual-xinput-backend/NEUTRAL-START-BLOCKER.md.
        public static string AvailabilityError { get { return KnownStartupFailure; } }

        public void Connect()
        {
            lock (gate)
            {
                CheckDisposed();
                if (connected) return;
                // This must precede file checks, LoadLibrary and every native call.
                // Even TargetAdd alone exposes the upstream non-neutral boot report.
                string unavailable = AvailabilityError;
                if (!String.IsNullOrEmpty(unavailable))
                {
                    status = unavailable;
                    throw new NotSupportedException(unavailable);
                }
                try
                {
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                        throw new PlatformNotSupportedException("Die ViGEm-Ausgabe benötigt Windows.");
                    string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ViGEmClient.dll"));
                    if (!File.Exists(path))
                        throw new FileNotFoundException("ViGEmClient.dll fehlt neben der Anwendung. Rohdatenvorschau bleibt ohne Controller-Ausgabe möglich.", path);

                    // A full local path with restricted dependency search; no PATH or
                    // current-directory fallback. Export delegates bind this exact module.
                    module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00001000);
                    if (module == IntPtr.Zero)
                        throw LoadFailure(Marshal.GetLastWin32Error());
                    api = new NativeApi(module);
                    client = api.Alloc();
                    if (client == IntPtr.Zero) throw new OutOfMemoryException("ViGEm konnte keinen Client anlegen.");
                    CheckResult(api.Connect(client), "Verbindung zum ViGEmBus");
                    target = api.TargetAlloc();
                    if (target == IntPtr.Zero) throw new OutOfMemoryException("ViGEm konnte keinen Controller anlegen.");
                    CheckResult(api.TargetAdd(client, target), "Controller verbinden");
                    targetAdded = true;
                    CheckResult(api.Update(client, target, new XInputPacket()), "Controller neutral initialisieren");
                    connected = true;
                    status = "Virtueller Xbox-360-Controller verbunden. ViGEm ist nicht mehr in Wartung.";
                }
                catch (Exception ex)
                {
                    string cleanup = ReleaseLocked(true);
                    status = "Controller nicht verbunden: " + ex.Message + cleanup;
                    throw new InvalidOperationException(status, ex);
                }
            }
        }

        public void Submit(ControllerFrame frame)
        {
            lock (gate)
            {
                CheckDisposed();
                if (!connected) throw new InvalidOperationException("Controller-Ausgabe ist nicht verbunden.");
                try
                {
                    XInputPacket packet = XInputPacket.FromFrame(frame);
                    CheckResult(api.Update(client, target, packet), "Controllerzustand senden");
                }
                catch (Exception ex)
                {
                    string cleanup = ReleaseLocked(true);
                    status = "Ausgabe nach Fehler getrennt: " + ex.Message + cleanup;
                    throw new InvalidOperationException(status, ex);
                }
            }
        }

        public void Neutral()
        {
            lock (gate)
            {
                if (disposed || !connected) return;
                try { CheckResult(api.Update(client, target, new XInputPacket()), "Controller freigeben"); }
                catch (Exception ex)
                {
                    string cleanup = ReleaseLocked(true);
                    status = "Ausgabe nach Freigabefehler getrennt: " + ex.Message + cleanup;
                    throw new InvalidOperationException(status, ex);
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                string cleanup = ReleaseLocked(true);
                status = "Controller-Ausgabe beendet." + cleanup;
            }
        }

        // Call only while holding gate. Every resource is attempted independently;
        // no repeated Dispose can double-free a pointer. No callback is registered.
        private string ReleaseLocked(bool neutral)
        {
            connected = false;
            bool wasAdded = targetAdded;
            targetAdded = false;
            NativeApi oldApi = api;
            IntPtr oldTarget = target, oldClient = client, oldModule = module;
            api = null;
            target = client = module = IntPtr.Zero;
            string failures = "";
            if (oldApi != null)
            {
                if (oldClient != IntPtr.Zero && oldTarget != IntPtr.Zero && wasAdded)
                {
                    if (neutral) TryCleanup(delegate { CheckResult(oldApi.Update(oldClient, oldTarget, new XInputPacket()), "Neutralisierung"); }, ref failures);
                    TryCleanup(delegate { CheckResult(oldApi.TargetRemove(oldClient, oldTarget), "Controller entfernen"); }, ref failures);
                }
                if (oldTarget != IntPtr.Zero) TryCleanup(delegate { oldApi.TargetFree(oldTarget); }, ref failures);
                if (oldClient != IntPtr.Zero)
                {
                    TryCleanup(delegate { oldApi.Disconnect(oldClient); }, ref failures);
                    TryCleanup(delegate { oldApi.Free(oldClient); }, ref failures);
                }
            }
            if (oldModule != IntPtr.Zero)
                TryCleanup(delegate { if (!FreeLibrary(oldModule)) throw new Win32Exception(Marshal.GetLastWin32Error(), "ViGEmClient konnte nicht freigegeben werden."); }, ref failures);
            return failures.Length == 0 ? "" : " Bereinigung meldete: " + failures;
        }

        private static void TryCleanup(Action action, ref string failures)
        {
            try { action(); }
            catch (Exception ex) { failures += (failures.Length == 0 ? "" : " / ") + ex.Message; }
        }

        private void CheckDisposed()
        {
            if (disposed) throw new ObjectDisposedException("ViGEmOutput");
        }

        private static Exception LoadFailure(int error)
        {
            string detail = error == 193 ? "Architektur der Client-DLL passt nicht zur Anwendung." :
                error == 577 || error == 1260 ? "Windows hat die Client-DLL gemäß seiner Sicherheitsrichtlinie blockiert." :
                error == 126 ? "Client-DLL oder eine ihrer Abhängigkeiten wurde nicht gefunden." :
                "Client-DLL konnte nicht geladen werden.";
            return new Win32Exception(error, detail + " Ausgabe bleibt aus; Windows-Schutz wird nicht verändert.");
        }

        private static void CheckResult(uint code, string operation)
        {
            if (code == Success) return;
            string detail;
            switch (code)
            {
                case 0xE0000001: detail = "ViGEmBus-Treiber nicht gefunden; separat installieren oder Rohdatenvorschau verwenden"; break;
                case 0xE0000002: detail = "kein freier Controllerplatz"; break;
                case 0xE0000003: detail = "ungültiger Controller"; break;
                case 0xE0000004: detail = "Controller konnte nicht entfernt werden"; break;
                case 0xE0000005: case 0xE0000012: detail = "bereits verbunden"; break;
                case 0xE0000006: detail = "Controller nicht initialisiert"; break;
                case 0xE0000007: detail = "Controller nicht angeschlossen"; break;
                case 0xE0000008: detail = "Treiber und Client-Version passen nicht zusammen"; break;
                case 0xE0000009: detail = "Zugriff auf ViGEmBus verweigert"; break;
                case 0xE0000013: detail = "Verbindung zum Treiber ungültig"; break;
                case 0xE0000015: detail = "ungültiger Parameter"; break;
                case 0xE0000016: detail = "Funktion nicht unterstützt"; break;
                case 0xE0000017: detail = "Windows-API-Fehler"; break;
                case 0xE0000018: detail = "Zeitüberschreitung"; break;
                case 0xE0000019: detail = "Client wird bereits beendet"; break;
                default: detail = "unbekannter ViGEm-Fehler"; break;
            }
            throw new InvalidOperationException(operation + ": " + detail + " (0x" + code.ToString("X8") + ").");
        }

        // Source ABI: nefarius/ViGEm.NET at2197a9a6200a1cc4cbefc19e4ef6c9bbb63ff567,
        // ViGEmClient.Native.cs. Cdecl and XUSB_REPORT-by-value were statically
        // checked against the headers and official x64/x86 DLL export tables.
        private sealed class NativeApi
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Allocate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeOne(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ConnectOne(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint TargetOperation(IntPtr client, IntPtr target);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint SubmitPacket(IntPtr client, IntPtr target, XInputPacket packet);
            internal readonly Allocate Alloc, TargetAlloc;
            internal readonly FreeOne Free, Disconnect, TargetFree;
            internal readonly ConnectOne Connect;
            internal readonly TargetOperation TargetAdd, TargetRemove;
            internal readonly SubmitPacket Update;

            internal NativeApi(IntPtr library)
            {
                Alloc = Bind<Allocate>(library, "vigem_alloc");
                Free = Bind<FreeOne>(library, "vigem_free");
                Connect = Bind<ConnectOne>(library, "vigem_connect");
                Disconnect = Bind<FreeOne>(library, "vigem_disconnect");
                TargetAlloc = Bind<Allocate>(library, "vigem_target_x360_alloc");
                TargetFree = Bind<FreeOne>(library, "vigem_target_free");
                TargetAdd = Bind<TargetOperation>(library, "vigem_target_add");
                TargetRemove = Bind<TargetOperation>(library, "vigem_target_remove");
                Update = Bind<SubmitPacket>(library, "vigem_target_x360_update");
            }

            private static T Bind<T>(IntPtr library, string name) where T : class
            {
                IntPtr address = GetProcAddress(library, name);
                if (address == IntPtr.Zero) throw new EntryPointNotFoundException("ViGEmClient unterstützt " + name + " nicht.");
                return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr library);
    }
}
