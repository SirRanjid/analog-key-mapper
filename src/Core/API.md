# Verwalteter Mapping-Core

Namespace `Tk75.Mapping`, C# 5 / .NET Framework 4.5. Dateien `MappingModels.cs`,
`MappingValidation.cs`, `MappingEngine.cs`, `ProfileJson.cs` gemeinsam einbinden.
Assemblyreferenzen: `System.Runtime.Serialization.dll`, `System.Xml.dll` und
die Standardbibliotheken. Keine Windows-/HID-/XInput-Aufrufe, kein Timer,
keine Hooks, keine erzeugten Tastatureingaben. Tests: `tests/Test-MappingCore.ps1`.

## Daten und Verarbeitung

- `Profile`: `Version=1`, `Name`, `Bindings`, `StickShape` (`Circle`/`Square`),
  `OpposedPolicy` (`Neutral`/`Subtract`), `Aggregation` (`Maximum`/`ClampedSum`).
- `Binding`: eindeutige `BindingId`, `KeyIndex` 0..255, `Target` aus
  `OutputTarget`, `Enabled` und eigene `Processing` vom Typ `SignalSettings`.
  Beliebige Tastenplätze und mehrere Bindings je Taste sind zulässig.
- `Calibration`: getrennt vom Profil und vom Signalzustand. `Rest` und `Bottom`
  müssen explizit gesetzt sein. Umgekehrte Sensorrichtung ist erlaubt.
  `UsableMin`/`UsableMax` sind optionale gemeinsame Rohwertgrenzen, die beide
  Kalibrierpunkte enthalten müssen. `MeasuredTravel` ist optionale positive
  Messmetadaten; daraus wird hier kein Hubmaß berechnet.
- `SignalState`: genau ein eigener Zustand je `BindingId`. Wird vom Aufrufer
  gehalten. **Zugriff serialisieren und bei Profil-/Kalibrierungswechsel,
  Eingabeverlust, Trennung, Suspend oder deaktivierter Ausgabe zurücksetzen.**

`SignalProcessor.Process(raw, calibration, settings, state, dtSeconds)` liefert
`SignalResult`: `Normalized`, `AfterDeadzone`, `AfterCurve`, `Final`, `Error`
und `IsValid`. Ungültige Eingaben liefern 0 plus Fehler und setzen den Zustand
zurück. Es gibt keine angenommene Standardkalibrierung. Zeiten müssen explizit
als endliche, nichtnegative Sekunden übergeben werden.

Die Reihenfolge ist kalibrierte Normalisierung und Clamping, Hysterese-Gate,
Deadzonen, Kurve, Skalierung, Ausgabe-Deadzone, Ausgabegrenzen und optionale Glättung.
`TopDeadzone` ist die Deadzone am Ruhepunkt; `BottomDeadzone` sättigt am
Druckende. Beide sind normalisiert; ihre Summe muss kleiner als 1 sein.

`Hysteresis` ist eine zusätzliche normalisierte Einschaltschwelle oberhalb
der Ruhe-Deadzone. Aktiv wird der Eingang strikt oberhalb
`TopDeadzone + Hysteresis`; freigegeben wird bei `TopDeadzone` oder darunter.
Die Kurve erhält nach Aktivierung weiterhin den regulären Deadzone-Wert.

`Curve`: `Linear`, `Exponential` (`x^Exponent`), `Logarithmic`
(`log(1 + Exponent*x) / log(1+Exponent)`), `Smoothstep`, `Custom` oder `Bezier`.
`Exponent` ist endlich und positiv. `CustomPoints` enthält 2..64 Punkte,
beginnt bei (0,0), endet bei (1,1), hat strikt steigende X- und nicht fallende
Y-Werte in 0..1. `Custom` interpoliert zwischen Punkten linear. `Bezier`
verwendet kubische Segmente mit gemeinsamen Tangenten an den Knoten, sodass
die Übergänge glatt bleiben. Die optional gespeicherte `CurvePoint.Tangent`
ist eine nichtnegative, endliche Steigung (dy/dx); `null` lässt sie automatisch
bestimmen. Die wirksamen Tangenten werden auf die Nachbarpunkte begrenzt,
damit die Ausgabe monoton innerhalb von 0..1 bleibt. Griffe liegen in X bei
einem beziehungsweise zwei Dritteln des jeweiligen Segments. Punkte und
Tangenten werden auch bei gerade nicht gewählter eigener Kurve validiert.

Zunächst wird `AfterCurve * Scale` auf 0..1 begrenzt (`Scale` endlich und
nichtnegativ). `AfterCurve` selbst bleibt der unveränderte Kurvenwert.
`OutputDeadzone` wirkt danach in 0..1: Werte bis einschließlich dieser Grenze
ergeben sofort 0; darüber wird der Restbereich linear auf 0..1 gestreckt.
Eine Grenze von 1 macht die Ausgabe vollständig neutral. Eine ausgelöste
Ausgabe-Deadzone setzt auch den geglätteten Wert sofort auf 0. `Scale=0` ergibt
auch bei positivem `MinOutput` immer 0.

Erst für den verbleibenden positiven Wert wird das aktive Ausgabeziel berechnet:
`MinOutput + Restbereich * (MaxOutput-MinOutput)`. `MaxOutput` ist eine harte
Obergrenze je Binding, auch bei einer Skalierung über 1 und mit Glättung.
`MinOutput` bezeichnet das Mindestziel einer aktiven Ausgabe; Ruhe, Ausgabe-
Deadzone und eine auf 0 abgebildete Kurve bleiben weiterhin neutral.
`SmoothingTimeConstant` ist optional in Sekunden, Standard 0. Die exponentielle
Glättung verwendet `1-exp(-dt/tau)`; ein aktiver geglätteter Anstieg kann dabei
vorübergehend unter `MinOutput` liegen. **Loslassen setzt immer unmittelbar
auf 0, unabhängig von Minimum, Glättung und dt.** Keine Invertierung erzeugt
eine Ausgabe in Ruhe; eine Stick-Gegenrichtung ist ein eigenes Ausgabeziel.

## Zusammensetzen eines Controllerzustands

```csharp
ControllerFrame frame = MappingEngine.Compose(
    raw, calibrations, profile, states, dtSeconds);
```

`raw` hat den Typ `IDictionary<int,double>`, `calibrations` den Typ
`IDictionary<int,Calibration>` und `states` den Typ
`IDictionary<string,SignalState>`. `dtSeconds` ist eine `double`-Zeitspanne.

Der Aufruf erwartet veränderbare Zustands-Dictionaries und nur aktuelle
Rohwerte. Fehlt ein Rohwert oder seine Kalibrierung, wird das entsprechende
Binding neutral und in `Errors` genannt. Alte gecachte Rohwerte lassen sich
ohne Zeitstempel nicht als veraltet erkennen: Der Aufrufer muss sie entfernen
oder den kompletten Zustand mit `MappingEngine.ResetStates(states)` verwerfen.
Entfernte Bindings werden aus dem Zustandsverzeichnis entfernt. Ungültige
Profile ergeben einen vollständig neutralen Frame plus Fehler.

`ControllerFrame` enthält `LeftX`, `LeftY`, `RightX`, `RightY` (-1..1),
`LeftTrigger`, `RightTrigger` (0..1), `Buttons` (übliche XInput-ushort-Bits),
`Errors` und `BindingResults`. Positive Y bedeutet die positive XInput-Y-Achse.
Dies ist ein Datenobjekt; es wird kein virtueller Controller erzeugt.

Mehrere Beiträge auf derselben Stickrichtung bzw. demselben Trigger werden
standardmäßig über ihr Maximum, optional als begrenzte Summe kombiniert.
Wenn beide Stick-Gegenrichtungen positiv sind, wird bei `Neutral` 0 ausgegeben;
bei `Subtract` wird die Differenz gebildet. `Circle` begrenzt jedes Stickpaar
auf Radius 1, `Square` erhält volle Diagonalkomponenten.

Buttons verwenden die eigene `ButtonThreshold` je Binding, Standard 0,5;
die endgültige Bindungsausgabe muss positiv und mindestens so groß sein.
Mehrere aktivierte Bindings werden als OR kombiniert. Gegenläufige Dpad-Bits
werden paarweise neutralisiert. Die Aggregationsoption verändert Buttons nicht.

## Profil-JSON und Bearbeitung

`ProfileJson.Serialize(profile)`, `Deserialize(json)` und `Clone(profile)`
arbeiten ausschließlich mit dem Profil, nie mit Hardwarekalibrierungen oder
Laufzeitzuständen. Enumwerte sind in JSON Ganzzahlen ihrer Deklarationsreihenfolge.
Die Profilversion muss 1 sein. Fehlende optionale Verarbeitungseinstellungen
erhalten die dokumentierten neutralen Defaults. Unbekannte/doppelte Felder,
falsche JSON-Typen, NaN/Infinity, fehlende Pflichtfelder, ungültige Einstellungen
und unbekannte Versionen/Enumwerte werden abgelehnt. Grenze: 4 MiB Zeichen,
4096 Bindings, 64 Custom-Punkte je Binding, maximal 20 JSON-Strukturebenen.
Das ist eine funktionale Implementierung mit Validierung je Compose-Aufruf;
eine bestimmte Latenz, Abtastrate oder Speicherallokationsrate ist nicht gemessen.

`ProfileEditing.ApplySettings(profile, bindingIds, preset)` erzeugt atomar ein
neues validiertes Profil mit tief kopierten Verarbeitungseinstellungen für
alle ausgewählten Bindings. Tastenplätze und Ausgabeziele bleiben erhalten.
Das ist zugleich der Kern für Copy/Paste eines Verarbeitungs-Presets.

`EditHistory(initialProfile, capacity=100)` speichert begrenzt Profilsnapshots.
`Commit`, `Undo`, `Redo`, `CanUndo`, `CanRedo`, `Current` unterstützen einzelne
oder gesamte Bulk-Transaktionen. `Current` liefert immer eine getrennte Kopie.
Kalibrierungen werden von Profil-Undo/Redo nicht berührt. Der Integrator muss
nach einer übernommenen Profiländerung die betreffenden Signalzustände resetten.
# Physical-key activation and opposing-key rules

`Profile.Inputs` is an optional version-1 member, defaulting to an empty list when
reading older profiles. An absent key retains its previous continuous-travel
behavior. A configured `KeyInputSettings` uses its fixed `ActuationPoint` even
when `RapidTriggerEnabled` is false. All three distances are calibrated fractions
in `(0,1]`; they are not inferred millimeters.

With rapid triggering, the first press reaches `ActuationPoint`. Thereafter the
largest observed depth is the release reference; backing off by `ReleaseMovement`
deactivates the key. The smallest subsequent observed depth is the repress
reference; pressing down by `PressMovement` reactivates it. Returning to calibrated
zero resets that cycle immediately. Gates do not synthesize movement, schedule
repeats or change the original depth. Closing a gate resets every target filter
for that physical key, bypassing minimum output and smoothing.

Opposite pairs must be reciprocal and have the same `InputOpposedPolicy` on both
keys. Their gates act before all per-binding curves/output processing. Neutral
suppresses both active keys. FirstPressed/LastPressed compare physical activation
transitions, including rapid-trigger reactivation. Inputs activating in the same
Compose frame have equal order and remain neutral; this is frame resolution, not
a claim of hardware-simultaneous measurement. Releasing one key exposes the still
physically active other key without creating a new press. Suppression does not
freeze its physical rapid-trigger tracking. An opponent without mappings is still
a required raw/calibrated input for its mapped partner.

The runtime owns `IDictionary<int, KeyInputState>` alongside its binding states.
Use `MappingEngine.Compose(raw, calibrations, profile, bindingStates, inputStates,
dtSeconds)` and call `ResetInputStates` wherever binding states reset, including
configuration changes, input loss and output disable. Legacy Compose is retained
only for profiles without configured Inputs. All relevant physical keys are
normalized exactly once per frame. Any invalid relevant input or processing state
neutralizes the entire frame and resets both state collections.

`ControllerFrame.InputResults` contains calibrated `Normalized`, `Active` before
opposition handling and `Allowed` after it, plus `Error`. A suppressed key remains
Active, so output arming must not mistake two opposed held keys for physical rest.
For keys without new input settings, preserve the existing per-binding rest-deadzone
arming check rather than treating every tiny nonzero normalized noise value as a
configured activation.

`KeyInputEditing.Configure` returns a validated profile copy, disconnecting old
pairs and creating the selected reciprocal pair atomically. `Remove` resets one
key to legacy behavior and unpairs its former partner without deleting the
partner's own activation settings. `GetOrDefault`/`Clone` return detached values.
`ApplyActivation` copies only rapid-trigger/activation distances to selected keys,
preserving each destination's pair. Existing binding clipboard operations retain
Profile.Inputs; callers can combine ApplyActivation in the same undo transaction
when copying an entire key. JSON profile cloning, history and ordinary signal
edits preserve physical input settings; signal-only presets never contain them.
