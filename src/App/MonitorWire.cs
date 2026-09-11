using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tk75.App
{
    // Pure, monotonic device evidence lease. Expiry is irreversible for this
    // connection: a late pipe reply cannot resurrect a previously held key.
    public sealed class MonitorDeviceLease
    {
        public const long MaximumSilenceMs = 500;
        bool started, proved, expired;
        long lastObserved, lastEvidence, sequence, helperTime = -1, firstHelperTime, firstReceipt;
        public void Start(long now)
        {
            if (started || now < 0) throw new InvalidDataException("Invalid device lease start.");
            started = true; lastObserved = lastEvidence = now;
        }
        // 0 = waiting for first proof, 1 = live, 2 = permanently expired.
        public int Status(long now)
        {
            if (!started) return 0;
            if (now < lastObserved || now - lastEvidence >= MaximumSilenceMs) expired = true;
            lastObserved = Math.Max(lastObserved, now);
            return expired ? 2 : proved ? 1 : 0;
        }
        public void AcceptProof(string line, long now)
        {
            string[] parts = line == null ? new string[0] : line.Split(' ');
            long next, timestamp;
            if (parts.Length != 3 || parts[0] != "LIVE" ||
                !Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out next) ||
                !Int64.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp) ||
                next < 1 || next != sequence + 1 || timestamp < 0 || timestamp < helperTime ||
                next.ToString(CultureInfo.InvariantCulture) != parts[1] || timestamp.ToString(CultureInfo.InvariantCulture) != parts[2])
                throw new InvalidDataException("Invalid device liveness sequence.");
            if (!started || Status(now) == 2) throw new InvalidDataException("Device liveness expired; reconnect required.");
            // Both processes use the same Windows monotonic clock. Refuse proof
            // that sat in a pipe queue for an extra full lease since the first
            // proof; receiving buffered old proof is not a new device response.
            if (!proved) { firstHelperTime = timestamp; firstReceipt = now; }
            else if ((now - firstReceipt) - (timestamp - firstHelperTime) >= MaximumSilenceMs)
            { expired = true; throw new InvalidDataException("Buffered device proof is too old; reconnect required."); }
            sequence = next; helperTime = timestamp; proved = true; lastEvidence = now;
        }
        public void AcceptValidData(long now)
        {
            if (!started || !proved || Status(now) == 2) throw new InvalidDataException("Device liveness expired; reconnect required.");
            lastEvidence = now;
        }
    }

    // Pure decoding only: no process, native, HID, signature or loading APIs.
    // Raw DATA is validated separately by the selected travel decoder.
    public static class MonitorWire
    {
        public const int MaximumLineLength = 4096;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string ReadLine(TextReader reader, int maximum)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (maximum < 1 || maximum > MaximumLineLength) throw new ArgumentOutOfRangeException("maximum");
            StringBuilder line = new StringBuilder(); int next;
            while ((next = reader.Read()) != -1)
            {
                if (next == '\n')
                {
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                    // Exactly LF or one CRLF is framing. Extra embedded control
                    // characters must not be trimmed into a seemingly valid command.
                    foreach (char character in line.ToString())
                        if (Char.IsControl(character)) throw new InvalidDataException("Steuerzeichen in der Antwort des Tastatur-Zugriffs.");
                    return line.ToString();
                }
                if (line.Length >= maximum) throw new InvalidDataException("Zu lange Antwort des Tastatur-Zugriffs.");
                line.Append((char)next);
            }
            if (line.Length != 0) throw new InvalidDataException("Unvollständige Antwort des Tastatur-Zugriffs.");
            return null;
        }
        public static uint DecodeReady(string line)
        {
            if (line == "READY 3590") return 3590;
            if (line == "READY 3591") return 3591;
            throw new InvalidDataException("Unbekannte Tastatur-Modellkennung.");
        }
        public static byte[] DecodeData(string line)
        {
            if (line == null || line.Length != 69 || !line.StartsWith("DATA ", StringComparison.Ordinal)) throw new InvalidDataException("Unbekanntes Format der Druckwerte.");
            byte[] data = new byte[32];
            for (int i = 0; i < data.Length; i++)
                if (!Byte.TryParse(line.Substring(5 + i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out data[i])) throw new InvalidDataException("Ungültige Druckwerte.");
            return data;
        }
        public static string DecodeError(string line)
        {
            try
            {
                if (line == null || line.Length > MaximumLineLength || !line.StartsWith("ERROR ", StringComparison.Ordinal)) throw new FormatException();
                string encoded = line.Substring(6); byte[] bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded) throw new FormatException();
                string message = StrictUtf8.GetString(bytes);
                if (String.IsNullOrWhiteSpace(message) || message.Length > 1000 || message.IndexOf('\0') >= 0) throw new FormatException();
                return message;
            }
            catch (Exception ex)
            {
                if (!(ex is FormatException) && !(ex is DecoderFallbackException)) throw;
                throw new InvalidDataException("Ungültige Fehlermeldung des Tastatur-Zugriffs.", ex);
            }
        }
    }
}
