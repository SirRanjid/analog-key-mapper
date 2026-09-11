using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;

namespace Tk75.App
{
    // Display translations never rewrite profile names, editable text or enum values.
    public static class UiText
    {
        sealed class TextOrigin { public string Original, Displayed; }
        static readonly ConditionalWeakTable<object, TextOrigin> Origins = new ConditionalWeakTable<object, TextOrigin>();
        static readonly ConditionalWeakTable<Control, object> PreservedText = new ConditionalWeakTable<Control, object>();
        static readonly object gate = new object();
        static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal) {
            {"Deine Tastatur", "Your keyboard"}, {"Tastatur einrichten", "Set up keyboard"}, {"TASTATUR", "KEYBOARD"}, {"PROFIL", "PROFILE"},
            {"Verbinden", "Connect"}, {"Auswählen", "Select"}, {"Taste auswählen", "Select a key"}, {"Wähle eine Taste, um sie einzurichten.", "Select a key to set it up."},
            {"Controller verbunden – gehaltene Tasten nach Loslassen bereit", "Controller connected – release held keys to make them ready"},
            {"Controller wird verbunden …", "Connecting controller …"},
            {"Controller-Verbindung wird abgebrochen …", "Canceling controller connection …"},
            {"Controller-Verbindung abgebrochen.", "Controller connection canceled."},
            {"Die gespeicherten Controller-Startoptionen konnten nicht geprüft werden.", "Controller startup preferences could not be verified after saving."},
            {"Die gespeicherte Controller-Startsitzung konnte nicht geprüft werden.", "Controller startup session could not be verified after saving."},
            {"Die Controller-Startoptionen wurden vor dem bestätigten Beenden geändert.", "Controller startup preferences changed before shutdown was confirmed."},
            {"Die Controller-Startsitzung ist nicht verfügbar; bitte manuell verbinden.", "Controller startup session is unavailable; connect controllers manually."},
            {"Die Controller-Startsitzung wurde geändert; bitte manuell verbinden.", "Controller startup session changed; connect controllers manually."},
            {"Ungültige Controller-Startsitzung.", "Invalid controller startup session."},
            {"Klicke auf eine Taste in der Abbildung.", "Click a key on the keyboard."}, {"Noch nicht kalibriert.", "Not calibrated yet."},
            {"Kalibriert · bereit zum Zuordnen.", "Calibrated · ready to map."}, {"Ziele gemeinsam zuweisen. Jede Taste einzeln kalibrieren.", "Assign outputs together. Calibrate each key separately."},
            {"Druckwerte noch nicht verfügbar. Siehe Verbindungsstatus unten.", "Pressure data is unavailable. See the connection status below."},
            {"Keine Druckwerte.\nVerbindungsdetails stehen unten.", "No pressure data.\nConnection details are below."},
            {"Druck: —", "Pressure: —"}, {"Zum Messen oben verbinden", "Connect above to read pressure"}, {"Druckzugriff noch nicht bereit", "Pressure input is not ready"},
            {"Warte auf Druckdaten …", "Waiting for pressure data …"}, {"1 · Kalibrieren", "1 · Calibrate"}, {"Erneut kalibrieren", "Recalibrate"},
            {"Warum kommen keine Druckwerte?", "Why is there no pressure data?"}, {"Druckwert-Verbindung", "Pressure input connection"},
            {"Controller-Ziele", "Controller outputs"}, {"+ Ziel hinzufügen", "+ Add output"}, {"Controller-Ziel", "Controller output"},
            {"Noch keine Zuordnung\nWähle oben dein erstes Ziel.", "No mapping yet\nChoose your first output above."},
            {"Entfernen", "Remove"}, {"Ein / aus", "On / off"}, {"An", "On"}, {"Aus", "Off"}, {"Ja", "Yes"}, {"Nein", "No"}, {"Wert", "Value"}, {"Ausgabe", "Output"},
            {"Strg + Klick für mehrere Tasten", "Ctrl + click to select multiple keys"}, {"ISO · voreingestellt", "ISO · preset"},
            {"Manuell · ANSI", "Manual · ANSI"}, {"Manuell · ISO", "Manual · ISO"}, {"Automatisch · ISO", "Automatic · ISO"}, {"Automatisch · ANSI", "Automatic · ANSI"},
            {"Automatisch · unbekannt", "Automatic · unknown"}, {"Feinabstimmung  ▾", "Fine-tuning  ▾"}, {"Feinabstimmung  ▴", "Fine-tuning  ▴"},
            {"Feinabstimmung", "Fine-tuning"}, {"Controller noch nicht bereit", "Controller not ready"}, {"Aus · F8", "Off · F8"},
            {"Profil", "Profile"}, {"Speichern  ·  Strg+S", "Save  ·  Ctrl+S"}, {"Neu", "New"}, {"Duplizieren", "Duplicate"},
            {"Umbenennen", "Rename"}, {"Löschen", "Delete"}, {"Importieren", "Import"}, {"Exportieren", "Export"},
            {"Rückgängig  ·  Strg+Z", "Undo  ·  Ctrl+Z"}, {"Wiederholen  ·  Strg+Y", "Redo  ·  Ctrl+Y"}, {"Geräte neu suchen", "Refresh devices"},
            {"Tastaturabbildung", "Keyboard layout"}, {"Automatisch erkennen", "Detect automatically"},
            {"Beschriftung QWERTZ", "QWERTZ legends"}, {"Beschriftung QWERTY", "QWERTY legends"},
            {"Live-Monitor", "Live monitor"}, {"Erkannte Tasten / Anlernen", "Detected keys / Identify a key"}, {"Erkannte Tasten", "Detected keys"},
            {"Taste anders benennen / anlernen", "Rename / identify a key"}, {"Profil & Programmregeln", "Profile & application rules"},
            {"Verbindung & Entwicklungsstand", "Connection & development status"}, {"← Tastatur", "← Keyboard"},
            {"Einstellungen kopieren", "Copy settings"}, {"Einstellungen einfügen (gewählte Gruppe)", "Paste settings (selected group)"},
            {"Index", "Index"}, {"Taste", "Key"}, {"Rohwert", "Raw value"}, {"Ziele", "Outputs"}, {"Taste / Ziel", "Key / output"},
            {"Hub mm¹", "Travel mm¹"}, {"Normalisiert", "Normalized"}, {"Nach Deadzone", "After deadzone"}, {"Nach Kurve", "After curve"},
            {"Anwenden", "Apply"}, {"Presets ▾", "Presets ▾"}, {"Aus gewählter Zuordnung speichern", "Save selected mapping as preset"},
            {"Alles außer Mapping", "Settings without outputs"}, {"Alles", "Everything"}, {"Nur Deadzones", "Deadzone settings only"},
            {"Nur Kurve", "Response curve only"}, {"Nur Filter", "Stability settings only"}, {"Nur Output Range", "Output range only"}, {"Nur Mapping", "Outputs only"},
            {"Kopieren", "Copy"}, {"Einfügen", "Paste"}, {"Parameter", "Setting"}, {"Gemischt", "Mixed"},
            {"Linear", "Linear"}, {"Soft", "Gentle"}, {"Aggressiv", "Responsive"}, {"Präzise Bewegung", "Precise movement"},
            {"Deadzone Ruhe (0–1)", "Ignore movement at the top (0–1)"}, {"Deadzone Anschlag (0–1)", "Reach full output earlier (0–1)"},
            {"Kurve", "Response curve"}, {"Exponent / Stärke", "Curve strength"}, {"Ausgabe Minimum (0–1)", "Minimum active output (0–1)"},
            {"Ausgabe Maximum (0–1)", "Maximum output (0–1)"}, {"Skalierung", "Output strength"}, {"Ausgabe-Deadzone (0–1)", "Ignore small outputs (0–1)"},
            {"Hysterese (0–1)", "Switch gap (0–1)"}, {"Schaltabstand (0–1)", "Switch gap (0–1)"},
            {"Glättung (Sekunden; 0 = aus)", "Smooth changes (seconds; 0 = off)"}, {"Button-Schwelle (0–1)", "Button activation point (0–1)"},
            {"Antwortkurve", "Response curve"}, {"Gemischt · erste Zuordnung", "Mixed · first mapping"}, {"Zuordnung auswählen", "Select a mapping"},
            {"Abbrechen", "Cancel"}, {"Übernehmen", "Apply"}, {"Speichern", "Save"}, {"Neu aufnehmen", "Start again"},
            {"Ich habe die Taste beide Male bis zum Anschlag gedrückt.", "I pressed the key all the way down both times."},
            {"Optional: gemessenen Tastenhub angeben", "Optional: enter measured key travel"}, {"Selbst gemessener Gesamthub (mm)", "Total travel you measured (mm)"},
            {"Fertig. Anschlag bestätigen und übernehmen.", "Done. Confirm full presses, then apply."},
            {"Zu wenige unterschiedliche Druckwerte. Bitte langsamer neu aufnehmen.", "Not enough distinct pressure values. Try again more slowly."},
            {"Bis zum Anschlag drücken, dann vollständig loslassen.", "Press all the way down, then release fully."},
            {"Noch einmal langsam bis zum Anschlag drücken und loslassen.", "Slowly press all the way down once more, then release."},
            {"Du bestimmst das Tempo.", "Go at your own pace."}, {"Erster Durchgang geschafft.", "First pass completed."},
            {"Mehrere Tasten erkannt. Bitte abbrechen und nur die gewünschte Taste bewegen.", "Multiple keys detected. Cancel and move only the intended key."},
            {"Noch keine Bewegung dieser Taste erkannt.", "No movement detected for this key yet."},
            {"Eine andere Taste wurde gedrückt. Aufnahme neu starten.", "Another key was pressed. Start again."},
            {"Kalibrierung", "Calibration"}, {"Profilname", "Profile name"},
            {"Oberflächenvorschau · Beispieldaten · keine Geräte geöffnet", "UI preview · example data · no devices opened"},
            {"Vorschau · Controller noch in Prüfung", "Preview · controller validation pending"}, {"Controller aus", "Controller off"},
            {"Bereit; Lesen nicht gestartet.", "Ready; reading has not started."}, {"Tastatur-Zugriff wird geprüft und gestartet …", "Checking and starting keyboard access …"},
            {"Tastatur bereit. Drücke eine Taste – noch keine Druckwerte empfangen.", "Keyboard ready. Press a key — no pressure values received yet."},
            {"Druckwerte werden empfangen.", "Receiving pressure values."}, {"Lesen angehalten.", "Reading stopped."}, {"Lesen wird angehalten.", "Stopping input."},
            {"Keine aktuellen Messwerte", "No current readings"}, {"Unkalibriert", "Not calibrated"}, {"Unbekannt", "Unknown"},
            {"Keine Tastatur verbunden", "No keyboard connected"}, {"Controller aktivieren", "Enable controller"},
            {"Automatisch nach Vordergrundprogramm", "Switch profiles for the active application"}, {"Auch unbekannte Tasten-Indizes zeigen", "Show unidentified key indices"},
            {"Auslösepunkt für Buttons (0–1)", "Button activation point (0–1)"},
            {"Ruhebereich (0–1)", "Ignore movement at the top (0–1)"}, {"Anschlagbereich (0–1)", "Reach full output earlier (0–1)"},
            {"Kleine Ausgaben ignorieren (0–1)", "Ignore small outputs (0–1)"}, {"Kurvenstärke", "Curve strength"}, {"Ausgabestärke", "Output strength"},
            {"Stickform: Circle = diagonale Länge begrenzen", "Stick shape: Circle limits diagonal distance"},
            {"Gegenrichtungen: Neutral oder Differenz", "Opposite outputs: neutral or difference"},
            {"Gleiche Ziele: Maximum oder begrenzte Summe", "Shared outputs: strongest input or capped sum"},
            {"Achse invertieren: am Mapping das entsprechende + / − Ziel wählen.\nKeine Ausgabe an einer losgelassenen Taste, auch bei Minimum > 0.", "To reverse an axis, choose its + or − output in the mapping.\nA released key produces no output, even with a minimum above zero."},
            {"Es werden nur Programmname oder Programmpfad im Vordergrund gelesen.\nEin automatischer Profilwechsel neutralisiert und deaktiviert den Controller.\nDas gewählte Profil wird danach bewusst wieder aktiviert.", "Application rules use the active application's name or path.\nAutomatic profile switching releases and disables the controller.\nEnable the selected profile again when you are ready."},
            {"Programmregeln", "Application rules"}, {"Ausgewählte Regel entfernen", "Remove selected rule"},
            {"Programmname (z. B. Spiel.exe) oder vollständiger Pfad", "Application name (e.g. Game.exe) or full path"},
            {"Presetname", "Preset name"}, {"Welche Taste anlernen?", "Which key should be identified?"},
            {"Zum Kopieren genau eine Quelltaste auswählen.", "Select one source key to copy."},
            {"Zuerst eine Taste mit Zuordnungen kopieren.", "Copy a key with mappings first."},
            {"Zuerst eine oder mehrere Tasten auswählen.", "Select one or more keys first."},
            {"Genau eine Zuordnung als Quelle für das neue Preset auswählen.", "Select one mapping to save as a preset."},
            {"Zuerst die Tastatur verbinden.", "Connect the keyboard first."},
            {"F8 ist bereits belegt. Der globale Abschalter muss verfügbar sein, bevor der Controller aktiviert wird.", "F8 is already in use. The global off shortcut must be available before enabling the controller."},
            {"Es fehlen zwei vollständige, eindeutige Druck-/Loslasszyklen mit genügend unterschiedlichen Messwerten.", "Two complete press-and-release passes with enough distinct readings are required."},
            {"Den tatsächlichen Anschlag bestätigen. Ein beobachtetes Maximum allein beweist keinen vollständigen Tastendruck.", "Confirm that you pressed the key all the way down. The highest recorded value alone cannot confirm this."},
            {"Einrichten", "Getting started"}, {"Fein einstellen", "Fine-tuning"}, {"Wenn kein Druck ankommt", "If no pressure data arrives"}, {"Controller-Ausgabe", "Controller output"},
            {"Verbinde deine Tastatur oben. Klicke danach eine Taste auf der Abbildung an. Über Kalibrieren wird ihr tatsächlicher Druckbereich gemessen. Wähle ein Controller-Ziel und füge es hinzu. Jede Taste kann beliebig viele Ziele erhalten.", "Connect your keyboard at the top, then click a key on the keyboard. Calibrate measures its actual pressure range. Choose a controller output and add it. Each key can have multiple outputs."},
            {"Unter Feinabstimmung findest du Kurven, Deadzones, Grenzen, Skalierung, Filter und Presets. Wähle rechts eine oder mehrere Zuordnungen, um nur deren Werte zu ändern. Strg + Klick wählt mehrere Tasten. Strg+C/V kopiert die gewählte Einstellungsgruppe; Strg+Z/Y macht Änderungen rückgängig oder wiederholt sie.", "Fine-tuning contains response curves, deadzones, output limits, strength, stability settings and presets. Select one or more mappings on the right to edit their values. Ctrl + click selects multiple keys. Ctrl+C/V copies and pastes the chosen settings group; Ctrl+Z/Y undoes and redoes changes."},
            {"Die Verbindung allein bestätigt noch keine Druckdaten. Die direkte Erfassung benötigt den signierten Tastatur-Helfer dieser Anwendung. Ist seine Signatur nicht vertrauenswürdig, startet er nicht. Der Verbindungsstatus zeigt den konkreten Grund. Ohne Druckdaten bleiben Anzeige und Kalibrierung leer.", "Connecting alone does not confirm that pressure data is available. Direct input requires this application's signed keyboard helper. If its signature is not trusted, it will not start. The connection status explains the reason. Without pressure data, the live display and calibration remain unavailable."},
            {"Die Controller-Ausgabe bleibt gesperrt, bis der alternative Controller-Unterbau den echten Start- und Abschalttest bestanden hat. Die bisherige Ausgabe meldete direkt beim Start unerwartete Stickwerte. Die Einstellungen und Vorschau sind bereits nutzbar. Es wird kein weiterer Treiber automatisch installiert.", "Controller output remains unavailable until the alternative controller component passes real startup and shutdown tests. The previous component reported unexpected stick values at startup. Settings and the calculated preview are available. No additional driver is installed automatically."},
            {"F8 schaltet eine aktivierte Ausgabe global aus. Änderungen, fehlende oder veraltete Eingaben, USB-Fehler und Ruhezustand schalten sie ebenfalls aus. Das Verhalten länger gehaltener Tasten muss noch geprüft werden.", "F8 turns off active controller output from any application. Setting changes, missing or stale input, USB errors and sleep also turn it off. The behavior of keys held down for longer periods still needs hardware validation."},
            {"Die TK75-Abbildung und Matrixzuordnung stammen aus dem Hersteller-Konfigurator. Die Druckdaten von WASD wurden am angeschlossenen Gerät geprüft. Weitere Tasten, Firmwarevarianten und FN/Drehgeber müssen getrennt bestätigt werden. Für andere Geräte wird keine passende Matrix vorausgesetzt.", "The TK75 drawing and key positions come from the manufacturer's configurator. WASD pressure data was checked on the connected keyboard. Other keys, firmware variants, Fn and the dial still need separate validation. The same key positions are not assumed for other devices."},
            {"Profile und Hardwarekalibrierung werden getrennt im Unterordner data gespeichert. Noch keine veröffentlichte stabile Version.", "Profiles and hardware calibration are saved separately in the data folder. This is not a published stable release yet."},
            {"Neutral", "Neutral"}, {"Subtract", "Difference"}, {"Circle", "Circle"}, {"Square", "Square"}, {"Maximum", "Strongest input"}, {"SumClamped", "Capped sum"}
        };
        static readonly Dictionary<string, string> German = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, KeyValuePair<string, string>> OptionLabels = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal) {
            {"Exponential", Pair("Sanfter Beginn", "Gentle start")}, {"Logarithmic", Pair("Starker Beginn", "Strong start")},
            {"Smoothstep", Pair("Sanfter Beginn und Anschlag", "Gentle start and finish")}, {"Custom", Pair("Eigene Kurve · eckig", "Custom curve · angular")},
            {"Bezier", Pair("Glatt (Bézier)", "Smooth (Bézier)")},
            {"Circle", Pair("Kreisförmig", "Circular")}, {"Square", Pair("Quadratisch", "Square")},
            {"Subtract", Pair("Differenz", "Difference")}, {"Maximum", Pair("Stärkste Eingabe", "Strongest input")},
            {"SumClamped", Pair("Begrenzt addieren", "Capped sum")}, {"ClampedSum", Pair("Begrenzt addieren", "Capped sum")}
        };
        static UiText()
        {
            foreach (var pair in English) if (!German.ContainsKey(pair.Value)) German.Add(pair.Value, pair.Key);
            Array.Sort(DynamicParts, delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return b.Key.Length.CompareTo(a.Key.Length); });
        }
        static string language = "en";
        public static string Language { get { return language; } }
        public static void SetLanguage(string value)
        { if (value != "de" && value != "en") throw new ArgumentException("Unsupported language."); language = value; }
        public static string Get(string german, string english)
        {
            lock (gate) { English[german] = english; if (!German.ContainsKey(english)) German.Add(english, german); }
            return language == "en" ? english : german;
        }
        public static string Get(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            KeyValuePair<string, string> option;
            if (OptionLabels.TryGetValue(value, out option)) return language == "en" ? option.Value : option.Key;
            string translated;
            lock (gate)
            {
                if (language == "de") return German.TryGetValue(value, out translated) ? translated : value;
                if (English.TryGetValue(value, out translated)) return translated;
            }
            foreach (var prefix in new[] { Pair("Taste kalibrieren · ", "Calibrate key · "), Pair("Taste erkennen · ", "Identify key · "), Pair("Profil: ", "Profile: ") })
                if (value.StartsWith(prefix.Key, StringComparison.Ordinal)) return prefix.Value + value.Substring(prefix.Key.Length);
            if (value.Contains("\r\n\r\n"))
            { string[] parts = value.Split(new[] { "\r\n\r\n" }, StringSplitOptions.None); for (int i = 0; i < parts.Length; i++) parts[i] = Get(parts[i]); return String.Join("\r\n\r\n", parts); }
            // Composed status strings contain static UI fragments plus real values.
            string result = value;
            foreach (var pair in DynamicParts) result = result.Replace(pair.Key, pair.Value);
            return result;
        }
        static readonly KeyValuePair<string, string>[] DynamicParts = {
            // App/KeyboardLayout.cs: app-owned validation; physical legends stay unchanged.
            Pair("Tastencode fehlt.", "The key code is missing."),
            Pair("Ungültige Tastengeometrie.", "The key geometry is invalid."),
            Pair("Layoutname fehlt.", "The layout name is missing."),
            Pair("Ungültige Layoutgröße.", "The layout size is invalid."),
            Pair("Taste liegt außerhalb des Layouts.", "A key lies outside the layout."),
            Pair("Tastencode oder Index mehrfach vergeben.", "A key code or index is assigned more than once."),
            Pair("Layout enthält keine Tasten.", "The layout contains no keys."),
            Pair("Taste gehört nicht zu diesem Layout.", "The key does not belong to this layout."),
            
            // App/ManagedMonitorSource.cs
            Pair("Verbindung abgebrochen.", "Connection cancelled."),
            Pair("Windows hat den Tastatur-Zugriff nicht gestartet.", "Windows did not start keyboard access."),
            Pair("Die Tastatur hat den Zugriff nicht rechtzeitig bestätigt.", "The keyboard did not confirm access in time."),
            Pair("Tastatur-Verbindung unterbrochen: ", "Keyboard connection interrupted: "),
            Pair("Tastatur-Zugriff wurde vorzeitig beendet.", "Keyboard access ended unexpectedly."),
            Pair("Die Tastatur-Verbindung wurde beendet.", "The keyboard connection has ended."),
            Pair("Unerwartete Daten nach dem Verbindungsende.", "Unexpected data arrived after the connection ended."),
            Pair("Doppelte Tastatur-Identifikation.", "The keyboard identification was received twice."),
            Pair("Druckwerte ohne bestätigte Tastatur-Identifikation.", "Pressure data arrived before the keyboard identity was confirmed."),
            Pair("Doppelte Geräte-Frischebestätigung.", "The device freshness capability was confirmed twice."),
            Pair("Geräte-Frische ohne bestätigtes Protokoll.", "Device freshness data arrived before the protocol was confirmed."),
            Pair("Doppelte RGB-Funktionsbestätigung.", "The lighting capability was confirmed twice."),
            Pair("Unbekannte RGB-Funktionsreihenfolge.", "Lighting capabilities were reported in an unexpected order."),
            Pair("RGB-Antwort ohne passende Leseanfrage.", "A lighting response arrived without a matching request."),
            Pair("Ungültige RGB-Sicherung.", "The lighting backup is invalid."),
            Pair("Uneindeutige RGB-Sicherung.", "The lighting backup is ambiguous."),
            Pair("RGB-Sicherung gehört zu einer anderen Tastatur oder Lichtebene.", "The lighting backup belongs to a different keyboard or lighting layer."),
            Pair("RGB-Änderung wurde nicht im erwarteten Zustand bestätigt; Sicherung behalten.", "The lighting change was not confirmed in the expected state; the backup was retained."),
            Pair("Druckwerte können nicht schnell genug verarbeitet werden. Bitte neu verbinden.", "Pressure data cannot be processed quickly enough. Please reconnect."),
            Pair("Tastatur-Zugriff beendet; Ausschalten der Messung wurde nicht bestätigt.", "Keyboard access ended; stopping measurements was not confirmed."),
            Pair("Tastatur-Zugriff: ", "Keyboard access: "),
            Pair("Die Tastatur antwortet nicht mehr rechtzeitig. Bitte neu verbinden.", "The keyboard is no longer responding in time. Please reconnect."),
            Pair("Die Tastatur ist nicht verbunden.", "The keyboard is not connected."),
            Pair("Der laufende Tastatur-Helfer unterstützt den RGB-Lesezugriff noch nicht.", "The running keyboard helper does not support reading lighting settings yet."),
            Pair("Der laufende Tastatur-Helfer unterstützt diese RGB-Änderung noch nicht.", "The running keyboard helper does not support this lighting change yet."),
            Pair("Die Beleuchtung wird bereits gelesen.", "Lighting settings are already being read."),
            Pair("Bitte die Tastatur neu verbinden.", "Please reconnect the keyboard."),
            Pair("Lesen der Beleuchtung abgebrochen.", "Reading lighting settings was cancelled."),
            Pair("Die Beleuchtung konnte nicht rechtzeitig vollständig gelesen werden.", "The lighting settings could not be read completely in time."),
            Pair("Zugriff getrennt; die Übertragung des Ausschaltbefehls konnte nicht bestätigt werden.", "Access was disconnected; delivery of the stop command could not be confirmed."),
            Pair("Ausschaltbefehl übertragen; Windows hat den Tastatur-Lesezugriff anschließend nicht rechtzeitig freigegeben.", "The stop command was sent, but Windows did not release keyboard access in time."),
            Pair("Der Tastatur-Zugriff konnte nicht fehlerfrei beendet werden.", "Keyboard access could not be closed without errors."),
            
            // App/MultiControllerSession.cs: composed status prefix.
            Pair("Tastaturverbindung fehlgeschlagen: ", "Keyboard connection failed: "),
            Pair("\r\nParametername: ", "\r\nParameter name: "),
            // Core/ControllerRouting.cs, KeyEditing.cs, MappingAssignments.cs
            Pair("Der ausgewählte Controller fehlt.", "The selected controller is missing."),
            Pair("Mindestens ein Controller muss erhalten bleiben.", "At least one controller must remain."),
            Pair("Ungültige oder doppelte Tasten-Auswahl.", "The key selection is invalid or contains duplicates."),
            Pair("Mindestens eine Taste auswählen.", "Select at least one key."),
            Pair("Für Gegentasten genau eine oder zwei Tasten auswählen.", "Select exactly one or two keys to configure opposite keys."),
            Pair("Die Gegentaste muss eine andere Taste sein; bei zwei ausgewählten Tasten bilden diese das Paar.", "The opposite key must be a different key; when two keys are selected, those keys form the pair."),
            Pair("Nicht bearbeitbare Eigenschaft: ", "This setting cannot be edited: "),
            Pair("Tastenindex muss in 0..255 liegen.", "The key index must be between 0 and 255."),
            Pair("Eine Taste ist mehrfach ausgewaehlt.", "The same key is selected more than once."),
            Pair("Zu viele ausgewaehlte Tasten.", "Too many keys are selected."),
            Pair("Mindestens eine Zieltaste auswaehlen.", "Select at least one destination key."),
            Pair("Leere oder doppelte BindingId ausgewaehlt.", "A selected mapping ID is empty or duplicated."),
            Pair("Zu viele ausgewaehlte Bindings.", "Too many mappings are selected."),
            Pair("Mindestens ein Binding auswaehlen.", "Select at least one mapping."),
            Pair("Mindestens eine ausgewaehlte BindingId fehlt.", "At least one selected mapping ID is missing."),
            Pair("Kurvenpunkte fehlen.", "Curve points are missing."),
            Pair("Leerer Kurvenpunkt.", "A curve point is empty."),
            Pair("Maximal 64 Kurvenpunkte erlaubt.", "A curve can contain at most 64 points."),
            Pair("Die Zwischenablage muss genau eine physische Quelltaste enthalten.", "The clipboard must contain exactly one physical source key."),
            Pair("Die Quelltaste hat keine Zuordnung.", "The source key has no mapping."),
            Pair("Unbekannte Kopiergruppe.", "Unknown copy group."),
            Pair("Die kopierten Zuordnungen enthalten dasselbe Ziel für den gewählten Controller mehrfach.", "The copied mappings contain the same output more than once for the selected controller."),
            Pair("Zieltaste ", "Destination key "),
            Pair(" hat keine bestehenden Zuordnungen; nur Alles darf dort neue anlegen.", " has no existing mappings; only Everything can create new mappings there."),
            Pair("Einfuegen wuerde mehr als 4096 Bindings erzeugen.", "Pasting would create more than 4096 mappings."),
            Pair("Mehrdeutiges Zahlenformat.", "The number format is ambiguous."),
            Pair("Eine Zahl wird erwartet.", "Enter a number."),
            Pair("Zahlen muessen endlich sein.", "Numbers must be finite."),
            Pair("Genau ein Auswahlwert wird erwartet.", "Choose exactly one value."),
            Pair("Unbekannter Auswahlwert.", "Unknown selection value."),
            Pair("Ganzzahliger Auswahlwert erwartet.", "The selection value must be a whole number."),
            Pair("Passender Auswahlwert erwartet.", "A matching selection value is required."),
            Pair("Enabled erwartet true oder false.", "Enabled requires true or false."),
            Pair("CustomPoints erwartet eine Kurvenpunktliste.", "CustomPoints requires a list of curve points."),
            Pair("Unbekannte Eigenschaft.", "Unknown setting."),
            Pair("Unbekanntes Ausgabeziel.", "Unknown controller output."),
            Pair("Die Zuordnung würde die Grenze von 4096 Einträgen überschreiten.", "This mapping would exceed the limit of 4096 entries."),
            
            // Core/MappingEngine.cs and PreviewComposer.cs
            Pair("Signalzustand fehlt.", "Signal state is missing."),
            Pair("Zeitdifferenz muss endlich und nichtnegativ sein.", "Elapsed time must be finite and non-negative."),
            Pair("Ungueltiger vorheriger Signalzustand.", "The previous signal state is invalid."),
            Pair("Die Reihenfolge der Tastendrücke muss zurückgesetzt werden.", "The key press sequence must be reset."),
            Pair("Kein aktueller Rohwert fuer Tastenindex ", "No current raw value for key index "),
            Pair("Keine Kalibrierung fuer Tastenindex ", "No calibration for key index "),
            Pair("Ungültiger vorheriger Zustand der physischen Taste.", "The previous physical key state is invalid."),
            Pair("Dauerhafter Zustand für physische Tasteinstellungen fehlt.", "Persistent state for physical key settings is missing."),
            Pair("Mehrere Controller müssen vor der Verarbeitung einzeln zugeordnet werden.", "Multiple controllers must be routed individually before processing."),
            Pair("Zustandsverzeichnis fehlt.", "The state collection is missing."),
            Pair("Zustandsverzeichnis für physische Tasten fehlt.", "The physical key state collection is missing."),
            Pair("Ungueltige Zeitdifferenz.", "Elapsed time is invalid."),
            Pair("Mehrere Controller müssen vor der Vorschau einzeln zugeordnet werden.", "Multiple controllers must be routed individually before previewing."),
            Pair("Zustandsverzeichnis für die Vorschau fehlt.", "The preview state collection is missing."),
            Pair("Zustandsverzeichnis für physische Vorschautasten fehlt.", "The physical key preview state collection is missing."),
            Pair("Ungültige Zeitdifferenz für die Vorschau.", "Elapsed time for the preview is invalid."),
            Pair("Eine Gegentaste hat keinen gültigen aktuellen Eingabewert.", "An opposite key has no valid current input value."),
            Pair("Kein aktueller Eingabewert für diese Taste.", "No current input value for this key."),
            
            // Core/MappingValidation.cs
            Pair("Tastenkombination für den Moduswechsel fehlt.", "The mode-switch shortcut is missing."),
            Pair("Eine Taste in 1..255 außer reinen Umschalttasten ist erforderlich.", "A key code between 1 and 255 is required; modifier keys alone are not allowed."),
            Pair("Ungültige Zusatztasten für den Moduswechsel.", "The mode-switch shortcut has invalid modifiers."),
            Pair("Ruhe- und Endwert muessen endlich, verschieden und numerisch auswertbar sein.", "Rest and full-press values must be finite, different and numerically usable."),
            Pair("Optionale Rohwertgrenzen muessen gemeinsam angegeben werden.", "Both optional raw-value limits must be provided together."),
            Pair("Rohwertgrenzen muessen geordnet sein und beide Kalibrierpunkte enthalten.", "Raw-value limits must be ordered and include both calibration points."),
            Pair("Signalverarbeitung fehlt.", "Signal settings are missing."),
            Pair("Unbekannte Kurvenart.", "Unknown curve type."),
            Pair("Nicht unterstuetzte Profilversion.", "Unsupported profile version."),
            Pair("Moduswechsel und Abschalter benötigen verschiedene Tastenkombinationen, wenn beide eingeschaltet sind.", "Mode switching and emergency stop must use different shortcuts when both are enabled."),
            Pair("Unbekannte Stickform.", "Unknown stick shape."),
            Pair("Unbekannte Gegenrichtungsregel.", "Unknown opposite-direction rule."),
            Pair("Unbekannte Zusammenfassung.", "Unknown input aggregation method."),
            Pair("Unbekannter Controllertyp.", "Unknown controller type."),
            Pair("Unbekannter Modus für die Tastensperre.", "Unknown keyboard suppression mode."),
            Pair("Die Farbe der Modustaste muss ein RGB-Wert in 0..16777215 sein.", "The mode-switch key color must be an RGB value between 0 and 16777215."),
            Pair("Controller-Liste fehlt oder überschreitet 32 Einträge.", "The controller list is missing or contains more than 32 entries."),
            Pair("Leerer Controller-Eintrag.", "A controller entry is empty."),
            Pair("Controller-ID muss gültig und eindeutig sein.", "Controller IDs must be valid and unique."),
            Pair("Controller-Namen müssen eindeutig sein und 1..64 Zeichen enthalten.", "Controller names must be unique and contain 1 to 64 characters."),
            Pair("Unbekannter Controllertyp im Controller-Eintrag.", "A controller entry has an unknown controller type."),
            Pair("Controllerfarbe muss ein RGB-Wert in 0..16777215 sein oder die Standardfarbe verwenden.", "The controller color must be an RGB value between 0 and 16777215, or use the default color."),
            Pair("Der Hauptcontroller stimmt nicht mit dem bisherigen Controllertyp überein.", "The main controller does not match the legacy controller type."),
            Pair("Liste unterdrückter Tastatureingaben fehlt oder überschreitet 256 Einträge.", "The suppressed key list is missing or contains more than 256 entries."),
            Pair("Unterdrückte Tastenindizes müssen eindeutig in 0..255 liegen.", "Suppressed key indices must be unique and between 0 and 255."),
            Pair("Tasteinstellungen fehlen oder überschreiten 256 Einträge.", "Key settings are missing or contain more than 256 entries."),
            Pair("Eine physische Taste hat mehrere Eingabeeinstellungen.", "A physical key has more than one input settings entry."),
            Pair("Bindings fehlen oder ueberschreiten 4096 Eintraege.", "Mappings are missing or contain more than 4096 entries."),
            Pair("Leeres Binding.", "A mapping entry is empty."),
            Pair("BindingId muss nichtleer, hoechstens 128 Zeichen lang und eindeutig sein.", "Mapping IDs must be non-empty, unique and at most 128 characters long."),
            Pair("Zuordnung verweist auf einen unbekannten Controller.", "A mapping refers to an unknown controller."),
            Pair("Dieselbe Taste darf demselben Ziel desselben Controllers nur einmal zugeordnet sein.", "The same key can only be mapped once to the same output on the same controller."),
            Pair("Tasteinstellung fehlt.", "Key settings are missing."),
            Pair("Unbekannte Vorrangregel für Gegentasten.", "Unknown priority rule for opposite keys."),
            
            // Core/ProfileJson.cs and SignalPreset.cs
            Pair("Profil-JSON ist zu gross.", "The profile JSON is too large."),
            Pair("Profil-JSON fehlt oder ist zu gross.", "The profile JSON is missing or too large."),
            Pair("Ungueltiges Profil-JSON: ", "Invalid profile JSON: "),
            Pair("Ein Profil muss genau ein JSON-Objekt sein.", "A profile must contain exactly one JSON object."),
            Pair("Profil-JSON ist zu tief verschachtelt.", "The profile JSON is nested too deeply."),
            Pair("Weitere Daten hinter dem Profil-JSON.", "Unexpected data follows the profile JSON."),
            Pair("Unvollstaendiges Profil-JSON.", "The profile JSON is incomplete."),
            Pair("Falscher JSON-Datentyp; erwartet: ", "Incorrect JSON type; expected: "),
            Pair("Nicht unterstuetzte JSON-Metadaten.", "Unsupported JSON metadata."),
            Pair("Verschachtelter Wert statt Skalar.", "A simple value was expected instead of a nested value."),
            Pair("JSON-Zahlen muessen endlich sein.", "JSON numbers must be finite."),
            Pair("Ungueltiger Arrayeintrag.", "An array entry is invalid."),
            Pair("Ungültiger Tastenindex-Arrayeintrag.", "A key-index array entry is invalid."),
            Pair("Unterdrückte Tastenindizes müssen eindeutige ganze Zahlen in 0..255 sein.", "Suppressed key indices must be unique whole numbers between 0 and 255."),
            Pair("Doppeltes oder ungueltiges JSON-Feld.", "A JSON field is duplicated or invalid."),
            Pair("Die Farbe der Modustaste muss eine ganze RGB-Zahl in 0..16777215 sein.", "The mode-switch key color must be a whole RGB number between 0 and 16777215."),
            Pair("Die Tastensperre benötigt einen gültigen Modus (0, 1 oder 2).", "Keyboard suppression requires a valid mode (0, 1 or 2)."),
            Pair("Unbekanntes Profilfeld: ", "Unknown profile field: "),
            Pair("Tastenkombinationen benötigen ganze Zahlen.", "Shortcut settings require whole numbers."),
            Pair("Unbekanntes Tastenkombinationsfeld: ", "Unknown shortcut field: "),
            Pair("Unbekanntes Bindingfeld: ", "Unknown mapping field: "),
            Pair("Controllerfarbe muss eine ganze RGB-Zahl in 0..16777215 sein.", "The controller color must be a whole RGB number between 0 and 16777215."),
            Pair("Unbekanntes Controllerfeld: ", "Unknown controller field: "),
            Pair("Unbekanntes Tasteinstellungsfeld: ", "Unknown key settings field: "),
            Pair("Unbekanntes Verarbeitungsfeld: ", "Unknown signal settings field: "),
            Pair("Unbekanntes Kurvenpunktfeld: ", "Unknown curve point field: "),
            Pair("2..1000 Profilsnapshots erlaubt.", "The profile history must allow between 2 and 1000 snapshots."),
            Pair("Signal-Preset ist zu gross.", "The signal preset is too large."),
            Pair("Ein Signal-Preset muss genau ein JSON-Objekt sein.", "A signal preset must contain exactly one JSON object."),
            Pair("Signal-Preset ist zu tief verschachtelt.", "The signal preset is nested too deeply."),
            Pair("Weitere Daten hinter dem Signal-Preset.", "Unexpected data follows the signal preset."),
            Pair("Unvollstaendiges Signal-Preset.", "The signal preset is incomplete."),
            Pair("Falscher JSON-Datentyp im Signal-Preset.", "A signal preset contains an incorrect JSON type."),
            Pair("Nicht unterstuetzte JSON-Metadaten im Signal-Preset.", "The signal preset contains unsupported JSON metadata."),
            Pair("Unbekanntes oder doppeltes Feld im Signal-Preset.", "The signal preset contains an unknown or duplicate field."),
            Pair("Signal-Preset erfordert Version, Name und Settings.", "A signal preset requires Version, Name and Settings."),
            Pair("Signal-Preset fehlt oder ist zu gross.", "The signal preset is missing or too large."),
            Pair("Ungueltiges Signal-Preset: ", "Invalid signal preset: "),
            
            // App/WorkspaceStore.cs
            Pair("Eine gueltige SHA-256-Kennung ist erforderlich.", "A valid SHA-256 identifier is required."),
            Pair("Ohne Seriennummer oder Windows-Geraetepfad ist keine physische Kalibrierungskennung moeglich.", "A physical calibration identifier requires a serial number or Windows device path."),
            Pair("Konfigurationsdatei ist zu gross.", "The configuration file is too large."),
            Pair("Ungueltiges Konfigurations-JSON.", "The configuration JSON is invalid."),
            Pair("Falscher JSON-Feldtyp oder unbekannte Metadaten.", "A JSON field has an incorrect type or unknown metadata."),
            Pair("Unbekanntes oder doppeltes JSON-Feld.", "A JSON field is unknown or duplicated."),
            Pair("Erforderliches JSON-Feld fehlt: ", "A required JSON field is missing: "),
            Pair("Ein ganzzahliges JSON-Feld ist erforderlich.", "A whole-number JSON field is required."),
            Pair("Ungueltiges numerisches JSON-Feld.", "A numeric JSON field is invalid."),
            Pair("Numerisches Feld liegt ausserhalb des erlaubten Bereichs.", "A numeric field is outside the allowed range."),
            Pair("Ungueltiges numerisches Feld.", "A numeric field is invalid."),
            Pair("Unbekannte Kalibrierungsversion.", "Unknown calibration version."),
            Pair("Zu viele Tastenkalibrierungen.", "Too many key calibrations."),
            Pair("Tastenindex ausserhalb 0..255.", "The key index is outside the range 0 to 255."),
            Pair("SensorOffset muss dem Ruhewert entsprechen.", "SensorOffset must match the rest value."),
            Pair("Kalibrierung passt nicht zu diesem Geraet und Protokoll.", "The calibration does not match this device and protocol."),
            Pair("Ungueltige oder doppelte Tastenkalibrierung.", "A key calibration is invalid or duplicated."),
            Pair("Unbekannte Version der Programmregeln.", "Unknown application rules version."),
            Pair("Zu viele Programmregeln.", "Too many application rules."),
            Pair("Ungueltige Programmregeln.", "The application rules are invalid."),
            Pair("Programmregeln muessen eindeutig sein.", "Application rules must be unique."),
            Pair("Ein klarer EXE-Name oder absoluter EXE-Pfad ist erforderlich.", "An unambiguous EXE filename or absolute EXE path is required."),
            Pair("Relative Laufwerkspfade sind nicht erlaubt.", "Drive-relative paths are not allowed."),
            Pair("Relative Programmpfade sind nicht erlaubt.", "Relative application paths are not allowed."),
            Pair("Nur lokale Profilnamen sind erlaubt.", "Only local profile filenames are allowed."),
            Pair("Reservierte Windows-Geraetenamen sind keine Profile.", "Reserved Windows device names cannot be used as profile filenames."),
            
            // App/MonitorWire.cs and SignedMonitorAccess.cs
            Pair("Steuerzeichen in der Antwort des Tastatur-Zugriffs.", "The keyboard helper response contains control characters."),
            Pair("Zu lange Antwort des Tastatur-Zugriffs.", "The keyboard helper response is too long."),
            Pair("Unvollständige Antwort des Tastatur-Zugriffs.", "The keyboard helper response is incomplete."),
            Pair("Unbekannte Tastatur-Modellkennung.", "Unknown keyboard model identifier."),
            Pair("Unbekanntes Format der Druckwerte.", "Unknown pressure data format."),
            Pair("Ungültige Druckwerte.", "The pressure data is invalid."),
            Pair("Ungültige Fehlermeldung des Tastatur-Zugriffs.", "The keyboard helper returned an invalid error message."),
            Pair("Der signierte Tastatur-Zugriff fehlt oder kann nicht gelesen werden.", "The signed keyboard helper is missing or cannot be read."),
            Pair("Windows erlaubt keinen Lesezugriff auf den Tastatur-Helfer.", "Windows does not allow the keyboard helper file to be read."),
            Pair("Der Pfad zum Tastatur-Helfer ist ungültig.", "The keyboard helper path is invalid."),
            Pair("Der Pfad zum Tastatur-Helfer wird nicht unterstützt.", "The keyboard helper path is not supported."),
            Pair("Die Windows-Signaturprüfung ist auf diesem System nicht verfügbar.", "Windows signature verification is unavailable on this system."),
            Pair("Ein vollständiger lokaler Dateipfad ist erforderlich.", "A full local file path is required."),
            Pair("Der Tastatur-Helfer muss eine EXE-Datei sein.", "The keyboard helper must be an EXE file."),
            Pair("Die Signatur des Tastatur-Helfers passt nicht zu seiner Datei. Die automatische Druckwert-Erfassung bleibt aus.", "The keyboard helper signature does not match its file. Automatic pressure input remains off."),
            Pair("Windows konnte die Signatur des Tastatur-Helfers nicht als vertrauenswürdig bestätigen. Die automatische Druckwert-Erfassung bleibt aus.", "Windows could not verify that the keyboard helper signature is trusted. Automatic pressure input remains off."),
            Pair("Tastenlernen – Controller aus", "Key identification – controller off"),
            Pair("Kalibrierung – Controller aus", "Calibration – controller off"),
            Pair("Profil gelöscht", "Profile deleted"), Pair("Profilwechsel fehlgeschlagen", "Profile switch failed"),
            Pair("Abschalter – Controller aus", "Off shortcut – controller off"), Pair("Manuell deaktiviert", "Disconnected manually"),
            Pair("Anwendung wird geschlossen", "Closing application"), Pair("Anwendung beendet", "Application closed"),
            Pair("Ruhezustand – Controller aus", "Sleep – controller off"), Pair("Eingabefehler – Controller aus", "Input error – controller off"),
            Pair("Tastensperre beendet", "Keyboard suppression stopped"), Pair("Tastensperre bereit", "Keyboard suppression ready"), Pair("Startet", "Starting"),
            Pair("Tastensperre konnte nicht starten: ", "Keyboard suppression could not start: "),
            Pair("Tastensperre inaktiv; Entfernen nicht bestätigt: ", "Keyboard suppression is inactive; removal could not be confirmed: "),
            Pair("Windows konnte die Tastensperre nicht einrichten.", "Windows could not set up keyboard suppression."),
            Pair("Tastensperre-Nachrichten konnten nicht gelesen werden.", "Keyboard suppression messages could not be read."),
            Pair("Tastensperre nicht verfügbar: ", "Keyboard suppression is unavailable: "),
            Pair("Tastensperre abgebrochen: ", "Keyboard suppression stopped: "),
            Pair("Nur lesen - Controller aus", "Reading input – controller off"),
            Pair("Die Tastatur liest keine aktuellen Eingabedaten.", "The keyboard is not reading current input data."),
            Pair("Beide Eingabefunktionen gemeinsam angeben.", "Both input functions must be provided together."),
            Pair("Diese Ausgabefunktion unterstützt nur Xbox 360. Für andere Controllertypen ist eine typisierte Ausgabefunktion erforderlich.", "This output function supports Xbox 360 only. Other controller types need a matching output function."),
            Pair("Controllerverbindung beim Neutralisieren verloren.", "The controller connection was lost while releasing its outputs."),
            Pair("Tastaturmodus – Controller verbunden und neutral", "Keyboard mode – controller connected and neutral"),
            Pair("Aktuelle Eingabedaten fehlen: ", "Current input data is missing: "),
            Pair("Controllerverbindung wurde unterbrochen.", "The controller connection was interrupted."),
            Pair("Moduswechsel fehlgeschlagen – Controller aus: ", "Mode switch failed – controller off: "),
            Pair("Geraetewechsel - Controller aus", "Device changed – controller off"),
            Pair("Einstellungen geaendert - Controller aus", "Settings changed – controller off"),
            Pair("Ungueltige Tastenkalibrierung.", "Invalid key calibration."),
            Pair("Einstellungen ungueltig - Controller aus: ", "Invalid settings – controller off: "),
            Pair("Eingabeverbindung wurde unterbrochen.", "The input connection was interrupted."),
            Pair("Ein zuvor gelesener Tastenwert ist nicht mehr aktuell (Taste ", "A previous pressure reading is no longer current (key "),
            Pair("Das Profil hat keine aktive Zuordnung.", "This profile has no active mapping."),
            Pair("Die Eingabeeinstellungen sind noch nicht bereit. ", "The input settings are not ready yet. "),
            Pair("Virtueller Controller aktiv", "Virtual controller connected"),
            Pair("Controllerstatus fehlerhaft - Controller aus: ", "Controller status error – controller off: "),
            Pair("Controllerverbindung verloren - Controller aus", "Controller connection lost – controller off"),
            Pair("Controller-Backend fehlt.", "The controller component is missing."),
            Pair("Controller-Backend hat keine Verbindung bestaetigt.", "The controller component did not confirm a connection."),
            Pair("Controllerverbindung wurde beim Aktivieren verloren.", "The controller connection was lost during startup."),
            Pair("Controller aus: ", "Controller off: "),
            Pair("Eingabeverbindung unterbrochen - Controller aus", "Input connection interrupted – controller off"),
            Pair("Eingabe- oder Controllerverbindung wurde unterbrochen.", "The input or controller connection was interrupted."),
            Pair("Eingabe- oder Controllerverbindung beim Neutralisieren verloren.", "The input or controller connection was lost while releasing outputs."),
            Pair("Eingabedaten fehlen oder sind veraltet - Controller aus", "Input data is missing or stale – controller off"),
            Pair("Controllerverbindung beim Senden verloren.", "The controller connection was lost while sending output."),
            Pair("Daten- oder Ausgabefehler - ", "Data or output error – "),
            Pair("Die verbundene Tastatur bietet noch keinen RGB-Lesezugriff.", "The connected keyboard does not provide lighting read access yet."),
            Pair("Die Tastaturverbindung hat sich während der RGB-Sicherung geändert.", "The keyboard connection changed while backing up its lighting."),
            Pair("Die verbundene Tastatur bietet noch keinen RGB-Schreibzugriff.", "The connected keyboard does not provide lighting write access yet."),
            Pair("Die Tastaturverbindung hat sich während der RGB-Änderung geändert; Sicherung behalten.", "The keyboard connection changed while updating its lighting; the backup has been kept."),
            Pair("Kein eindeutiger Decoder fuer diese Input-Collection.", "No unambiguous decoder is available for this input interface."),
            Pair("Lesen konnte nicht gestartet werden: ", "Input could not be started: "),
            Pair("Die Inputquelle fehlt.", "The input source is missing."), Pair("Unbekanntes Inputformat.", "Unknown input format."),
            Pair("Inputquelle konnte nicht sauber geschlossen werden: ", "The input source could not be closed cleanly: "),
            Pair("Anhalten ausstehend; Eingabewerte sind unbekannt.", "Stopping input; pressure readings are unavailable."),
            Pair("Moduswechsel konnte nicht für alle Controller bestätigt werden.", "The mode switch could not be confirmed for all controllers."),
            Pair("Einstellungen geändert – alle Controller aus", "Settings changed – all controllers off"),
            Pair("Controller-Einrichtung fehlgeschlagen: ", "Controller setup failed: "),
            Pair("Tastaturverbindung geändert – alle Controller aus", "Keyboard connection changed – all controllers off"),
            Pair("Der aktuelle Xbox-Ausgabeweg unterstützt zunächst einen verbundenen Controller. Trenne den anderen Controller, um diesen zu verwenden.", "The current Xbox output supports one connected controller. Disconnect the other controller to use this one."),
            Pair("Es können höchstens vier Xbox-Controller gleichzeitig verbunden sein. Trenne zuerst einen anderen Controller.", "Up to four Xbox controllers can be connected at once. Disconnect another controller first."),
            Pair("Controller-Verbindung fehlgeschlagen", "Controller connection failed"),
            Pair("Controller konnten nicht vollständig getrennt werden.", "Some controllers could not be disconnected."),
            Pair("Controller nach Verbindungsfehler beendet. Einstellungen erneut anwenden: ", "The controller stopped after a connection error. Apply the settings again: "),
            Pair("Inputfehler: ", "Input error: "), Pair("Taste kalibrieren · ", "Calibrate key · "), Pair("Taste erkennen · ", "Identify key · "),
            Pair("Taste ", "Key "), Pair(" Tasten", " keys"), Pair(" Taste", " key"), Pair("Zuordnungen", "mappings"), Pair("Zuordnung", "mapping"), Pair("Tasten gesehen", "keys seen"),
            Pair("Input-Collections gefunden.", "input interfaces found."), Pair("F8 bereit.", "F8 ready."), Pair("F8 nicht verfügbar.", "F8 unavailable."),
            Pair("Linker Stick-Klick", "Left stick click"), Pair("Rechter Stick-Klick", "Right stick click"),
            Pair("Linker Stick", "Left stick"), Pair("Rechter Stick", "Right stick"), Pair("Linker Trigger", "Left trigger"), Pair("Rechter Trigger", "Right trigger"),
            Pair("Steuerkreuz", "D-pad"), Pair("rechts", "right"), Pair("links", "left"), Pair("oben", "up"), Pair("unten", "down"),
            Pair("Druck erkannt · Rohwert ", "Pressure detected · raw value "), Pair("Druck · ", "Pressure · "),
            Pair("Letzter Wert: ", "Last value: "), Pair(" · veraltet", " · stale"), Pair(" · alt", " · stale"),
            Pair("Noch keine Messwerte für ", "No readings yet for "), Pair("Messwert ", "Reading "), Pair("Bisheriger Bereich ", "Observed range "),
            Pair(" von 2 Durchgängen abgeschlossen", " of 2 passes completed"), Pair("Durchgang ", "Pass "), Pair(" von 2", " of 2"),
            Pair(" erkannt · ", " detected · "), Pair("Jetzt vollständig loslassen.", "Now release fully."),
            Pair("Jetzt nur ", "Now press only "), Pair("Drücke jetzt nur ", "Now press only "), Pair("Nur ", "Only "),
            Pair(" langsam bis zum Anschlag drücken und vollständig loslassen.", " slowly all the way down, then release fully."),
            Pair(" noch einmal langsam drücken und vollständig loslassen.", " slowly press and release fully once more."),
            Pair(" zweimal langsam drücken und vollständig loslassen.", " slowly press and release fully twice."),
            Pair("Du bestimmst das Tempo.", "Go at your own pace."), Pair("Erster Durchgang geschafft.", "First pass completed."),
            Pair("Fertig. Anschlag bestätigen und übernehmen.", "Done. Confirm full presses, then apply."),
            Pair("Bis zum Anschlag drücken, dann vollständig loslassen.", "Press all the way down, then release fully."),
            Pair("Noch einmal langsam bis zum Anschlag drücken und loslassen.", "Slowly press all the way down once more, then release."),
            Pair("Zu wenige unterschiedliche Druckwerte. Bitte langsamer neu aufnehmen.", "Not enough distinct readings. Try again more slowly."),
            Pair("Der Tastatur-Helfer ist noch nicht digital signiert.", "The keyboard helper is not digitally signed yet."),
            Pair("Die automatische Druckwert-Erfassung kann deshalb nicht starten.", "Automatic pressure input cannot start yet."),
            Pair("Prüfcode", "Verification code"), Pair("Die ausgewählte Tastatur fehlt.", "The selected keyboard is unavailable."),
            Pair("Ausgabe gesperrt", "Output unavailable"), Pair("Vorschau", "Preview"), Pair("Profil: ", "Profile: "),
            Pair("Kalibrierung fehlt.", "Calibration is missing."), Pair("Profil fehlt.", "Profile is missing."),
            Pair("Rohwert fehlt oder ist nicht endlich.", "A pressure reading is missing or invalid."),
            Pair("Deadzonen muessen in 0..1 liegen und zusammen kleiner als 1 sein.", "Top and bottom deadzones must be between 0 and 1, and add up to less than 1."),
            Pair("Ausgabegrenzen muessen geordnet in 0..1 liegen.", "Output limits must be between 0 and 1, with the minimum no greater than the maximum."),
            Pair("Hysterese muss nichtnegativ und kleiner als der nutzbare Eingabebereich sein.", "The switch gap must be non-negative and smaller than the usable input range."),
            Pair("Exponent muss endlich und positiv sein.", "Curve strength must be a finite positive number."),
            Pair("Skalierung muss endlich und nichtnegativ sein.", "Output strength must be a finite non-negative number."),
            Pair("Glaettungszeit muss endlich und nichtnegativ sein.", "Smoothing time must be a finite non-negative number."),
            Pair("Button-Schwelle muss in (0,1] liegen.", "The button activation point must be above 0 and no greater than 1."),
            Pair("Ausgabe-Deadzone muss in 0..1 liegen.", "The threshold for ignoring small outputs must be between 0 and 1."),
            Pair("Ein gemessener physischer Hub muss endlich und positiv sein.", "Measured key travel must be a finite positive number."),
            Pair("Profilname muss 1..128 Zeichen enthalten.", "The profile name must contain 1 to 128 characters."),
            Pair("Eine benutzerdefinierte Kurve braucht 2..64 Punkte.", "A custom curve needs 2 to 64 points."),
            Pair("Kurvenpunkte muessen endlich in 0..1 liegen.", "Curve coordinates must be finite numbers between 0 and 1."),
            Pair("Die Kurve muss bei (0,0) beginnen.", "The curve must start at (0,0)."),
            Pair("Die Kurve muss bei (1,1) enden.", "The curve must end at (1,1)."),
            Pair("Kurven-X muss strikt steigen; Kurven-Y darf nicht fallen.", "Curve points must move to the right and may not move down."),
            Pair("Kurventangenten muessen endlich und nichtnegativ sein; leer bedeutet automatisch.", "Curve handle slopes must be finite and nonnegative; leave them empty for automatic smoothing."),
            Pair("Der Auslösepunkt muss größer als 0 und höchstens 100 % sein.", "The actuation point must be above 0 and no greater than 100%."),
            Pair("Der erneute Druckweg muss größer als 0 und höchstens 100 % sein.", "Retrigger movement must be above 0 and no greater than 100%."),
            Pair("Der Loslassweg muss größer als 0 und höchstens 100 % sein.", "Release movement must be above 0 and no greater than 100%."),
            Pair("Gegentasten müssen gegenseitig mit derselben Vorrangregel verbunden sein.", "Opposite keys must point to each other and share one priority rule."),
            Pair("Eine Gegentaste muss eine andere gültige physische Taste sein.", "The opposite key must be a different valid physical key."),
            Pair("Für dieses Reportformat fehlt ein bestätigter Adapter. Das Diagnoseprogramm kann Rohreports aufzeichnen.", "No validated adapter is available for this input format. The diagnostic tool can record raw reports."),
            Pair("Das Controller-Ausgabeprogramm fehlt. Bitte die App vollständig aktualisieren.", "The controller output helper is missing. Please update the complete application."),
            Pair("PS5-Ausgabe noch nicht bereit: Der DualSense-Ausgabeweg benötigt den freigegebenen USB/IP-Treiber und einen bestandenen Windows-HID-Start-/Abschalttest.", "PS5 output is not ready: the DualSense output path needs the approved USB/IP driver and a passed Windows HID startup/shutdown test."),
            Pair("¹ mm: lineare Schätzung aus selbst gemessenem Gesamthub. Alte/fehlende Werte erzeugen keine Controller-Ausgabe.", "¹ mm: a linear estimate based on the travel you measured. Stale or missing values produce no controller output.")
        };
        static KeyValuePair<string, string> Pair(string a, string b) { return new KeyValuePair<string, string>(a, b); }
        static string TranslateTracked(object item, string text)
        {
            TextOrigin origin = Origins.GetValue(item, delegate { return new TextOrigin(); });
            if (origin.Original == null || text != origin.Displayed) origin.Original = text;
            return origin.Displayed = Get(origin.Original);
        }
        public static void Apply(Control root)
        {
            if (root == null || root.IsDisposed) return;
            // Textboxes may hold user-owned content. Only explicitly marked help is translated.
            object preserved;
            if (!PreservedText.TryGetValue(root, out preserved) && (!(root is TextBoxBase) || (root is TextBox && ((TextBox)root).ReadOnly && root.Tag as string == "localized")) && !(root is ComboBox) && !(root is DataGridView))
            {
                string value = TranslateTracked(root, root.Text);
                if (value != root.Text) root.Text = value;
            }
            var strip = root as ToolStrip;
            if (strip != null) ApplyItems(strip.Items);
            var grid = root as DataGridView;
            if (grid != null) foreach (DataGridViewColumn column in grid.Columns)
            { string value = TranslateTracked(column, column.HeaderText); if (value != column.HeaderText) column.HeaderText = value; }
            foreach (Control child in root.Controls) Apply(child);
        }
        // Already localized text containing user-owned names must not be processed twice.
        public static void PreserveText(Control control)
        { PreservedText.GetValue(control, delegate { return new object(); }); }
        static void ApplyItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                string value = TranslateTracked(item, item.Text); if (value != item.Text) item.Text = value;
                var nested = item as ToolStripDropDownItem; if (nested != null) ApplyItems(nested.DropDownItems);
            }
        }
    }

    public static class UiPreferences
    {
        public static string LoadLanguage(string dataPath)
        {
            string path = Path.Combine(dataPath, "ui-language.txt");
            try { if (File.Exists(path)) { string saved = File.ReadAllText(path).Trim(); if (saved == "de" || saved == "en") return saved; } }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return "en";
        }
        public static void SaveLanguage(string dataPath, string value)
        {
            if (value != "de" && value != "en") throw new ArgumentException("Unsupported language.");
            Directory.CreateDirectory(dataPath); string path = Path.Combine(dataPath, "ui-language.txt"), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, value + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
