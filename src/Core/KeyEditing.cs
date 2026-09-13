using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tk75.Mapping
{
    [Flags]
    public enum InputActivationFields { None = 0, RapidTrigger = 1, Actuation = 2, Press = 4, Release = 8, All = 15 }

    public static class KeyInputEditing
    {
        private static List<int> SelectedKeys(IEnumerable<int> keyIndices)
        {
            if (keyIndices == null) throw new ArgumentNullException("keyIndices");
            var selected = new List<int>(); var unique = new HashSet<int>();
            foreach (int key in keyIndices)
            {
                if (key < 0 || key > 255 || !unique.Add(key)) throw new ArgumentException("Ungültige oder doppelte Tasten-Auswahl.", "keyIndices");
                selected.Add(key);
            }
            if (selected.Count == 0) throw new ArgumentException("Mindestens eine Taste auswählen.", "keyIndices");
            return selected;
        }
        private static KeyInputSettings FindOrAdd(Profile profile, int keyIndex)
        {
            foreach (KeyInputSettings input in profile.Inputs) if (input.KeyIndex == keyIndex) return input;
            var created = new KeyInputSettings { KeyIndex = keyIndex }; profile.Inputs.Add(created); return created;
        }
        public static KeyInputSettings Clone(KeyInputSettings value)
        {
            var errors = MappingValidation.ValidateInputSettings(value);
            if (errors.Count != 0) throw new ArgumentException(String.Join(" ", errors.ToArray()), "value");
            return new KeyInputSettings { KeyIndex = value.KeyIndex, RapidTriggerEnabled = value.RapidTriggerEnabled,
                ActuationPoint = value.ActuationPoint, PressMovement = value.PressMovement, ReleaseMovement = value.ReleaseMovement,
                OppositeKeyIndex = value.OppositeKeyIndex, OppositePolicy = value.OppositePolicy };
        }
        public static KeyInputSettings GetOrDefault(Profile profile, int keyIndex)
        {
            MappingValidation.RequireValid(profile);
            if (keyIndex < 0 || keyIndex > 255) throw new ArgumentOutOfRangeException("keyIndex");
            foreach (KeyInputSettings input in profile.Inputs) if (input.KeyIndex == keyIndex) return Clone(input);
            return new KeyInputSettings { KeyIndex = keyIndex };
        }
        private static void Unpair(Profile profile, int keyIndex)
        {
            foreach (KeyInputSettings input in profile.Inputs)
                if (input.KeyIndex == keyIndex || input.OppositeKeyIndex == keyIndex)
                { input.OppositeKeyIndex = null; input.OppositePolicy = InputOpposedPolicy.Neutral; }
        }
        public static Profile Configure(Profile profile, KeyInputSettings settings)
        {
            KeyInputSettings changed = Clone(settings);
            Profile result = ProfileJson.Clone(profile);
            Unpair(result, changed.KeyIndex);
            if (changed.OppositeKeyIndex.HasValue)
            {
                int otherKey = changed.OppositeKeyIndex.Value;
                Unpair(result, otherKey);
                KeyInputSettings other = null;
                foreach (KeyInputSettings input in result.Inputs) if (input.KeyIndex == otherKey) { other = input; break; }
                if (other == null) { other = new KeyInputSettings { KeyIndex = otherKey }; result.Inputs.Add(other); }
                other.OppositeKeyIndex = changed.KeyIndex; other.OppositePolicy = changed.OppositePolicy;
            }
            result.Inputs.RemoveAll(delegate(KeyInputSettings input) { return input.KeyIndex == changed.KeyIndex; });
            result.Inputs.Add(changed);
            MappingValidation.RequireValid(result);
            return result;
        }
        public static Profile Remove(Profile profile, int keyIndex)
        { return Remove(profile, new[] { keyIndex }); }

        public static Profile Remove(Profile profile, IEnumerable<int> keyIndices)
        {
            var selected = new HashSet<int>(SelectedKeys(keyIndices));
            Profile result = ProfileJson.Clone(profile);
            foreach (KeyInputSettings input in result.Inputs)
                if (input.OppositeKeyIndex.HasValue && selected.Contains(input.OppositeKeyIndex.Value))
                { input.OppositeKeyIndex = null; input.OppositePolicy = InputOpposedPolicy.Neutral; }
            result.Inputs.RemoveAll(delegate(KeyInputSettings input) { return selected.Contains(input.KeyIndex); });
            MappingValidation.RequireValid(result);
            return result;
        }
        // Copy the physical activation behavior while retaining each destination's
        // own opposite partner. Blindly copying partner indices could create self-pairs.
        public static Profile ApplyActivation(Profile profile, IEnumerable<int> keyIndices, KeyInputSettings source)
        { return ApplyActivation(profile, keyIndices, source, InputActivationFields.All); }

        public static Profile ApplyActivation(Profile profile, IEnumerable<int> keyIndices, KeyInputSettings source, InputActivationFields fields)
        {
            if (source == null) throw new ArgumentNullException("source");
            if ((fields & ~InputActivationFields.All) != 0) throw new ArgumentOutOfRangeException("fields");
            List<int> selected = SelectedKeys(keyIndices);
            // Mixed/unselected fields and pairing metadata are not part of the
            // edit. Validate only the activation values that will be copied.
            var copied = new KeyInputSettings(); CopyActivation(source, copied, fields); Clone(copied);
            Profile result = ProfileJson.Clone(profile);
            if (fields == InputActivationFields.None) return result;
            foreach (int key in selected)
                CopyActivation(copied, FindOrAdd(result, key), fields);
            MappingValidation.RequireValid(result);
            return result;
        }
        private static void CopyActivation(KeyInputSettings source, KeyInputSettings target, InputActivationFields fields)
        {
            if ((fields & InputActivationFields.RapidTrigger) != 0) target.RapidTriggerEnabled = source.RapidTriggerEnabled;
            if ((fields & InputActivationFields.Actuation) != 0) target.ActuationPoint = source.ActuationPoint;
            target.PressMovement = target.ActuationPoint;
            if ((fields & InputActivationFields.Release) != 0) target.ReleaseMovement = source.ReleaseMovement;
        }
        public static Profile SetPairing(Profile profile, IEnumerable<int> keyIndices, int? opposite, InputOpposedPolicy policy)
        {
            List<int> selected = SelectedKeys(keyIndices);
            if (selected.Count > 2) throw new ArgumentException("Für Gegentasten genau eine oder zwei Tasten auswählen.", "keyIndices");
            if (!Enum.IsDefined(typeof(InputOpposedPolicy), policy)) throw new ArgumentOutOfRangeException("policy");
            if (opposite.HasValue && (opposite.Value < 0 || opposite.Value > 255 || opposite.Value == selected[0] ||
                (selected.Count == 2 && opposite.Value != selected[1])))
                throw new ArgumentException("Die Gegentaste muss eine andere Taste sein; bei zwei ausgewählten Tasten bilden diese das Paar.", "opposite");
            Profile result = ProfileJson.Clone(profile);
            foreach (int key in selected) Unpair(result, key);
            if (opposite.HasValue)
            {
                Unpair(result, opposite.Value);
                KeyInputSettings first = FindOrAdd(result, selected[0]), second = FindOrAdd(result, opposite.Value);
                first.OppositeKeyIndex = second.KeyIndex; first.OppositePolicy = policy;
                second.OppositeKeyIndex = first.KeyIndex; second.OppositePolicy = policy;
            }
            MappingValidation.RequireValid(result);
            return result;
        }
    }

    public enum CopyPart { All, Deadzones, Curve, Filters, OutputRange, Mapping, AllExceptMapping }

    /// <summary>
    /// Pure, atomic profile edits. Input profiles, clipboard bindings and values
    /// are never mutated. Returned profiles are ready for a single EditHistory.Commit.
    /// Clipboard order is the source key's binding order in Profile.Bindings.
    /// All replaces each destination's bindings, including Enabled and processing.
    /// Mapping replaces Targets and Enabled with fresh IDs: equal binding counts
    /// preserve destination processing position by position; differing counts use
    /// the first existing destination binding's processing for all new bindings.
    /// Only All may create bindings on an unmapped destination. Every other mode
    /// requires an existing binding on EVERY selected key, or the entire edit fails.
    /// AllExceptMapping copies the FIRST source binding's processing AND Enabled
    /// to all existing destination bindings, preserving their IDs and Targets.
    /// Partial groups also use the first source binding, but preserve Enabled.
    /// Duplicate, empty, missing or out-of-range selections are errors, never a
    /// partially applied operation. No calibration, hardware or runtime state here.
    /// </summary>
    public static class KeyEditing
    {
        private static readonly HashSet<string> NumberProperties = new HashSet<string>(StringComparer.Ordinal) {
            "TopDeadzone", "BottomDeadzone", "Exponent", "MinOutput", "MaxOutput", "Scale",
            "OutputDeadzone", "Hysteresis", "SmoothingTimeConstant", "ButtonThreshold"
        };

        private static void RequireProperty(string property)
        {
            if (property == null || (!NumberProperties.Contains(property) && property != "Curve" &&
                property != "CustomPoints" && property != "Enabled" && property != "Target"))
                throw new ArgumentException("Nicht bearbeitbare Eigenschaft: " + property + ".", "property");
        }
        private static void RequireKey(int key)
        { if (key < 0 || key > 255) throw new ArgumentOutOfRangeException("keyIndex", "Tastenindex muss in 0..255 liegen."); }
        private static List<int> Keys(IEnumerable<int> selection)
        {
            if (selection == null) throw new ArgumentNullException("keyIndices");
            var result = new List<int>(); var seen = new HashSet<int>();
            foreach (int key in selection)
            {
                RequireKey(key);
                if (!seen.Add(key)) throw new ArgumentException("Eine Taste ist mehrfach ausgewaehlt.", "keyIndices");
                result.Add(key);
                if (result.Count > 256) throw new ArgumentException("Zu viele ausgewaehlte Tasten.", "keyIndices");
            }
            if (result.Count == 0) throw new ArgumentException("Mindestens eine Zieltaste auswaehlen.", "keyIndices");
            return result;
        }
        private static List<Binding> BindingsFor(Profile profile, int key)
        { return BindingsFor(profile, key, null); }
        private static List<Binding> BindingsFor(Profile profile, int key, string controllerId)
        {
            var result = new List<Binding>();
            foreach (Binding binding in profile.Bindings)
                if (binding.KeyIndex == key && (controllerId == null || binding.ControllerId == controllerId)) result.Add(binding);
            return result;
        }
        private static void RequireController(Profile profile, string controllerId)
        {
            if (controllerId == null) return;
            if (profile.Controllers.Count == 0 && controllerId == "main") return;
            foreach (ControllerDefinition controller in profile.Controllers) if (controller.Id == controllerId) return;
            throw new ArgumentException("Der ausgewählte Controller fehlt.", "controllerId");
        }
        private static List<Binding> SelectedBindings(Profile profile, IEnumerable<string> ids)
        {
            if (ids == null) throw new ArgumentNullException("bindingIds");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id) || !selected.Add(id)) throw new ArgumentException("Leere oder doppelte BindingId ausgewaehlt.", "bindingIds");
                if (selected.Count > 4096) throw new ArgumentException("Zu viele ausgewaehlte Bindings.", "bindingIds");
            }
            if (selected.Count == 0) throw new ArgumentException("Mindestens ein Binding auswaehlen.", "bindingIds");
            var result = new List<Binding>();
            foreach (Binding binding in profile.Bindings) if (selected.Contains(binding.BindingId)) result.Add(binding);
            if (result.Count != selected.Count) throw new ArgumentException("Mindestens eine ausgewaehlte BindingId fehlt.", "bindingIds");
            return result;
        }
        private static List<CurvePoint> ClonePoints(IEnumerable<CurvePoint> points)
        {
            if (points == null) throw new ArgumentException("Kurvenpunkte fehlen.", "value");
            var result = new List<CurvePoint>();
            foreach (CurvePoint point in points)
            {
                if (point == null) throw new ArgumentException("Leerer Kurvenpunkt.", "value");
                result.Add(new CurvePoint(point.X, point.Y, point.Tangent));
                if (result.Count > 64) throw new ArgumentException("Maximal 64 Kurvenpunkte erlaubt.", "value");
            }
            return result;
        }
        private static SignalSettings CloneSignal(SignalSettings source)
        {
            return new SignalSettings {
                TopDeadzone = source.TopDeadzone, BottomDeadzone = source.BottomDeadzone,
                Curve = source.Curve, Exponent = source.Exponent, CustomPoints = ClonePoints(source.CustomPoints),
                MinOutput = source.MinOutput, MaxOutput = source.MaxOutput, Scale = source.Scale,
                OutputDeadzone = source.OutputDeadzone, Hysteresis = source.Hysteresis,
                SmoothingTimeConstant = source.SmoothingTimeConstant, ButtonThreshold = source.ButtonThreshold
            };
        }
        private static List<Binding> CloneClipboard(List<Binding> source)
        {
            if (source == null || source.Count == 0) throw new ArgumentException("Zuerst eine Taste mit Zuordnungen kopieren.", "copied");
            var wrapper = new Profile { Bindings = source };
            var routes = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in source)
                if (binding != null && routes.Add(binding.ControllerId))
                    wrapper.Controllers.Add(new ControllerDefinition { Id = binding.ControllerId, Name = "Controller " + routes.Count, Kind = ControllerKind.Xbox360 });
            MappingValidation.RequireValid(wrapper);
            int key = source[0].KeyIndex;
            foreach (Binding binding in source) if (binding.KeyIndex != key) throw new ArgumentException("Die Zwischenablage muss genau eine physische Quelltaste enthalten.", "copied");
            return ProfileJson.Clone(wrapper).Bindings;
        }
        public static List<Binding> CopyKey(Profile profile, int keyIndex)
        { return CopyKey(profile, keyIndex, null); }
        public static List<Binding> CopyKey(Profile profile, int keyIndex, string controllerId)
        {
            MappingValidation.RequireValid(profile); RequireKey(keyIndex); RequireController(profile, controllerId);
            var bindings = BindingsFor(profile, keyIndex, controllerId);
            if (bindings.Count == 0) throw new ArgumentException("Die Quelltaste hat keine Zuordnung.", "keyIndex");
            return CloneClipboard(bindings);
        }
        public static Profile Paste(Profile profile, IEnumerable<int> keyIndices, List<Binding> copied, CopyPart part)
        { return Paste(profile, keyIndices, copied, part, null); }
        // An explicit destination route scopes replacement/partial edits to that
        // route only. Without an override, copied mappings retain their source
        // route IDs, while processing-only edits retain destination route IDs.
        public static Profile Paste(Profile profile, IEnumerable<int> keyIndices, List<Binding> copied, CopyPart part, string controllerId)
        {
            MappingValidation.RequireValid(profile);
            RequireController(profile, controllerId);
            if (!Enum.IsDefined(typeof(CopyPart), part)) throw new ArgumentException("Unbekannte Kopiergruppe.", "part");
            List<int> selected = Keys(keyIndices);
            List<Binding> source = CloneClipboard(copied);
            if (controllerId != null && (part == CopyPart.All || part == CopyPart.Mapping))
            {
                var outputs = new HashSet<OutputTarget>();
                foreach (Binding binding in source)
                    if (!outputs.Add(binding.Target))
                        throw new ArgumentException("Die kopierten Zuordnungen enthalten dasselbe Ziel für den gewählten Controller mehrfach.", "copied");
            }
            // Preflight EVERY destination before making any edits or creating IDs.
            int replaced = 0;
            foreach (int key in selected)
            {
                int count = BindingsFor(profile, key, controllerId).Count;
                if (part != CopyPart.All && count == 0) throw new ArgumentException("Zieltaste " + key + " hat keine bestehenden Zuordnungen; nur Alles darf dort neue anlegen.", "keyIndices");
                replaced += count;
            }
            if ((part == CopyPart.All || part == CopyPart.Mapping) && profile.Bindings.Count - replaced + source.Count * selected.Count > 4096)
                throw new ArgumentException("Einfuegen wuerde mehr als 4096 Bindings erzeugen.", "copied");
            Profile result = ProfileJson.Clone(profile);
            foreach (int key in selected)
            {
                List<Binding> existing = BindingsFor(result, key, controllerId);
                if (part == CopyPart.All || part == CopyPart.Mapping)
                {
                    result.Bindings.RemoveAll(delegate(Binding binding) { return binding.KeyIndex == key && (controllerId == null || binding.ControllerId == controllerId); });
                    for (int i = 0; i < source.Count; i++)
                    {
                        SignalSettings processing = part == CopyPart.All ? source[i].Processing :
                            existing.Count == source.Count ? existing[i].Processing : existing[0].Processing;
                        result.Bindings.Add(new Binding { KeyIndex = key, Target = source[i].Target,
                            ControllerId = controllerId ?? source[i].ControllerId, Enabled = source[i].Enabled, Processing = CloneSignal(processing) });
                    }
                }
                else
                {
                    foreach (Binding destination in existing)
                    {
                        CopyGroup(source[0].Processing, destination.Processing, part);
                        if (part == CopyPart.AllExceptMapping) destination.Enabled = source[0].Enabled;
                    }
                }
            }
            MappingValidation.RequireValid(result);
            return result;
        }
        private static void CopyGroup(SignalSettings source, SignalSettings target, CopyPart part)
        {
            bool all = part == CopyPart.AllExceptMapping;
            if (all || part == CopyPart.Deadzones)
            { target.TopDeadzone = source.TopDeadzone; target.BottomDeadzone = source.BottomDeadzone; target.OutputDeadzone = source.OutputDeadzone; }
            if (all || part == CopyPart.Curve)
            { target.Curve = source.Curve; target.Exponent = source.Exponent; target.CustomPoints = ClonePoints(source.CustomPoints); }
            if (all || part == CopyPart.Filters)
            { target.Hysteresis = source.Hysteresis; target.SmoothingTimeConstant = source.SmoothingTimeConstant; }
            if (all || part == CopyPart.OutputRange)
            {
                target.MinOutput = source.MinOutput; target.MaxOutput = source.MaxOutput; target.Scale = source.Scale;
                target.OutputDeadzone = source.OutputDeadzone; target.ButtonThreshold = source.ButtonThreshold;
            }
        }

        private static bool NumericType(object value)
        {
            if (value == null || value.GetType().IsEnum) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal: return true;
                default: return false;
            }
        }
        private static double Number(object value)
        {
            double number;
            if (value is string)
            {
                string text = ((string)value).Trim();
                // Decimal comma is allowed for the German editor, never thousands.
                if (text.IndexOf(',') >= 0)
                {
                    if (text.IndexOf('.') >= 0 || text.IndexOf(',') != text.LastIndexOf(',')) throw new ArgumentException("Mehrdeutiges Zahlenformat.", "value");
                    text = text.Replace(',', '.');
                }
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new ArgumentException("Eine Zahl wird erwartet.", "value");
            }
            else if (NumericType(value)) number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            else throw new ArgumentException("Eine Zahl wird erwartet.", "value");
            if (!MappingValidation.IsFinite(number)) throw new ArgumentException("Zahlen muessen endlich sein.", "value");
            return number;
        }
        private static object EnumValue(Type type, object value)
        {
            object parsed;
            if (value != null && value.GetType() == type) parsed = value;
            else if (value is string)
            {
                if (((string)value).IndexOf(',') >= 0) throw new ArgumentException("Genau ein Auswahlwert wird erwartet.", "value");
                try { parsed = Enum.Parse(type, (string)value, true); }
                catch (Exception) { throw new ArgumentException("Unbekannter Auswahlwert.", "value"); }
            }
            else if (NumericType(value))
            {
                double number = Number(value);
                if (number < int.MinValue || number > int.MaxValue || number != Math.Truncate(number)) throw new ArgumentException("Ganzzahliger Auswahlwert erwartet.", "value");
                parsed = Enum.ToObject(type, (int)number);
            }
            else throw new ArgumentException("Passender Auswahlwert erwartet.", "value");
            if (!Enum.IsDefined(type, parsed)) throw new ArgumentException("Unbekannter Auswahlwert.", "value");
            return parsed;
        }
        private static object TypedValue(string property, object value)
        {
            if (NumberProperties.Contains(property)) return Number(value);
            if (property == "Curve") return EnumValue(typeof(CurveKind), value);
            if (property == "Target") return EnumValue(typeof(OutputTarget), value);
            if (property == "Enabled")
            {
                if (value is bool) return value;
                bool parsed;
                if (value is string && bool.TryParse((string)value, out parsed)) return parsed;
                throw new ArgumentException("Enabled erwartet true oder false.", "value");
            }
            var points = value as IEnumerable<CurvePoint>;
            if (points == null) throw new ArgumentException("CustomPoints erwartet eine Kurvenpunktliste.", "value");
            return ClonePoints(points);
        }
        private static object Get(Binding binding, string property)
        {
            SignalSettings s = binding.Processing;
            switch (property)
            {
                case "TopDeadzone": return s.TopDeadzone; case "BottomDeadzone": return s.BottomDeadzone;
                case "Exponent": return s.Exponent; case "MinOutput": return s.MinOutput; case "MaxOutput": return s.MaxOutput;
                case "Scale": return s.Scale; case "OutputDeadzone": return s.OutputDeadzone; case "Hysteresis": return s.Hysteresis;
                case "SmoothingTimeConstant": return s.SmoothingTimeConstant; case "ButtonThreshold": return s.ButtonThreshold;
                case "Curve": return s.Curve; case "CustomPoints": return s.CustomPoints;
                case "Enabled": return binding.Enabled; case "Target": return binding.Target;
                default: throw new ArgumentException("Unbekannte Eigenschaft.");
            }
        }
        private static void Set(Binding binding, string property, object value)
        {
            SignalSettings s = binding.Processing;
            switch (property)
            {
                case "TopDeadzone": s.TopDeadzone = (double)value; break; case "BottomDeadzone": s.BottomDeadzone = (double)value; break;
                case "Exponent": s.Exponent = (double)value; break; case "MinOutput": s.MinOutput = (double)value; break;
                case "MaxOutput": s.MaxOutput = (double)value; break; case "Scale": s.Scale = (double)value; break;
                case "OutputDeadzone": s.OutputDeadzone = (double)value; break; case "Hysteresis": s.Hysteresis = (double)value; break;
                case "SmoothingTimeConstant": s.SmoothingTimeConstant = (double)value; break; case "ButtonThreshold": s.ButtonThreshold = (double)value; break;
                case "Curve": s.Curve = (CurveKind)value; break; case "CustomPoints": s.CustomPoints = ClonePoints((IEnumerable<CurvePoint>)value); break;
                case "Enabled": binding.Enabled = (bool)value; break; case "Target": binding.Target = (OutputTarget)value; break;
            }
        }
        /// <summary>
        /// Allowed properties: every SignalSettings field plus Enabled and Target.
        /// BindingId, KeyIndex and profile/calibration fields are not editable here.
        /// Numeric values or invariant/decimal-comma strings, enum values/names,
        /// bool values/strings and IEnumerable&lt;CurvePoint&gt; are accepted by type.
        /// Changing CustomPoints preserves Curve; select Custom explicitly if wanted.
        /// </summary>
        public static Profile ApplyProperty(Profile profile, IEnumerable<string> bindingIds, string property, object value)
        {
            MappingValidation.RequireValid(profile); RequireProperty(property);
            // Materialize the selection once: callers may pass a one-shot iterator.
            var selected = new List<string>();
            foreach (Binding binding in SelectedBindings(profile, bindingIds)) selected.Add(binding.BindingId);
            return ApplyValidatedProperty(profile, selected, property, TypedValue(property, value));
        }
        private static Profile ApplyValidatedProperty(Profile profile, IEnumerable<string> bindingIds, string property, object value)
        {
            Profile result = ProfileJson.Clone(profile);
            foreach (Binding binding in SelectedBindings(result, bindingIds)) Set(binding, property, value);
            MappingValidation.RequireValid(result);
            return result;
        }
        /// <summary>Returns the shared typed value, or null if mixed. An empty or invalid selection throws. CustomPoints are compared structurally and returned as a deep copy.</summary>
        public static object MixedValue(Profile profile, IEnumerable<string> bindingIds, string property)
        {
            MappingValidation.RequireValid(profile); RequireProperty(property);
            List<Binding> selected = SelectedBindings(profile, bindingIds);
            object first = Get(selected[0], property);
            for (int i = 1; i < selected.Count; i++)
            {
                object next = Get(selected[i], property);
                if (property == "CustomPoints")
                {
                    var a = (List<CurvePoint>)first; var b = (List<CurvePoint>)next;
                    if (a.Count != b.Count) return null;
                    for (int p = 0; p < a.Count; p++) if (a[p].X != b[p].X || a[p].Y != b[p].Y || a[p].Tangent != b[p].Tangent) return null;
                }
                else if (!object.Equals(first, next)) return null;
            }
            return property == "CustomPoints" ? ClonePoints((IEnumerable<CurvePoint>)first) : first;
        }
    }
}
