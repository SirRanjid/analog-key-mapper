using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    public static class MappingValidation
    {
        public static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Unit(double value) { return IsFinite(value) && value >= 0 && value <= 1; }
        public static List<string> ValidateHotkey(HotkeySettings value)
        {
            var errors = new List<string>();
            if (value == null) { errors.Add("Tastenkombination für den Moduswechsel fehlt."); return errors; }
            int key = value.KeyCode;
            if (key < 1 || key > 255 || key == 0x10 || key == 0x11 || key == 0x12 ||
                key == 0x5B || key == 0x5C || key >= 0xA0 && key <= 0xA5)
                errors.Add("Eine Taste in 1..255 außer reinen Umschalttasten ist erforderlich.");
            if (value.Modifiers < 0 || value.Modifiers > 15) errors.Add("Ungültige Zusatztasten für den Moduswechsel.");
            return errors;
        }
        public static List<string> ValidateCalibration(Calibration value)
        {
            var errors = new List<string>();
            if (value == null) { errors.Add("Kalibrierung fehlt."); return errors; }
            if (!IsFinite(value.Rest) || !IsFinite(value.Bottom) || value.Rest == value.Bottom || !IsFinite(value.Bottom - value.Rest))
                errors.Add("Ruhe- und Endwert muessen endlich, verschieden und numerisch auswertbar sein.");
            if (value.UsableMin.HasValue != value.UsableMax.HasValue)
                errors.Add("Optionale Rohwertgrenzen muessen gemeinsam angegeben werden.");
            if (value.UsableMin.HasValue && value.UsableMax.HasValue)
            {
                double min = value.UsableMin.Value, max = value.UsableMax.Value;
                if (!IsFinite(min) || !IsFinite(max) || min >= max || value.Rest < min || value.Rest > max || value.Bottom < min || value.Bottom > max)
                    errors.Add("Rohwertgrenzen muessen geordnet sein und beide Kalibrierpunkte enthalten.");
            }
            if (value.MeasuredTravel.HasValue && (!IsFinite(value.MeasuredTravel.Value) || value.MeasuredTravel.Value <= 0))
                errors.Add("Ein gemessener physischer Hub muss endlich und positiv sein.");
            return errors;
        }
        public static List<string> ValidateSettings(SignalSettings value)
        {
            var errors = new List<string>();
            if (value == null) { errors.Add("Signalverarbeitung fehlt."); return errors; }
            if (!Unit(value.TopDeadzone) || !Unit(value.BottomDeadzone) || value.TopDeadzone + value.BottomDeadzone >= 1)
                errors.Add("Deadzonen muessen in 0..1 liegen und zusammen kleiner als 1 sein.");
            if (!Enum.IsDefined(typeof(CurveKind), value.Curve)) errors.Add("Unbekannte Kurvenart.");
            if (!IsFinite(value.Exponent) || value.Exponent <= 0) errors.Add("Exponent muss endlich und positiv sein.");
            if (!Unit(value.MinOutput) || !Unit(value.MaxOutput) || value.MinOutput > value.MaxOutput) errors.Add("Ausgabegrenzen muessen geordnet in 0..1 liegen.");
            if (!IsFinite(value.Scale) || value.Scale < 0) errors.Add("Skalierung muss endlich und nichtnegativ sein.");
            if (!IsFinite(value.Hysteresis) || value.Hysteresis < 0 || value.Hysteresis >= 1 - value.TopDeadzone - value.BottomDeadzone)
                errors.Add("Hysterese muss nichtnegativ und kleiner als der nutzbare Eingabebereich sein.");
            if (!IsFinite(value.SmoothingTimeConstant) || value.SmoothingTimeConstant < 0) errors.Add("Glaettungszeit muss endlich und nichtnegativ sein.");
            if (!Unit(value.ButtonThreshold) || value.ButtonThreshold <= 0) errors.Add("Button-Schwelle muss in (0,1] liegen.");
            if (!Unit(value.OutputDeadzone)) errors.Add("Ausgabe-Deadzone muss in 0..1 liegen.");
            if (value.CustomPoints == null || value.CustomPoints.Count < 2 || value.CustomPoints.Count > 64)
                errors.Add("Eine benutzerdefinierte Kurve braucht 2..64 Punkte.");
            else
            {
                for (int i = 0; i < value.CustomPoints.Count; i++)
                {
                    CurvePoint p = value.CustomPoints[i];
                    if (p == null || !Unit(p.X) || !Unit(p.Y)) { errors.Add("Kurvenpunkte muessen endlich in 0..1 liegen."); continue; }
                    if (p.Tangent.HasValue && (!IsFinite(p.Tangent.Value) || p.Tangent.Value < 0))
                        errors.Add("Kurventangenten muessen endlich und nichtnegativ sein; leer bedeutet automatisch.");
                    if (i == 0 && (p.X != 0 || p.Y != 0)) errors.Add("Die Kurve muss bei (0,0) beginnen.");
                    if (i == value.CustomPoints.Count - 1 && (p.X != 1 || p.Y != 1)) errors.Add("Die Kurve muss bei (1,1) enden.");
                    CurvePoint previous = i > 0 ? value.CustomPoints[i - 1] : null;
                    if (previous != null && (p.X <= previous.X || p.Y < previous.Y)) errors.Add("Kurven-X muss strikt steigen; Kurven-Y darf nicht fallen.");
                }
            }
            return errors;
        }
        public static List<string> ValidateProfile(Profile value)
        {
            var errors = new List<string>();
            if (value == null) { errors.Add("Profil fehlt."); return errors; }
            if (value.Version != 1) errors.Add("Nicht unterstuetzte Profilversion.");
            errors.AddRange(ValidateHotkey(value.ModeSwitchHotkey));
            errors.AddRange(ValidateHotkey(value.EmergencyStopHotkey));
            if (value.ModeSwitchHotkey != null && value.EmergencyStopHotkey != null && value.ModeSwitchHotkey.Enabled && value.EmergencyStopHotkey.Enabled &&
                value.ModeSwitchHotkey.KeyCode == value.EmergencyStopHotkey.KeyCode && value.ModeSwitchHotkey.Modifiers == value.EmergencyStopHotkey.Modifiers)
                errors.Add("Moduswechsel und Abschalter benötigen verschiedene Tastenkombinationen, wenn beide eingeschaltet sind.");
            if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 128) errors.Add("Profilname muss 1..128 Zeichen enthalten.");
            if (!Enum.IsDefined(typeof(StickShape), value.StickShape)) errors.Add("Unbekannte Stickform.");
            if (!Enum.IsDefined(typeof(OpposedPolicy), value.OpposedPolicy)) errors.Add("Unbekannte Gegenrichtungsregel.");
            if (!Enum.IsDefined(typeof(AggregationMode), value.Aggregation)) errors.Add("Unbekannte Zusammenfassung.");
            if (!Enum.IsDefined(typeof(ControllerKind), value.Controller)) errors.Add("Unbekannter Controllertyp.");
            if (!Enum.IsDefined(typeof(KeyboardSuppressionMode), value.KeyboardSuppressionMode)) errors.Add("Unbekannter Modus für die Tastensperre.");
            if (value.ModeSwitchRgbColor < 0 || value.ModeSwitchRgbColor > 0xFFFFFF) errors.Add("Die Farbe der Modustaste muss ein RGB-Wert in 0..16777215 sein.");
            var controllerIds = new HashSet<string>(StringComparer.Ordinal);
            var controllerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (value.Controllers == null || value.Controllers.Count > 32) errors.Add("Controller-Liste fehlt oder überschreitet 32 Einträge.");
            else if (value.Controllers.Count == 0) controllerIds.Add("main");
            else foreach (ControllerDefinition controller in value.Controllers)
            {
                if (controller == null) { errors.Add("Leerer Controller-Eintrag."); continue; }
                if (!ValidControllerId(controller.Id) || !controllerIds.Add(controller.Id)) errors.Add("Controller-ID muss gültig und eindeutig sein.");
                if (!ValidControllerName(controller.Name) || !controllerNames.Add(controller.Name)) errors.Add("Controller-Namen müssen eindeutig sein und 1..64 Zeichen enthalten.");
                if (!Enum.IsDefined(typeof(ControllerKind), controller.Kind)) errors.Add("Unbekannter Controllertyp im Controller-Eintrag.");
                if (controller.RgbColor.HasValue && (controller.RgbColor.Value < 0 || controller.RgbColor.Value > 0xFFFFFF))
                    errors.Add("Controllerfarbe muss ein RGB-Wert in 0..16777215 sein oder die Standardfarbe verwenden.");
                if (controller.Id == "main" && controller.Kind != value.Controller) errors.Add("Der Hauptcontroller stimmt nicht mit dem bisherigen Controllertyp überein.");
            }
            if (value.SuppressedKeyboardKeys == null || value.SuppressedKeyboardKeys.Count > 256)
                errors.Add("Liste unterdrückter Tastatureingaben fehlt oder überschreitet 256 Einträge.");
            else
            {
                var suppressedKeys = new HashSet<int>();
                foreach (int key in value.SuppressedKeyboardKeys)
                    if (key < 0 || key > 255 || !suppressedKeys.Add(key)) errors.Add("Unterdrückte Tastenindizes müssen eindeutig in 0..255 liegen.");
            }
            if (value.Inputs == null || value.Inputs.Count > 256) errors.Add("Tasteinstellungen fehlen oder überschreiten 256 Einträge.");
            else
            {
                var inputs = new Dictionary<int, KeyInputSettings>();
                foreach (KeyInputSettings input in value.Inputs)
                {
                    errors.AddRange(ValidateInputSettings(input));
                    if (input == null) continue;
                    if (inputs.ContainsKey(input.KeyIndex)) errors.Add("Eine physische Taste hat mehrere Eingabeeinstellungen.");
                    else inputs.Add(input.KeyIndex, input);
                }
                foreach (KeyInputSettings input in value.Inputs)
                {
                    if (input == null || !input.OppositeKeyIndex.HasValue) continue;
                    KeyInputSettings opposite;
                    if (!inputs.TryGetValue(input.OppositeKeyIndex.Value, out opposite) || opposite.OppositeKeyIndex != input.KeyIndex || opposite.OppositePolicy != input.OppositePolicy)
                        errors.Add("Gegentasten müssen gegenseitig mit derselben Vorrangregel verbunden sein.");
                }
            }
            if (value.Bindings == null || value.Bindings.Count > 4096) { errors.Add("Bindings fehlen oder ueberschreiten 4096 Eintraege."); return errors; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var assignments = new HashSet<Tuple<int, OutputTarget, string>>();
            foreach (Binding binding in value.Bindings)
            {
                if (binding == null) { errors.Add("Leeres Binding."); continue; }
                if (string.IsNullOrWhiteSpace(binding.BindingId) || binding.BindingId.Length > 128 || !ids.Add(binding.BindingId)) errors.Add("BindingId muss nichtleer, hoechstens 128 Zeichen lang und eindeutig sein.");
                if (binding.KeyIndex < 0 || binding.KeyIndex > 255) errors.Add("Tastenindex muss in 0..255 liegen.");
                if (!ValidControllerId(binding.ControllerId) || !controllerIds.Contains(binding.ControllerId)) errors.Add("Zuordnung verweist auf einen unbekannten Controller.");
                if (!assignments.Add(Tuple.Create(binding.KeyIndex, binding.Target, binding.ControllerId)))
                    errors.Add("Dieselbe Taste darf demselben Ziel desselben Controllers nur einmal zugeordnet sein.");
                if (!Enum.IsDefined(typeof(OutputTarget), binding.Target)) errors.Add("Unbekanntes Ausgabeziel.");
                foreach (string error in ValidateSettings(binding.Processing)) errors.Add((binding.BindingId ?? "?") + ": " + error);
            }
            return errors;
        }
        public static bool ValidControllerId(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char value in id)
                if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                    (value >= '0' && value <= '9') || value == '-' || value == '_')) return false;
            return true;
        }
        public static bool ValidControllerName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim()) return false;
            foreach (char value in name) if (Char.IsControl(value)) return false;
            return true;
        }
        public static List<string> ValidateInputSettings(KeyInputSettings value)
        {
            var errors = new List<string>();
            if (value == null) { errors.Add("Tasteinstellung fehlt."); return errors; }
            if (value.KeyIndex < 0 || value.KeyIndex > 255) errors.Add("Tastenindex muss in 0..255 liegen.");
            if (!Unit(value.ActuationPoint) || value.ActuationPoint <= 0) errors.Add("Der Auslösepunkt muss größer als 0 und höchstens 100 % sein.");
            if (!Unit(value.PressMovement) || value.PressMovement <= 0) errors.Add("Der erneute Druckweg muss größer als 0 und höchstens 100 % sein.");
            if (!Unit(value.ReleaseMovement) || value.ReleaseMovement <= 0) errors.Add("Der Loslassweg muss größer als 0 und höchstens 100 % sein.");
            if (!Enum.IsDefined(typeof(InputOpposedPolicy), value.OppositePolicy)) errors.Add("Unbekannte Vorrangregel für Gegentasten.");
            if (value.OppositeKeyIndex.HasValue && (value.OppositeKeyIndex.Value < 0 || value.OppositeKeyIndex.Value > 255 || value.OppositeKeyIndex == value.KeyIndex))
                errors.Add("Eine Gegentaste muss eine andere gültige physische Taste sein.");
            return errors;
        }
        public static void RequireValid(Profile value)
        {
            var errors = ValidateProfile(value);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.ToArray()), "profile");
        }
    }
}
