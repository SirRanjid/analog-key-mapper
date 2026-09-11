using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed class CalibrationEntry
    {
        public int KeyIndex;
        public double Rest = double.NaN, Bottom = double.NaN;
        public double? UsableMin, UsableMax, MeasuredTravel;
        [ScriptIgnore] public double SensorOffset { get { return Rest; } }
        public Calibration Value() { return new Calibration(Rest, Bottom) { UsableMin = UsableMin, UsableMax = UsableMax, MeasuredTravel = MeasuredTravel }; }
    }
    public sealed class CalibrationDocument
    {
        public int SchemaVersion = 1;
        public string DeviceIdentity;
        public string ProtocolFingerprint;
        public List<CalibrationEntry> Entries = new List<CalibrationEntry>();
    }
    public sealed class FocusRule { public string Executable; public string ProfileFile; }
    public sealed class FocusSettings
    {
        public int SchemaVersion = 1;
        public bool Enabled;
        public string DefaultProfileFile;
        public List<FocusRule> Rules = new List<FocusRule>();
    }
    public sealed class WorkspaceStore
    {
        const int MaximumDocumentBytes = 1048576;
        public readonly string Root;
        public WorkspaceStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, "profiles")); }
        public string[] Profiles() { return Directory.GetFiles(Path.Combine(Root, "profiles"), "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(); }
        public string NewProfilePath() { return Path.Combine(Root, "profiles", Guid.NewGuid().ToString("N") + ".json"); }
        public static void WriteAtomic(string path, string content)
        {
            string full = Path.GetFullPath(path), temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); file.Flush(true); }
                if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static string Hash(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        static string RequireHash(string value)
        {
            if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Eine gueltige SHA-256-Kennung ist erforderlich.");
            return value.ToLowerInvariant();
        }
        // Without a serial this is deliberately conservative: a changed Windows path
        // requires a new calibration. A shared VID/PID is not a physical identity.
        public static string Identity(CollectionInfo device, string fingerprint)
        {
            if (device == null) throw new ArgumentNullException("device");
            fingerprint = RequireHash(fingerprint);
            if (string.IsNullOrWhiteSpace(device.serial) && string.IsNullOrWhiteSpace(device.devicePath)) throw new InvalidDataException("Ohne Seriennummer oder Windows-Geraetepfad ist keine physische Kalibrierungskennung moeglich.");
            return Hash(fingerprint + "\n" + (!string.IsNullOrWhiteSpace(device.serial) ? "serial:" + device.serial : "windows-path:" + device.devicePath));
        }
        public string KeyMapPath(string fingerprint) { return Path.Combine(Root, "keys-" + RequireHash(fingerprint) + ".json"); }
        public string CalibrationPath(string identity) { return Path.Combine(Root, "calibration-" + RequireHash(identity) + ".json"); }

        static string ReadDocument(string path, out XmlElement root)
        {
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaximumDocumentBytes) throw new InvalidDataException("Konfigurationsdatei ist zu gross.");
                bytes = new byte[(int)file.Length]; int offset = 0;
                while (offset < bytes.Length) { int count = file.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            }
            try
            {
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 16, MaxArrayLength = MaximumDocumentBytes, MaxStringContentLength = MaximumDocumentBytes, MaxNameTableCharCount = 16384 };
                var tree = new XmlDocument { XmlResolver = null };
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas)) tree.Load(reader);
                root = tree.DocumentElement;
                using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)) return reader.ReadToEnd();
            }
            catch (XmlException ex) { throw new InvalidDataException("Ungueltiges Konfigurations-JSON.", ex); }
        }

        static void Type(XmlElement element, string type)
        {
            if (element == null || element.GetAttribute("type") != type || element.Attributes.Count != 1) throw new InvalidDataException("Falscher JSON-Feldtyp oder unbekannte Metadaten.");
        }
        static void Members(XmlElement element, string[] required, string[] optional)
        {
            Type(element, "object");
            var allowed = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in element.ChildNodes)
            {
                var member = node as XmlElement;
                if (member == null || !allowed.Contains(member.Name) || !seen.Add(member.Name)) throw new InvalidDataException("Unbekanntes oder doppeltes JSON-Feld.");
            }
            foreach (string member in required) if (!seen.Contains(member)) throw new InvalidDataException("Erforderliches JSON-Feld fehlt: " + member);
        }
        static double Number(XmlElement element, bool integral)
        {
            Type(element, "number");
            if (integral)
            {
                long integer;
                if (!long.TryParse(element.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)) throw new InvalidDataException("Ein ganzzahliges JSON-Feld ist erforderlich.");
                return integer;
            }
            double value;
            if (!double.TryParse(element.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !MappingValidation.IsFinite(value))
                throw new InvalidDataException("Ungueltiges numerisches JSON-Feld.");
            return value;
        }
        static void OptionalNumber(XmlElement element)
        { if (element != null) { if (element.GetAttribute("type") == "null") Type(element, "null"); else Number(element, false); } }
        static T Decode<T>(string json)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = MaximumDocumentBytes, RecursionLimit = 16 }.Deserialize<T>(json); }
            catch (ArgumentException ex) { throw new InvalidDataException("Ungueltiges Konfigurations-JSON.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Ungueltiges Konfigurations-JSON.", ex); }
            catch (OverflowException ex) { throw new InvalidDataException("Numerisches Feld liegt ausserhalb des erlaubten Bereichs.", ex); }
            catch (FormatException ex) { throw new InvalidDataException("Ungueltiges numerisches Feld.", ex); }
        }
        public CalibrationDocument LoadCalibration(string identity, string fingerprint)
        {
            identity = RequireHash(identity); fingerprint = RequireHash(fingerprint);
            string path = CalibrationPath(identity);
            if (!File.Exists(path)) return new CalibrationDocument { DeviceIdentity = identity, ProtocolFingerprint = fingerprint };
            XmlElement root; string json = ReadDocument(path, out root);
            Members(root, new[] { "SchemaVersion", "DeviceIdentity", "ProtocolFingerprint", "Entries" }, new string[0]);
            if (Number(root["SchemaVersion"], true) != 1) throw new InvalidDataException("Unbekannte Kalibrierungsversion.");
            Type(root["DeviceIdentity"], "string"); Type(root["ProtocolFingerprint"], "string"); Type(root["Entries"], "array");
            if (root["Entries"].ChildNodes.Count > 256) throw new InvalidDataException("Zu viele Tastenkalibrierungen.");
            foreach (XmlNode node in root["Entries"].ChildNodes)
            {
                var entry = node as XmlElement;
                Members(entry, new[] { "KeyIndex", "Rest", "Bottom" }, new[] { "UsableMin", "UsableMax", "MeasuredTravel", "SensorOffset" });
                double index = Number(entry["KeyIndex"], true);
                if (index < 0 || index > 255) throw new InvalidDataException("Tastenindex ausserhalb 0..255.");
                double rest = Number(entry["Rest"], false); Number(entry["Bottom"], false);
                OptionalNumber(entry["UsableMin"]); OptionalNumber(entry["UsableMax"]); OptionalNumber(entry["MeasuredTravel"]);
                // Older files serialized this derived getter; accept only consistency.
                if (entry["SensorOffset"] != null && Number(entry["SensorOffset"], false) != rest) throw new InvalidDataException("SensorOffset muss dem Ruhewert entsprechen.");
            }
            var value = Decode<CalibrationDocument>(json);
            Validate(value, identity, fingerprint); return value;
        }
        public void SaveCalibration(CalibrationDocument value)
        {
            if (value == null) throw new InvalidDataException("Kalibrierung fehlt.");
            Validate(value, value.DeviceIdentity, value.ProtocolFingerprint);
            WriteAtomic(CalibrationPath(value.DeviceIdentity), new JavaScriptSerializer().Serialize(value));
        }
        public static void Validate(CalibrationDocument value, string identity, string fingerprint)
        {
            identity = RequireHash(identity); fingerprint = RequireHash(fingerprint);
            if (value == null || value.SchemaVersion != 1 || !string.Equals(value.DeviceIdentity, identity, StringComparison.OrdinalIgnoreCase) || !string.Equals(value.ProtocolFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) || value.Entries == null || value.Entries.Count > 256)
                throw new InvalidDataException("Kalibrierung passt nicht zu diesem Geraet und Protokoll.");
            var seen = new HashSet<int>();
            foreach (CalibrationEntry item in value.Entries)
                if (item == null || item.KeyIndex < 0 || item.KeyIndex > 255 || !seen.Add(item.KeyIndex) || MappingValidation.ValidateCalibration(item.Value()).Count != 0)
                    throw new InvalidDataException("Ungueltige oder doppelte Tastenkalibrierung.");
        }
        public FocusSettings LoadFocus()
        {
            string path = Path.Combine(Root, "focus.json");
            if (!File.Exists(path)) return new FocusSettings();
            XmlElement root; string json = ReadDocument(path, out root);
            Members(root, new[] { "SchemaVersion", "Enabled", "Rules" }, new[] { "DefaultProfileFile" });
            if (Number(root["SchemaVersion"], true) != 1) throw new InvalidDataException("Unbekannte Version der Programmregeln.");
            Type(root["Enabled"], "boolean"); Type(root["Rules"], "array");
            if (root["Rules"].ChildNodes.Count > 256) throw new InvalidDataException("Zu viele Programmregeln.");
            if (root["DefaultProfileFile"] != null) Type(root["DefaultProfileFile"], root["DefaultProfileFile"].GetAttribute("type") == "null" ? "null" : "string");
            foreach (XmlNode node in root["Rules"].ChildNodes)
            {
                var rule = node as XmlElement; Members(rule, new[] { "Executable", "ProfileFile" }, new string[0]);
                Type(rule["Executable"], "string"); Type(rule["ProfileFile"], "string");
            }
            var settings = Decode<FocusSettings>(json); ValidateFocus(settings); return settings;
        }
        public void SaveFocus(FocusSettings settings) { ValidateFocus(settings); WriteAtomic(Path.Combine(Root, "focus.json"), new JavaScriptSerializer().Serialize(settings)); }
        public void ValidateFocus(FocusSettings settings)
        {
            if (settings == null || settings.SchemaVersion != 1 || settings.Rules == null || settings.Rules.Count > 256) throw new InvalidDataException("Ungueltige Programmregeln.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FocusRule rule in settings.Rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.Executable) || rule.Executable.Length > 512 || rule.Executable != rule.Executable.Trim() || rule.Executable.Any(char.IsControl) || !seen.Add(rule.Executable)) throw new InvalidDataException("Programmregeln muessen eindeutig sein.");
                string executable = rule.Executable;
                if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executable.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0 || executable.Split('\\', '/').Any(part => part == "." || part == ".."))
                    throw new InvalidDataException("Ein klarer EXE-Name oder absoluter EXE-Pfad ist erforderlich.");
                if (Path.IsPathRooted(executable))
                {
                    string pathRoot = Path.GetPathRoot(executable);
                    if ((!executable.StartsWith("\\\\", StringComparison.Ordinal) && (pathRoot == null || pathRoot.Length != 3)) || executable.Substring(pathRoot.Length).IndexOf(':') >= 0 || Path.GetFileName(executable).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new InvalidDataException("Relative Laufwerkspfade sind nicht erlaubt.");
                }
                else if (Path.GetFileName(executable) != executable || executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Relative Programmpfade sind nicht erlaubt.");
                RequireLocalProfile(rule.ProfileFile);
            }
            if (settings.DefaultProfileFile != null) RequireLocalProfile(settings.DefaultProfileFile);
        }
        public string RequireLocalProfile(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || file.Length > 255 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(file) != file || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Nur lokale Profilnamen sind erlaubt.");
            string stem = file.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                throw new InvalidDataException("Reservierte Windows-Geraetenamen sind keine Profile.");
            return Path.Combine(Root, "profiles", file);
        }
        public void Event(string message)
        {
            // No raw values, reports or foreground titles in normal logs.
            try
            {
                message = message ?? string.Empty;
                if (message.Length > 4096) message = message.Substring(0, 4096);
                string path = Path.Combine(Root, "events.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2097152) File.Delete(path);
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
