using System;
using System.IO;
using System.Text;
using System.Globalization;

namespace Tk75.Diagnostics
{
    // Pure parent/child wire grammar and heartbeat state; no HID or process APIs.
    public sealed class MonitorHostProtocol
    {
        public const int MaximumLineLength = 4096;
        public const int HeartbeatTimeoutMs = 2000;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        readonly object gate = new object();
        string devicePath, error;
        bool stopped;
        long heartbeat;
        MonitorRgbReadRequest rgbRead;
        MonitorRgbWriteRequest rgbWrite;
        int activeRgbReadId;

        public string DevicePath { get { lock (gate) return devicePath; } }
        public bool Stopped { get { lock (gate) return stopped; } }
        public string Error { get { lock (gate) return error; } }

        public void Receive(string line, long elapsedMs)
        {
            lock (gate)
            {
                if (stopped) return;
                if (elapsedMs - heartbeat >= HeartbeatTimeoutMs)
                {
                    error = "Die Verbindung zum Hauptprogramm ist abgelaufen.";
                    stopped = true;
                    return;
                }
                if (line == null) { stopped = true; return; }
                if (line.Length > MaximumLineLength) throw new InvalidDataException("Die Steuerzeile ist zu lang.");
                if (devicePath == null)
                {
                    if (!line.StartsWith("START ", StringComparison.Ordinal)) throw new InvalidDataException("Die erste Steuerzeile muss START sein.");
                    string encoded = line.Substring(6);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(encoded); }
                    catch (FormatException) { throw new InvalidDataException("Der Gerätepfad ist nicht gültig Base64-kodiert."); }
                    if (Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Die Gerätepfad-Kodierung ist nicht eindeutig.");
                    string decoded;
                    try { decoded = StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { throw new InvalidDataException("Der Gerätepfad enthält kein gültiges UTF-8."); }
                    if (!decoded.StartsWith("\\\\?\\hid#", StringComparison.OrdinalIgnoreCase) || decoded.Length > 2048)
                        throw new InvalidDataException("Der Gerätepfad ist kein vollständiger HID-Pfad.");
                    foreach (char c in decoded) if (Char.IsControl(c)) throw new InvalidDataException("Der Gerätepfad enthält Steuerzeichen.");
                    devicePath = decoded;
                    heartbeat = elapsedMs;
                }
                else if (line == "PING") heartbeat = elapsedMs;
                else if (line == "STOP") stopped = true;
                else if (line.StartsWith("RGBREAD ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' '); int requestId;
                    if (parts.Length != 3 || !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out requestId) || requestId < 1 ||
                        parts[1] != requestId.ToString(CultureInfo.InvariantCulture) || parts[2].Length != 1 || parts[2][0] < '0' || parts[2][0] > '4')
                        throw new InvalidDataException("Ungültige RGB-Leseanfrage.");
                    if (activeRgbReadId != 0) throw new InvalidDataException("Eine RGB-Leseanfrage ist bereits aktiv.");
                    activeRgbReadId = requestId; rgbRead = new MonitorRgbReadRequest(requestId, parts[2][0] - '0');
                }
                else if (line.StartsWith("RGBWRITE ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' '); int requestId;
                    if (parts.Length != 4 || !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out requestId) || requestId < 1 ||
                        parts[1] != requestId.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Ungültige RGB-Vergleichsanfrage.");
                    if (activeRgbReadId != 0) throw new InvalidDataException("Eine RGB-Anfrage ist bereits aktiv.");
                    Tk75RgbSnapshot expected = DecodeRgbSnapshot(parts[2]), desired = DecodeRgbSnapshot(parts[3]);
                    Tk75RgbExchange.Validate(expected, desired);
                    activeRgbReadId = requestId; rgbWrite = new MonitorRgbWriteRequest(requestId, expected, desired);
                }
                else throw new InvalidDataException("Unbekannte Steuerzeile; erlaubt sind PING, STOP, RGBREAD oder RGBWRITE mit Zustandsvergleich.");
            }
        }

        public MonitorRgbReadRequest TakeRgbRead()
        {
            lock (gate) { if (stopped) return null; MonitorRgbReadRequest result = rgbRead; rgbRead = null; return result; }
        }
        public MonitorRgbWriteRequest TakeRgbWrite()
        {
            lock (gate) { if (stopped) return null; MonitorRgbWriteRequest result = rgbWrite; rgbWrite = null; return result; }
        }
        static Tk75RgbSnapshot DecodeRgbSnapshot(string encoded)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new InvalidDataException("Ungültig kodierter RGB-Zustand."); }
            if (Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Uneindeutig kodierter RGB-Zustand.");
            return Tk75RgbProtocol.DecodeSnapshot(bytes);
        }
        public void CompleteRgbRead(int requestId)
        {
            lock (gate)
            {
                if (activeRgbReadId != requestId || rgbRead != null || rgbWrite != null) throw new InvalidOperationException("RGB request completion does not match the active request.");
                activeRgbReadId = 0;
            }
        }

        public void Stop() { lock (gate) stopped = true; }
        public void Fail(string message) { lock (gate) { if (error == null) error = message; stopped = true; } }
        public void CheckHeartbeat(long elapsedMs)
        {
            lock (gate)
            {
                if (!stopped && elapsedMs - heartbeat >= HeartbeatTimeoutMs)
                {
                    error = "Die Verbindung zum Hauptprogramm ist abgelaufen.";
                    stopped = true;
                }
            }
        }

        public static string ReadBoundedLine(TextReader reader)
        {
            StringBuilder line = new StringBuilder();
            while (true)
            {
                int next = reader.Read();
                if (next < 0)
                {
                    if (line.Length == 0) return null;
                    throw new InvalidDataException("Die letzte Steuerzeile wurde unvollständig übertragen.");
                }
                if (next == '\n')
                {
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                    return line.ToString();
                }
                if (line.Length >= MaximumLineLength) throw new InvalidDataException("Die Steuerzeile ist zu lang.");
                line.Append((char)next);
            }
        }

        public static string DataLine(byte[] report)
        {
            if (report == null || report.Length != 32) throw new InvalidDataException("Der Druckwertbericht muss genau 32 Byte enthalten.");
            return "DATA " + BitConverter.ToString(report).Replace("-", "");
        }

        public static string ErrorLine(string message)
        {
            if (message == null) message = "Unbekannter Fehler beim Tastatur-Zugriff.";
            // Bound output independently of arbitrary exception text.
            if (message.Length > 1000) message = message.Substring(0, 1000);
            return "ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        }
    }

    public sealed class MonitorRgbReadRequest
    {
        public int RequestId { get; private set; }
        public int Layer { get; private set; }
        internal MonitorRgbReadRequest(int requestId, int layer) { RequestId = requestId; Layer = layer; }
    }
    public sealed class MonitorRgbWriteRequest
    {
        public int RequestId { get; private set; }
        public Tk75RgbSnapshot Expected { get; private set; }
        public Tk75RgbSnapshot Desired { get; private set; }
        internal MonitorRgbWriteRequest(int id, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired) { RequestId = id; Expected = expected; Desired = desired; }
    }
}
