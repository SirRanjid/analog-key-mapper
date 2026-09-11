using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Tk75.Diagnostics
{
    [DataContract]
    public sealed class KeyLabelEntry
    {
        [DataMember(Name = "keyIndex", IsRequired = true, Order = 0)] public int KeyIndex { get; set; }
        [DataMember(Name = "label", IsRequired = true, Order = 1)] public string Label { get; set; }
    }

    // Labels describe a logical report protocol. They are never hardware calibration.
    [DataContract]
    public sealed class KeyMapDocument
    {
        [DataMember(Name = "schemaVersion", IsRequired = true, Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Name = "protocolFingerprint", IsRequired = true, Order = 1)] public string ProtocolFingerprint { get; set; }
        [DataMember(Name = "entries", IsRequired = true, Order = 2)] public List<KeyLabelEntry> Entries { get; set; }

        public string GetLabel(int index)
        {
            if ((uint)index >= 256) throw new ArgumentOutOfRangeException("index");
            if (Entries != null)
                foreach (KeyLabelEntry entry in Entries)
                    if (entry != null && entry.KeyIndex == index) return entry.Label;
            return "Index " + index.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class KeyMapStore
    {
        const int MaximumFileBytes = 262144;
        static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(KeyMapDocument)); }

        static KeyMapDocument CheckedCopy(KeyMapDocument document)
        {
            if (document == null || document.SchemaVersion != 1) throw new InvalidDataException("Unsupported key-map schema; expected version 1.");
            string fingerprint = document.ProtocolFingerprint;
            if (fingerprint == null || fingerprint.Length != 64) throw new InvalidDataException("Protocol fingerprint must be a SHA-256 hex string.");
            foreach (char digit in fingerprint)
                if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f') || (digit >= 'A' && digit <= 'F')))
                    throw new InvalidDataException("Protocol fingerprint is not hexadecimal.");
            if (document.Entries == null || document.Entries.Count > 256) throw new InvalidDataException("Key map must contain an entries list with at most 256 entries.");
            KeyMapDocument copy = new KeyMapDocument { SchemaVersion = 1, ProtocolFingerprint = fingerprint.ToLowerInvariant(), Entries = new List<KeyLabelEntry>() };
            HashSet<int> indices = new HashSet<int>();
            foreach (KeyLabelEntry entry in document.Entries)
            {
                if (entry == null || (uint)entry.KeyIndex >= 256) throw new InvalidDataException("Key index must be 0..255.");
                if (!indices.Add(entry.KeyIndex)) throw new InvalidDataException("Duplicate key index.");
                ValidateLabel(entry.Label);
                copy.Entries.Add(new KeyLabelEntry { KeyIndex = entry.KeyIndex, Label = entry.Label });
            }
            copy.Entries.Sort(delegate(KeyLabelEntry a, KeyLabelEntry b) { return a.KeyIndex.CompareTo(b.KeyIndex); });
            return copy;
        }

        static void ValidateLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 80) throw new InvalidDataException("Key label must contain 1..80 characters and cannot be blank.");
            foreach (char character in label) if (char.IsControl(character)) throw new InvalidDataException("Key label contains a control character.");
        }

        public static KeyMapDocument Create(string fingerprint)
        {
            return CheckedCopy(new KeyMapDocument { SchemaVersion = 1, ProtocolFingerprint = fingerprint, Entries = new List<KeyLabelEntry>() });
        }

        // Return a deep copy so edits do not change the caller's previous document.
        public static KeyMapDocument SetLabel(KeyMapDocument document, int index, string label)
        {
            if ((uint)index >= 256) throw new ArgumentOutOfRangeException("index");
            ValidateLabel(label);
            KeyMapDocument copy = CheckedCopy(document);
            copy.Entries.RemoveAll(delegate(KeyLabelEntry entry) { return entry.KeyIndex == index; });
            copy.Entries.Add(new KeyLabelEntry { KeyIndex = index, Label = label });
            return CheckedCopy(copy);
        }

        static XmlElement RequireMembers(XmlElement element, string expectedType, string[] members)
        {
            RequireType(element, expectedType);
            HashSet<string> allowed = new HashSet<string>(members, StringComparer.Ordinal);
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in element.ChildNodes)
            {
                XmlElement child = node as XmlElement;
                if (child == null || !allowed.Contains(child.Name) || !found.Add(child.Name))
                    throw new InvalidDataException("Unknown or duplicate JSON member in key map.");
            }
            if (found.Count != allowed.Count) throw new InvalidDataException("Missing required JSON member in key map.");
            return element;
        }

        static void RequireType(XmlElement element, string type)
        {
            if (element == null || element.GetAttribute("type") != type || element.Attributes.Count != 1)
                throw new InvalidDataException("Wrong JSON field type or unexpected metadata in key map.");
        }

        public static KeyMapDocument Load(string path)
        {
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Key-map file exceeds the size limit.");
                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
            }
            try
            {
                // DCS alone ignores unknown fields; validate its JSON XML view first.
                XmlDocument tree = new XmlDocument(); tree.XmlResolver = null;
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max)) tree.Load(reader);
                XmlElement root = RequireMembers(tree.DocumentElement, "object", new[] { "schemaVersion", "protocolFingerprint", "entries" });
                RequireType(root["schemaVersion"], "number"); RequireType(root["protocolFingerprint"], "string"); RequireType(root["entries"], "array");
                foreach (XmlNode node in root["entries"].ChildNodes)
                {
                    XmlElement entry = RequireMembers(node as XmlElement, "object", new[] { "keyIndex", "label" });
                    RequireType(entry["keyIndex"], "number"); RequireType(entry["label"], "string");
                }
                using (MemoryStream stream = new MemoryStream(bytes)) return CheckedCopy((KeyMapDocument)Serializer().ReadObject(stream));
            }
            catch (SerializationException ex) { throw new InvalidDataException("Invalid key-map JSON.", ex); }
            catch (XmlException ex) { throw new InvalidDataException("Invalid key-map JSON.", ex); }
        }

        public static void Save(string path, KeyMapDocument document)
        {
            KeyMapDocument copy = CheckedCopy(document);
            string destination = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            bool exists = File.Exists(destination);
            if (exists && !string.Equals(Load(destination).ProtocolFingerprint, copy.ProtocolFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("Existing map belongs to another protocol fingerprint; it will not be overwritten.");
            string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { Serializer().WriteObject(stream, copy); stream.Flush(true); }
                if (exists) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static class ProtocolFingerprint
    {
        static readonly string[] CapabilityFields = new[] { "reportType", "kind", "usagePage", "reportId", "linkCollection", "isRange", "isAbsolute", "usageMinimum", "usageMaximum", "bitSize", "reportCount", "logicalMinimum", "logicalMaximum", "physicalMinimum", "physicalMaximum", "unitsExponent", "units" };
        static void Add(StringBuilder text, string name, object value)
        {
            text.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name).Append('=');
            if (value == null) { text.Append("-1:;"); return; }
            string encoded = Convert.ToString(value, CultureInfo.InvariantCulture);
            text.Append(encoded.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(encoded).Append(';');
        }

        // Logical protocol identity only: no path, serial, container or calibration.
        // A firmware/version/capability change intentionally invalidates an old map.
        public static string Calculate(CollectionInfo device, string protocolId)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (string.IsNullOrWhiteSpace(protocolId)) throw new ArgumentException("Protocol ID is required.", "protocolId");
            StringBuilder text = new StringBuilder();
            Add(text, "fingerprintSchema", 1); Add(text, "protocolId", protocolId);
            Add(text, "vid", device.vendorId); Add(text, "pid", device.productId); Add(text, "product", device.product);
            Add(text, "bcdDevice", device.version); Add(text, "usagePage", device.usagePage); Add(text, "usage", device.usage);
            Add(text, "inputLength", device.inputReportLength); Add(text, "outputLength", device.outputReportLength); Add(text, "featureLength", device.featureReportLength);
            List<string> capabilities = new List<string>();
            if (device.reportCapabilities != null)
                foreach (Dictionary<string, object> capability in device.reportCapabilities)
                {
                    if (capability == null) throw new ArgumentException("Null report capability.", "device");
                    StringBuilder entry = new StringBuilder();
                    foreach (string field in CapabilityFields)
                    {
                        object value;
                        Add(entry, field, capability.TryGetValue(field, out value) ? value : null);
                    }
                    capabilities.Add(entry.ToString());
                }
            capabilities.Sort(StringComparer.Ordinal);
            foreach (string capability in capabilities) Add(text, "capability", capability);
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
