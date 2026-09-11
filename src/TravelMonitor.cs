using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Tk75.Diagnostics
{
    // Separate from strictly passive Tk75Diag. Only identify and monitor on/off are allowed.
    internal static class FeatureNative
    {
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
    }
    internal static class TravelMonitor
    {
        static volatile bool stop;
        static JavaScriptSerializer json = new JavaScriptSerializer();
        static string Utc() { return DateTime.UtcNow.ToString("o"); }
        static void Log(StreamWriter writer, object item) { writer.WriteLine(json.Serialize(item)); writer.Flush(); }
        static void Set(SafeFileHandle handle, byte[] request, StreamWriter writer, string purpose)
        {
            // Write the intention to disk before issuing the hardware command.
            Log(writer, new { type = "feature-request", utc = Utc(), purpose = purpose, hex = BitConverter.ToString(request), reportLength = request.Length });
            if (!FeatureNative.HidD_SetFeature(handle, request, request.Length)) throw Native.Error("HidD_SetFeature " + purpose);
        }
        static byte[] Identify(SafeFileHandle handle, StreamWriter writer)
        {
            Set(handle, MonitorProtocol.IdentifyRequest(), writer, "read-device-identity");
            // Firmware replies may become available asynchronously; bounded request/reply wait.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(20);
                byte[] response = new byte[65];
                if (!FeatureNative.HidD_GetFeature(handle, response, response.Length)) throw Native.Error("HidD_GetFeature identify");
                Log(writer, new { type = "feature-response", utc = Utc(), purpose = "read-device-identity", hex = BitConverter.ToString(response), reportLength = response.Length });
                if (response[0] == 0 && response[1] == 0x8f) { MonitorProtocol.DeviceId(response); return response; }
            }
            throw new InvalidDataException("No matching identify response. No monitor activation performed.");
        }
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--stream-host") return MonitorStreamHost.Run();
            try
            {
                if (args.Length == 0 || args[0] == "--help")
                {
                    Console.WriteLine("TK75 Herstellermonitor | Diagnose mit expliziten Feature-Kommandos\n" +
                        "  identify --out identity.jsonl\n  capture --out travel.jsonl [--seconds 20] [--label W]\n" +
                        "  monitor-off --out cleanup.jsonl\n" +
                        "Nur TK75 TMR 3151:5030 (Kabel). Capture identifiziert das Modell, schaltet 1B an und im finally wieder aus.\n" +
                        "Kein XInput. Keine Konfiguration, Kalibrierung oder Firmware wird geschrieben. Ctrl+C beendet.");
                    return 0;
                }
                if (args[0] != "identify" && args[0] != "capture" && args[0] != "monitor-off") throw new ArgumentException("Unknown command");
                Dictionary<string, string> options = new Dictionary<string, string>();
                for (int i = 1; i < args.Length; i += 2)
                {
                    if (i + 1 >= args.Length || !new[] { "--out", "--seconds", "--label" }.Contains(args[i])) throw new ArgumentException("Invalid options");
                    options.Add(args[i], args[i + 1]);
                }
                if (!options.ContainsKey("--out")) throw new ArgumentException("--out is required; logs never overwrite existing files");
                int seconds = options.ContainsKey("--seconds") ? int.Parse(options["--seconds"], CultureInfo.InvariantCulture) : 20;
                if (seconds < 1 || seconds > 300) throw new ArgumentException("seconds must be 1..300");
                string label = options.ContainsKey("--label") ? options["--label"] : "unlabelled";
                List<CollectionInfo> devices = HidInventory.Enumerate().Where(d => d.vendorId == 0x3151 && d.productId == 0x5030 && d.product == "TK75 TMR" && d.error == null).ToList();
                List<CollectionInfo> configs = devices.Where(d => d.usagePage == 0xffff && d.usage == 2 && d.featureReportLength == 65).ToList();
                List<CollectionInfo> inputs = devices.Where(d => d.usagePage == 0xffff && d.usage == 1 && d.inputReportLength == 32).ToList();
                if (configs.Count != 1 || inputs.Count != 1) throw new InvalidOperationException("Expected exactly one TK75 TMR feature/input pair. Disconnect duplicate devices or reconnect keyboard by cable.");
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stop = true; };
                using (StreamWriter writer = new StreamWriter(new FileStream(options["--out"], FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
                using (SafeFileHandle handle = Native.CreateFile(configs[0].devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw Native.Error("Open TK75 feature interface");
                    Log(writer, new { type = "session", schemaVersion = 1, utc = Utc(), mode = args[0], label = label, seconds = seconds, note = "Manufacturer identify and temporary diagnostic monitor commands only; no XInput; no save/reset/calibration commands." });
                    Log(writer, new { type = "device", utc = Utc(), device = inputs[0] });
                    byte[] identity = Identify(handle, writer);
                    uint deviceId = MonitorProtocol.DeviceId(identity); int firmwareV5Candidate = MonitorProtocol.UsbFirmwareV5Candidate(identity);
                    Log(writer, new { type = "identity", utc = Utc(), deviceId = deviceId, firmwareV5Candidate = firmwareV5Candidate, firmwareV5CandidateHex = firmwareV5Candidate.ToString("X4"), firmwareInterpretation = "unverified V5 host layout; V4 uses different bytes and endianness", firmwareCandidateSource = "qmk-index.Bs-RnLKs.js getUSBVersion" });
                    Console.WriteLine("Device ID: {0}; unbestaetigter V5-Firmwarekandidat (unverified): 0x{1:X4}", deviceId, firmwareV5Candidate);
                    if (args[0] == "identify") return 0;
                    if (!MonitorProtocol.IsSupportedTk75(deviceId)) throw new InvalidOperationException("Unknown TK75 device ID; monitor command withheld.");
                    if (args[0] == "monitor-off") { Set(handle, MonitorProtocol.MonitorRequest(false), writer, "monitor-off"); return 0; }
                    bool cleanupRequired = false; int reports = 0; Stopwatch clock = new Stopwatch();
                    try
                    {
                        using (InputReader reader = new InputReader(inputs[0]))
                        {
                            cleanupRequired = true;
                            Set(handle, MonitorProtocol.MonitorRequest(true), writer, "monitor-on");
                            clock.Start();
                            Console.WriteLine("MESSUNG LAEUFT: {0}, {1} Sekunden. Rohreports werden protokolliert.", label, seconds);
                            while (!stop && clock.Elapsed.TotalSeconds < seconds && reports < 250000)
                            {
                                byte[] report = reader.Read(100);
                                if (report == null) continue;
                                reports++;
                                Log(writer, new { type = "report", utc = Utc(), devicePath = inputs[0].devicePath, elapsedMs = clock.Elapsed.TotalMilliseconds, reportId = (int)report[0], reportLength = report.Length, hex = BitConverter.ToString(report), label = label });
                                if (reports <= 8) Console.WriteLine(BitConverter.ToString(report));
                            }
                        }
                    }
                    finally
                    {
                        if (cleanupRequired)
                        {
                            // Cleanup must still reach hardware if disk logging has failed.
                            byte[] off = MonitorProtocol.MonitorRequest(false);
                            bool disabled = FeatureNative.HidD_SetFeature(handle, off, off.Length);
                            int error = Marshal.GetLastWin32Error();
                            Console.WriteLine(disabled ? "Monitor-OFF uebertragen." : "Monitor-OFF fehlgeschlagen. Kabel trennen oder monitor-off erneut ausfuehren.");
                            try { Log(writer, new { type = "cleanup", utc = Utc(), purpose = "monitor-off", hex = BitConverter.ToString(off), transferred = disabled, win32Error = disabled ? 0 : error }); }
                            catch (IOException) { Console.Error.WriteLine("Cleanup log unavailable."); }
                            if (!disabled) throw new System.ComponentModel.Win32Exception(error, "Monitor cleanup failed");
                        }
                    }
                    Log(writer, new { type = "end", utc = Utc(), reports = reports, elapsedMs = clock.Elapsed.TotalMilliseconds, stopped = stop, limitReached = reports >= 250000 });
                    Console.WriteLine("{0} Reports gespeichert: {1}", reports, options["--out"]);
                    return 0;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
