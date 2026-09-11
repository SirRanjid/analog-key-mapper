using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Tk75.Mapping
{
    /// <summary>A reusable signal preset contains no mapping, physical key or hardware calibration.</summary>
    [DataContract]
    public sealed class SignalPreset
    {
        [DataMember(IsRequired = true, Order = 0)] public int Version = 1;
        [DataMember(IsRequired = true, Order = 1)] public string Name;
        [DataMember(IsRequired = true, Order = 2)] public SignalSettings Settings = new SignalSettings();
    }

    /// <summary>
    /// Strict external schema: exactly Version, Name and Settings; all are required.
    /// JSON is limited to 65536 characters and 20 structural levels. Unknown or
    /// duplicate fields, extra documents, non-finite values, malformed settings
    /// and unsupported versions are rejected. Existing ProfileJson validation is
    /// reused internally, but exported JSON NEVER includes placeholder bindings.
    /// All returned values and snapshots are deep copies; callers serialize access
    /// to their own mutable DTOs. These functions perform no file or hardware I/O.
    /// </summary>
    public static class SignalPresetJson
    {
        public const int MaxJsonCharacters = 65536;

        private static Profile Snapshot(SignalPreset preset)
        {
            if (preset == null) throw new ArgumentNullException("preset");
            var wrapper = new Profile { Version = preset.Version, Name = preset.Name };
            wrapper.Bindings.Add(new Binding { BindingId = "preset-validation", KeyIndex = 0, Processing = preset.Settings });
            return ProfileJson.Clone(wrapper);
        }
        private static SignalPreset Unwrap(Profile profile)
        { return new SignalPreset { Version = profile.Version, Name = profile.Name, Settings = profile.Bindings[0].Processing }; }
        public static SignalPreset Clone(SignalPreset preset) { return Unwrap(Snapshot(preset)); }
        public static SignalSettings CloneSignal(SignalSettings settings)
        { return Clone(new SignalPreset { Name = "Signal", Settings = settings }).Settings; }
        public static string Serialize(SignalPreset preset)
        {
            SignalPreset snapshot = Clone(preset);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(SignalPreset)).WriteObject(stream, snapshot);
                string json = Encoding.UTF8.GetString(stream.ToArray());
                if (json.Length > MaxJsonCharacters) throw new ArgumentException("Signal-Preset ist zu gross.", "preset");
                return json;
            }
        }
        private static bool Whitespace(char value) { return value == ' ' || value == '\t' || value == '\r' || value == '\n'; }
        private static void CheckCompleteObject(string json)
        {
            int start = 0; while (start < json.Length && Whitespace(json[start])) start++;
            if (start == json.Length || json[start] != '{') throw new ArgumentException("Ein Signal-Preset muss genau ein JSON-Objekt sein.");
            int depth = 0; bool quoted = false, escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char value = json[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') quoted = false;
                    continue;
                }
                if (value == '"') { quoted = true; continue; }
                if (value == '{' || value == '[') { if (++depth > 20) throw new ArgumentException("Signal-Preset ist zu tief verschachtelt."); }
                else if (value == '}' || value == ']')
                {
                    if (--depth == 0)
                    {
                        for (int trailing = i + 1; trailing < json.Length; trailing++)
                            if (!Whitespace(json[trailing])) throw new ArgumentException("Weitere Daten hinter dem Signal-Preset.");
                        return;
                    }
                }
            }
            throw new ArgumentException("Unvollstaendiges Signal-Preset.");
        }
        private static void Type(XmlElement element, string expected)
        {
            if (element == null || element.GetAttribute("type") != expected) throw new ArgumentException("Falscher JSON-Datentyp im Signal-Preset.");
            foreach (XmlAttribute attribute in element.Attributes)
                if (attribute.Name != "type") throw new ArgumentException("Nicht unterstuetzte JSON-Metadaten im Signal-Preset.");
        }
        private static XmlElement Element(XmlDocument document, XmlElement parent, string name, string type, string text)
        {
            XmlElement element = document.CreateElement(name); element.SetAttribute("type", type);
            if (text != null) element.InnerText = text;
            parent.AppendChild(element); return element;
        }
        private static string InternalProfileJson(XmlDocument presetDocument)
        {
            XmlElement source = presetDocument.DocumentElement; Type(source, "object");
            var members = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (XmlNode child in source.ChildNodes)
            {
                var member = child as XmlElement;
                if (member == null || (member.Name != "Version" && member.Name != "Name" && member.Name != "Settings") || members.ContainsKey(member.Name))
                    throw new ArgumentException("Unbekanntes oder doppeltes Feld im Signal-Preset.");
                members.Add(member.Name, member);
            }
            if (members.Count != 3) throw new ArgumentException("Signal-Preset erfordert Version, Name und Settings.");
            Type(members["Version"], "number"); Type(members["Name"], "string"); Type(members["Settings"], "object");
            // Keep the full JSON-derived XML subtree, including duplicate and
            // unknown settings members. Re-serializing a DTO here would lose them.
            var document = new XmlDocument(); document.XmlResolver = null;
            XmlElement root = document.CreateElement("root"); root.SetAttribute("type", "object"); document.AppendChild(root);
            root.AppendChild(document.ImportNode(members["Version"], true));
            root.AppendChild(document.ImportNode(members["Name"], true));
            XmlElement array = Element(document, root, "Bindings", "array", null);
            XmlElement binding = Element(document, array, "item", "object", null);
            Element(document, binding, "BindingId", "string", "preset-validation");
            Element(document, binding, "KeyIndex", "number", "0"); Element(document, binding, "Target", "number", "0");
            XmlElement processing = Element(document, binding, "Processing", "object", null);
            foreach (XmlNode child in members["Settings"].ChildNodes) processing.AppendChild(document.ImportNode(child, true));
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, false))
                { document.WriteTo(writer); writer.Flush(); }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public static SignalPreset Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) throw new ArgumentException("Signal-Preset fehlt oder ist zu gross.", "json");
            CheckCompleteObject(json);
            try
            {
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 20, MaxStringContentLength = MaxJsonCharacters,
                    MaxArrayLength = MaxJsonCharacters, MaxBytesPerRead = 4096, MaxNameTableCharCount = MaxJsonCharacters };
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), quotas))
                {
                    var document = new XmlDocument(); document.XmlResolver = null; document.Load(reader);
                    return Unwrap(ProfileJson.Deserialize(InternalProfileJson(document)));
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception exception) { throw new ArgumentException("Ungueltiges Signal-Preset: " + exception.Message, "json", exception); }
        }
    }
}
