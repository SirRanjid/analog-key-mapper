using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Tk75.Mapping
{
    public static class ProfileJson
    {
        public const int MaxJsonCharacters = 4194304;
        private static DataContractJsonSerializer Serializer()
        { return new DataContractJsonSerializer(typeof(Profile), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 1000000 }); }
        public static string Serialize(Profile profile)
        {
            MappingValidation.RequireValid(profile);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, profile);
                string json = Encoding.UTF8.GetString(stream.ToArray());
                if (json.Length > MaxJsonCharacters) throw new ArgumentException("Profil-JSON ist zu gross.");
                return json;
            }
        }
        public static Profile Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) throw new ArgumentException("Profil-JSON fehlt oder ist zu gross.");
            CheckCompleteObject(json);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                var quotas = new XmlDictionaryReaderQuotas {
                    MaxDepth = 20, MaxStringContentLength = 262144, MaxArrayLength = MaxJsonCharacters,
                    MaxBytesPerRead = 4096, MaxNameTableCharCount = 65536
                };
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas))
                {
                    var document = new XmlDocument();
                    document.XmlResolver = null;
                    document.Load(reader);
                    CheckObject(document.DocumentElement, "profile");
                }
                using (var stream = new MemoryStream(bytes, false))
                {
                    var profile = (Profile)Serializer().ReadObject(stream);
                    MappingValidation.RequireValid(profile);
                    return profile;
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception exception)
            { throw new ArgumentException("Ungueltiges Profil-JSON: " + exception.Message, "json", exception); }
        }

        // DataContractJsonSerializer alone silently ignores unknown members and
        // may coerce string numbers. Validate the JSON representation first.
        private static bool JsonWhitespace(char value) { return value == ' ' || value == '\t' || value == '\r' || value == '\n'; }
        private static void CheckCompleteObject(string json)
        {
            int start = 0;
            while (start < json.Length && JsonWhitespace(json[start])) start++;
            if (start == json.Length || json[start] != '{') throw new ArgumentException("Ein Profil muss genau ein JSON-Objekt sein.");
            bool quoted = false, escaped = false;
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                char current = json[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') quoted = false;
                    continue;
                }
                if (current == '"') { quoted = true; continue; }
                if (current == '{' || current == '[')
                { if (++depth > 20) throw new ArgumentException("Profil-JSON ist zu tief verschachtelt."); }
                else if (current == '}' || current == ']')
                {
                    if (--depth == 0)
                    {
                        for (int trailing = i + 1; trailing < json.Length; trailing++)
                            if (!JsonWhitespace(json[trailing])) throw new ArgumentException("Weitere Daten hinter dem Profil-JSON.");
                        return;
                    }
                }
            }
            throw new ArgumentException("Unvollstaendiges Profil-JSON.");
        }
        private static void ExpectType(XmlElement element, string type)
        {
            if (element == null || element.GetAttribute("type") != type) throw new ArgumentException("Falscher JSON-Datentyp; erwartet: " + type + ".");
            foreach (XmlAttribute attribute in element.Attributes)
                if (attribute.Name != "type") throw new ArgumentException("Nicht unterstuetzte JSON-Metadaten.");
        }
        private static void Scalar(XmlElement element, string type)
        {
            ExpectType(element, type);
            foreach (XmlNode child in element.ChildNodes) if (child is XmlElement) throw new ArgumentException("Verschachtelter Wert statt Skalar.");
            if (type == "number")
            {
                double number;
                if (!double.TryParse(element.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !MappingValidation.IsFinite(number))
                    throw new ArgumentException("JSON-Zahlen muessen endlich sein.");
            }
        }
        private static void CheckArray(XmlElement array, string elementKind)
        {
            ExpectType(array, "array");
            foreach (XmlNode child in array.ChildNodes)
            {
                var element = child as XmlElement;
                if (element == null || element.LocalName != "item") throw new ArgumentException("Ungueltiger Arrayeintrag.");
                CheckObject(element, elementKind);
            }
        }
        private static void CheckKeyIndexArray(XmlElement array)
        {
            ExpectType(array, "array");
            var seen = new HashSet<int>();
            foreach (XmlNode child in array.ChildNodes)
            {
                var element = child as XmlElement;
                if (element == null || element.LocalName != "item") throw new ArgumentException("Ungültiger Tastenindex-Arrayeintrag.");
                Scalar(element, "number");
                int value;
                if (!Int32.TryParse(element.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) || value < 0 || value > 255 || !seen.Add(value))
                    throw new ArgumentException("Unterdrückte Tastenindizes müssen eindeutige ganze Zahlen in 0..255 sein.");
            }
        }
        private static void CheckObject(XmlElement obj, string kind)
        {
            ExpectType(obj, "object");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in obj.ChildNodes)
            {
                var member = child as XmlElement;
                if (member == null || !names.Add(member.LocalName)) throw new ArgumentException("Doppeltes oder ungueltiges JSON-Feld.");
                string name = member.LocalName;
                if (kind == "profile")
                {
                    if (name == "Name") Scalar(member, "string");
                    else if (name == "Version" || name == "StickShape" || name == "OpposedPolicy" || name == "Aggregation" || name == "Controller") Scalar(member, "number");
                    else if (name == "Bindings") CheckArray(member, "binding");
                    else if (name == "Inputs") CheckArray(member, "input");
                    else if (name == "LearnedInputs") CheckArray(member, "learnedInput");
                    else if (name == "Controllers") CheckArray(member, "controller");
                    else if (name == "SuppressedKeyboardKeys") CheckKeyIndexArray(member);
                    else if (name == "RgbOverrideEnabled") Scalar(member, "boolean");
                    else if (name == "ModeSwitchHotkey" || name == "EmergencyStopHotkey") CheckObject(member, "hotkey");
                    else if (name == "ControllerInputEnabled") Scalar(member, "boolean");
                    else if (name == "ModeSwitchLightingEnabled") Scalar(member, "boolean");
                    else if (name == "ModeSwitchRgbColor")
                    {
                        Scalar(member, "number"); int rgb;
                        if (!Int32.TryParse(member.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rgb) || rgb < 0 || rgb > 0xFFFFFF)
                            throw new ArgumentException("Die Farbe der Modustaste muss eine ganze RGB-Zahl in 0..16777215 sein.");
                    }
                    else if (name == "KeyboardSuppressionMode")
                    {
                        Scalar(member, "number"); int mode;
                        if (!Int32.TryParse(member.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out mode) || mode < 0 || mode > 2)
                            throw new ArgumentException("Die Tastensperre benötigt einen gültigen Modus (0, 1 oder 2).");
                    }
                    else throw new ArgumentException("Unbekanntes Profilfeld: " + name + ".");
                }
                else if (kind == "learnedInput")
                {
                    if (name == "Backend" || name == "SourceDeviceId" || name == "SourceName" || name == "ControlId") Scalar(member, "string");
                    else if ((name == "SourceKeyIndex" || name == "HatValue") && member.GetAttribute("type") == "null") Scalar(member, "null");
                    else if (name == "KeyIndex" || name == "SourceKeyIndex" || name == "Kind" || name == "Direction")
                    {
                        Scalar(member, "number"); int value;
                        if (!Int32.TryParse(member.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                            throw new ArgumentException("Learned input identifiers must be whole numbers.");
                    }
                    else if (name == "Minimum" || name == "Maximum" || name == "Rest" || name == "Active" || name == "HatValue") Scalar(member, "number");
                    else throw new ArgumentException("Unknown learned input field: " + name + ".");
                }
                else if (kind == "hotkey")
                {
                    if (name == "Enabled") Scalar(member, "boolean");
                    else if (name == "KeyCode" || name == "Modifiers")
                    {
                        Scalar(member, "number"); int value;
                        if (!Int32.TryParse(member.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                            throw new ArgumentException("Tastenkombinationen benötigen ganze Zahlen.");
                    }
                    else throw new ArgumentException("Unbekanntes Tastenkombinationsfeld: " + name + ".");
                }
                else if (kind == "binding")
                {
                    if (name == "BindingId" || name == "ControllerId") Scalar(member, "string");
                    else if (name == "KeyIndex" || name == "Target") Scalar(member, "number");
                    else if (name == "Enabled") Scalar(member, "boolean");
                    else if (name == "Processing") CheckObject(member, "settings");
                    else throw new ArgumentException("Unbekanntes Bindingfeld: " + name + ".");
                }
                else if (kind == "controller")
                {
                    if (name == "Id" || name == "Name") Scalar(member, "string");
                    else if (name == "Kind") Scalar(member, "number");
                    else if (name == "RgbColor")
                    {
                        if (member.GetAttribute("type") == "null") Scalar(member, "null");
                        else
                        {
                            Scalar(member, "number"); int rgb;
                            if (!Int32.TryParse(member.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rgb) || rgb < 0 || rgb > 0xFFFFFF)
                                throw new ArgumentException("Controllerfarbe muss eine ganze RGB-Zahl in 0..16777215 sein.");
                        }
                    }
                    else throw new ArgumentException("Unbekanntes Controllerfeld: " + name + ".");
                }
                else if (kind == "input")
                {
                    if (name == "RapidTriggerEnabled") Scalar(member, "boolean");
                    else if (name == "OppositeKeyIndex" && member.GetAttribute("type") == "null") Scalar(member, "null");
                    else if (name == "KeyIndex" || name == "ActuationPoint" || name == "PressMovement" || name == "ReleaseMovement" || name == "OppositeKeyIndex" || name == "OppositePolicy") Scalar(member, "number");
                    else throw new ArgumentException("Unbekanntes Tasteinstellungsfeld: " + name + ".");
                }
                else if (kind == "settings")
                {
                    if (name == "CustomPoints") CheckArray(member, "point");
                    else if (name == "TopDeadzone" || name == "BottomDeadzone" || name == "Curve" || name == "Exponent" ||
                        name == "MinOutput" || name == "MaxOutput" || name == "Scale" || name == "Hysteresis" ||
                        name == "SmoothingTimeConstant" || name == "ButtonThreshold" || name == "OutputDeadzone") Scalar(member, "number");
                    else throw new ArgumentException("Unbekanntes Verarbeitungsfeld: " + name + ".");
                }
                else
                {
                    if (name == "Tangent" && member.GetAttribute("type") == "null") Scalar(member, "null");
                    else if (name == "X" || name == "Y" || name == "Tangent") Scalar(member, "number");
                    else throw new ArgumentException("Unbekanntes Kurvenpunktfeld: " + name + ".");
                }
            }
        }
        public static Profile Clone(Profile profile) { return Deserialize(Serialize(profile)); }
    }

    public static class ProfileEditing
    {
        // The source and caller's preset remain untouched; validation is atomic.
        public static Profile ApplySettings(Profile source, IEnumerable<string> bindingIds, SignalSettings preset)
        {
            Profile result = ProfileJson.Clone(source);
            if (bindingIds == null) throw new ArgumentNullException("bindingIds");
            var errors = MappingValidation.ValidateSettings(preset);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.ToArray()), "preset");
            var selected = new HashSet<string>(bindingIds, StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in result.Bindings)
            {
                if (!selected.Contains(binding.BindingId)) continue;
                // Reuse the same validated serializer to deep-copy curve points.
                var wrapper = new Profile();
                wrapper.Bindings.Add(new Binding { BindingId = "preset", KeyIndex = 0, Processing = preset });
                binding.Processing = ProfileJson.Clone(wrapper).Bindings[0].Processing;
                found.Add(binding.BindingId);
            }
            if (found.Count != selected.Count) throw new ArgumentException("Mindestens eine ausgewaehlte BindingId fehlt.", "bindingIds");
            MappingValidation.RequireValid(result);
            return result;
        }
    }

    public sealed class EditHistory
    {
        private readonly List<string> snapshots = new List<string>();
        private readonly int capacity;
        private int position;
        public EditHistory(Profile initial, int capacity)
        {
            if (capacity < 2 || capacity > 1000) throw new ArgumentOutOfRangeException("capacity", "2..1000 Profilsnapshots erlaubt.");
            this.capacity = capacity; snapshots.Add(ProfileJson.Serialize(initial));
        }
        public EditHistory(Profile initial) : this(initial, 100) { }
        public Profile Current { get { return ProfileJson.Deserialize(snapshots[position]); } }
        // The immutable stored snapshot is a cheap identity for read-only UI
        // caches. Current deliberately continues to return an editable copy.
        internal object SnapshotToken { get { return snapshots[position]; } }
        public bool CanUndo { get { return position > 0; } }
        public bool CanRedo { get { return position < snapshots.Count - 1; } }
        public void Commit(Profile next)
        {
            string snapshot = ProfileJson.Serialize(next);
            if (snapshot == snapshots[position]) return;
            if (CanRedo) snapshots.RemoveRange(position + 1, snapshots.Count - position - 1);
            snapshots.Add(snapshot);
            if (snapshots.Count > capacity) snapshots.RemoveAt(0);
            position = snapshots.Count - 1;
        }
        public bool Undo() { if (!CanUndo) return false; position--; return true; }
        public bool Redo() { if (!CanRedo) return false; position++; return true; }
    }
}
