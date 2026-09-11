#requires -Version 5.1
<# Alle Werte sind synthetisch; kein HID, kein XInput, keine Treiber oder Hooks. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$core = Join-Path $workspace 'src\Core'
if ('Tk75.Mapping.Profile' -as [type]) { throw 'Bitte den Core-Test in einer neuen PowerShell-Sitzung starten, damit der aktuelle Quellcode geladen wird.' }
Add-Type -Path @(
    (Join-Path $core 'MappingModels.cs'), (Join-Path $core 'MappingValidation.cs'),
    (Join-Path $core 'BezierCurve.cs'), (Join-Path $core 'MappingEngine.cs'), (Join-Path $core 'ProfileJson.cs')
)
$script:assertions = 0
function Check([bool] $Condition, [string] $Message) {
    $script:assertions++
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
}
function Near([double] $Expected, [double] $Actual, [string] $Message, [double] $Tolerance = 0.000000001) {
    Check ((-not [double]::IsNaN($Actual)) -and [Math]::Abs($Expected - $Actual) -le $Tolerance) ($Message + ': ' + $Actual)
}
function Reject([scriptblock] $Action, [string] $Message) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    Check $caught $Message
}
function Binding([string] $Id, [int] $Index, [Tk75.Mapping.OutputTarget] $Target) {
    $value = [Tk75.Mapping.Binding]::new()
    $value.BindingId = $Id; $value.KeyIndex = $Index; $value.Target = $Target
    return $value
}
function Process([double] $Raw, $Calibration, $Settings, $State = $null, [double] $Dt = 0.01) {
    if ($null -eq $State) { $State = [Tk75.Mapping.SignalState]::new() }
    return [Tk75.Mapping.SignalProcessor]::Process($Raw, $Calibration, $Settings, $State, $Dt)
}

$cal = [Tk75.Mapping.Calibration]::new(100, 500)
$settings = [Tk75.Mapping.SignalSettings]::new()
Near 0 (Process 100 $cal $settings).Final 'Kalibrierter Ruhewert'
Near 0.5 (Process 300 $cal $settings).Normalized 'Kalibrierte Mitte'
Near 1 (Process 500 $cal $settings).Final 'Kalibrierter Endwert'
Near 0 (Process -100 $cal $settings).Final 'Clamping unter Ruhewert'
Near 1 (Process 900 $cal $settings).Final 'Clamping ueber Endwert'
$reverse = [Tk75.Mapping.Calibration]::new(500, 100)
Near 0.5 (Process 300 $reverse $settings).Final 'Umgekehrte Sensorrichtung'
Near 0 (Process 900 $reverse $settings).Final 'Umgekehrter Ruhebereich'
Near 1 (Process -100 $reverse $settings).Final 'Umgekehrter Endbereich'
$reverse.UsableMin = 0; $reverse.UsableMax = 600; $reverse.MeasuredTravel = 3.8
Near 0.5 (Process 300 $reverse $settings).Final 'Physischer Hub veraendert Rohwertnormalisierung nicht'
$invalidState = [Tk75.Mapping.SignalState]::new(); $invalidState.IsPressed = $true; $invalidState.SmoothedValue = 0.8
$bad = Process 300 $null $settings $invalidState
Check (-not $bad.IsValid) 'Fehlende Kalibrierung meldet Fehler'
Near 0 $bad.Final 'Keine angenommene Standardkalibrierung'
Near 0 $invalidState.SmoothedValue 'Fehler setzt Signalzustand zurueck'
Check (-not (Process 0 ([Tk75.Mapping.Calibration]::new()) $settings).IsValid) 'Uninitialisierte Kalibrierung abgelehnt'
Check (-not (Process 100 ([Tk75.Mapping.Calibration]::new(100,100)) $settings).IsValid) 'Nullspanne abgelehnt'
Check (-not (Process ([double]::NaN) $cal $settings).IsValid) 'NaN-Rohwert abgelehnt'
Check (-not (Process ([double]::PositiveInfinity) $cal $settings).IsValid) 'Unendlicher Rohwert abgelehnt'
Check (-not (Process 300 $cal $settings $null -0.1).IsValid) 'Negative Zeitdifferenz abgelehnt'
$badCal = [Tk75.Mapping.Calibration]::new(0,100); $badCal.UsableMin = 0
Check (-not (Process 50 $badCal $settings).IsValid) 'Einseitig angegebene Rohwertgrenze abgelehnt'
$badCal.UsableMax = 50
Check (-not (Process 25 $badCal $settings).IsValid) 'Kalibrierpunkt ausserhalb Rohwertgrenze abgelehnt'

$unit = [Tk75.Mapping.Calibration]::new(0,1)
$settings.TopDeadzone = 0.1; $settings.BottomDeadzone = 0.2
Near 0 (Process 0.1 $unit $settings).Final 'Oberer Deadzone-Rand bleibt neutral'
Near 0.5 (Process 0.45 $unit $settings).AfterDeadzone 'Nutzbarer Hub wird linear umgerechnet'
Near 1 (Process 0.8 $unit $settings).Final 'Untere Deadzone erreicht volle Ausgabe'
$settings.TopDeadzone = 0.8; $settings.BottomDeadzone = 0.2
Check (-not (Process 0.9 $unit $settings).IsValid) 'Kollabierte Deadzonen abgelehnt'

foreach ($curve in [Enum]::GetValues([Tk75.Mapping.CurveKind])) {
    $s = [Tk75.Mapping.SignalSettings]::new(); $s.Curve = $curve; $s.Exponent = 3
    $s.CustomPoints.Clear()
    $s.CustomPoints.Add([Tk75.Mapping.CurvePoint]::new(0,0))
    $s.CustomPoints.Add([Tk75.Mapping.CurvePoint]::new(0.25,0.1))
    $s.CustomPoints.Add([Tk75.Mapping.CurvePoint]::new(0.75,0.9))
    $s.CustomPoints.Add([Tk75.Mapping.CurvePoint]::new(1,1))
    Near 0 (Process 0 $unit $s).Final ($curve.ToString() + ' Ruhe-Endpunkt')
    Near 1 (Process 1 $unit $s).Final ($curve.ToString() + ' Druck-Endpunkt')
    $previous = 0.0
    for ($i = 0; $i -le 200; $i++) {
        $value = (Process ($i/200.0) $unit $s).Final
        Check ($value -ge 0 -and $value -le 1 -and $value -ge $previous) ($curve.ToString() + ' begrenzt und monoton')
        $previous = $value
    }
}
$s = [Tk75.Mapping.SignalSettings]::new(); $s.Curve = 'Exponential'; $s.Exponent = 2
Near 0.25 (Process 0.5 $unit $s).AfterCurve 'Quadratische Kennlinie'
$s.Curve = 'Logarithmic'; $s.Exponent = 3
Near 0.660964047443681 (Process 0.5 $unit $s).AfterCurve 'Logarithmische Kennlinie'
$s.Exponent = 0.000000000001
Near 0.5 (Process 0.5 $unit $s).AfterCurve 'Stabiler logarithmischer Grenzfall'
$s.Curve = 'Smoothstep'
Near 0.15625 (Process 0.25 $unit $s).AfterCurve 'Smoothstep Viertelhub'
$s.Curve = 'Custom'; $s.CustomPoints.Insert(1,[Tk75.Mapping.CurvePoint]::new(0.5,0.2))
Near 0.1 (Process 0.25 $unit $s).AfterCurve 'Stueckweise lineare eigene Kurve'
$s.CustomPoints[1].X = 0
Check (-not (Process 0.2 $unit $s).IsValid) 'Doppelte X-Position abgelehnt'
$s.CustomPoints[1].X = 0.5; $s.CustomPoints[1].Y = -0.1
Check (-not (Process 0.2 $unit $s).IsValid) 'Fallende negative Kurve abgelehnt'
$s = [Tk75.Mapping.SignalSettings]::new(); $s.MinOutput = 0.2; $s.MaxOutput = 0.8
Near 0 (Process 0 $unit $s).Final 'Minimum erzeugt keine Ruheeingabe'
Near 0.5 (Process 0.5 $unit $s).Final 'Ausgabegrenzen nach Kurve'
$s.Scale = 2
Near 0.8 (Process 0.5 $unit $s).Final 'Skalierung respektiert harte Ausgabeobergrenze'
$s.OutputDeadzone = 0.2
$combined = Process 0.25 $unit $s
Near 0.425 $combined.Final 'Skalierung und Ausgabe-Deadzone liegen vor Ausgabegrenzen'
Near 0.25 $combined.AfterCurve 'Nach-Kurve-Wert bleibt ohne Skalierung und Ausgabegrenzen'
Near 0 (Process 0.1 $unit $s).Final 'Ausgabe-Deadzone wirkt trotz positivem Minimum neutral'
Check ((Process 0.100001 $unit $s).Final -ge 0.2) 'Aktive Ausgabe beginnt am Mindestziel'
$s.Scale = 100
Near 0.8 (Process 0.5 $unit $s).Final 'Skalierung hundert kann MaxOutput nicht ueberschreiten'
Near 0.8 (Process 1 $unit $s).Final 'Voller Hub erhaelt MaxOutput bei Skalierung hundert'
$s.SmoothingTimeConstant = 0.1
$limitedState = [Tk75.Mapping.SignalState]::new(); $limitedState.IsPressed = $true; $limitedState.SmoothedValue = 1
Near 0.8 (Process 1 $unit $s $limitedState 0.001).Final 'Harte Grenze gilt auch fuer zuvor hoeheren Filterzustand'
$limitedState.Reset()
$ramp = Process 1 $unit $s $limitedState 0.001
Near (0.8 * (1 - [Math]::Exp(-0.01))) $ramp.Final 'Glattung folgt begrenztem Ausgabeziel'
Check ($ramp.Final -gt 0 -and $ramp.Final -lt 0.2) 'Filteranlauf darf unter Mindestziel liegen'
Near 0 (Process 0 $unit $s $limitedState 0).Final 'Loslassen bei kombinierten Einstellungen bleibt sofort neutral'
$s.SmoothingTimeConstant = 0
foreach ($scale in @(0, 0.1, 1, 2, 100)) {
    $s.Scale = $scale
    $previous = 0.0
    for ($i = 0; $i -le 40; $i++) {
        $value = (Process ($i / 40.0) $unit $s).Final
        Check (($value -eq 0 -or $value -ge 0.2) -and $value -le 0.8 -and $value -ge $previous) ('Min/Max bleiben bei Scale=' + $scale + ' begrenzt und monoton')
        $previous = $value
    }
}
$s.Scale = 0
Near 0 (Process 1 $unit $s).Final 'Skalierung null ist neutral'
$s.Scale = [double]::NaN
Check (-not (Process 0.5 $unit $s).IsValid) 'NaN-Einstellung abgelehnt'
$s = [Tk75.Mapping.SignalSettings]::new(); $s.MinOutput = 0.2; $s.MaxOutput = 0.8; $s.Curve = 'Custom'
$s.CustomPoints.Insert(1, [Tk75.Mapping.CurvePoint]::new(0.5, 0))
Near 0 (Process 0.25 $unit $s).Final 'Nullplateau der Kurve bleibt trotz positivem Minimum neutral'
Near 0.5 (Process 0.75 $unit $s).Final 'Positiver eigener Kurvenwert erhaelt aktiven Zielbereich'
$s = [Tk75.Mapping.SignalSettings]::new(); $s.OutputDeadzone = 0.2
Near 0 (Process 0.2 $unit $s).Final 'Ausgabe-Deadzone bis einschliesslich Rand neutral'
Near 0.0625 (Process 0.25 $unit $s).Final 'Ausgabe-Deadzone streckt Restbereich erneut'
Near 1 (Process 1 $unit $s).Final 'Ausgabe-Deadzone bewahrt Endpunkt'
$s.Scale = 2
Near 0.75 (Process 0.4 $unit $s).Final 'Ausgabe-Deadzone wirkt nach Skalierung'
$s.OutputDeadzone = 1
Near 0 (Process 1 $unit $s).Final 'Ausgabe-Deadzone eins deaktiviert ohne Division durch null'
$s.OutputDeadzone = 1.1
Check (-not (Process 0.5 $unit $s).IsValid) 'Ausgabe-Deadzone ausserhalb Bereich abgelehnt'
$s.OutputDeadzone = 0.2; $s.Scale = 1; $s.SmoothingTimeConstant = 0.1
$outputState = [Tk75.Mapping.SignalState]::new()
Check ((Process 1 $unit $s $outputState 0.1).Final -gt 0) 'Ausgabe vor Deadzone-Test aktiv'
Near 0 (Process 0.1 $unit $s $outputState 0.001).Final 'Ausgabe-Deadzone entfernt Glaettungsnachlauf sofort'

$s = [Tk75.Mapping.SignalSettings]::new(); $s.TopDeadzone = 0.1; $s.Hysteresis = 0.05
$state = [Tk75.Mapping.SignalState]::new()
Near 0 (Process 0.14 $unit $s $state).Final 'Hysterese vor Aktivierung'
Check ((Process 0.16 $unit $s $state).Final -gt 0) 'Hysterese aktiviert oberhalb Schwelle'
Check ((Process 0.12 $unit $s $state).Final -gt 0) 'Hysterese behaelt Aktivierung unter Einschaltschwelle'
Near 0 (Process 0.1 $unit $s $state).Final 'Hysterese gibt am Ruherand frei'
Near 0 (Process 0.14 $unit $s $state).Final 'Nach Loslassen erneut Einschaltschwelle erforderlich'
$s = [Tk75.Mapping.SignalSettings]::new(); $s.SmoothingTimeConstant = 0.1; $s.MinOutput = 0.2
$state = [Tk75.Mapping.SignalState]::new()
Near 0.632120558828558 (Process 1 $unit $s $state 0.1).Final 'Zeitkonstante einer Sprungantwort'
Near 0 (Process 0 $unit $s $state 0).Final 'Loslassen umgeht Glaettung und Minimum sofort'
Check (-not $state.IsPressed) 'Loslassen entfernt Hysteresezustand'
Near 0 (Process 1 $unit $s $state 0).Final 'Ohne vergangene Zeit kein Glaettungsfortschritt'

$profile = [Tk75.Mapping.Profile]::new()
$profile.Name = 'Synthetisches freies Mapping'
$one = Binding 'one-stick' 42 'LeftYPositive'; $two = Binding 'one-trigger' 42 'RightTrigger'
$two.Processing.Curve = 'Exponential'; $two.Processing.Exponent = 2
$profile.Bindings.Add($one); $profile.Bindings.Add($two)
$raw = [System.Collections.Generic.Dictionary[int,double]]::new(); $raw[42] = 0.5
$calibrations = [System.Collections.Generic.Dictionary[int,Tk75.Mapping.Calibration]]::new(); $calibrations[42] = $unit
$states = [System.Collections.Generic.Dictionary[string,Tk75.Mapping.SignalState]]::new()
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0.5 $frame.LeftY 'Eine beliebige Taste steuert Stick'
Near 0.25 $frame.RightTrigger 'Dieselbe Taste steuert gleichzeitig Trigger mit eigener Kurve'
Check ($frame.BindingResults.Count -eq 2 -and $states.Count -eq 2) 'Bindungsweise Ergebnisse und Zustaende getrennt'
Check ($frame.Errors.Count -eq 0) 'Gueltige Komposition ohne Fehler'
$raw.Remove(42) | Out-Null
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0 $frame.LeftY 'Fehlende Eingabe wird neutral'
Near 0 $frame.RightTrigger 'Fehlende Eingabe setzt alle Bindings dieser Taste zurueck'
Check ($frame.Errors.Count -eq 2) 'Fehlende Eingaben melden Fehler pro Binding'
$raw[42] = 1; $calibrations.Clear()
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0 $frame.LeftY 'Unbekannte Kalibrierung erzeugt keine Ausgabe'
Check ($frame.Errors.Count -eq 2) 'Unbekannte Kalibrierung meldet Fehler'
$calibrations[42] = $unit
$profile.Bindings.Clear(); $profile.Bindings.Add((Binding 'x-positive' 42 'LeftXPositive'))
$raw[7] = 0.3; $calibrations[7] = $unit
$profile.Bindings.Add((Binding 'x-negative' 7 'LeftXNegative'))
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0 $frame.LeftX 'Gegenrichtungen standardmaessig neutral trotz verschiedener Staerke'
Check ($states.Count -eq 2 -and -not $states.ContainsKey('one-trigger')) 'Entfernte Bindings hinterlassen keinen Zustand'
$profile.OpposedPolicy = 'Subtract'
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0.7 $frame.LeftX 'Explizites Subtrahieren der Gegenrichtungen'
$profile.Bindings.Clear(); $profile.Bindings.Add((Binding 'x' 42 'LeftXPositive')); $profile.Bindings.Add((Binding 'y' 42 'LeftYPositive'))
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 1 ([Math]::Sqrt($frame.LeftX*$frame.LeftX+$frame.LeftY*$frame.LeftY)) 'Kreisnormalisierung begrenzt Diagonalmagnitude'
Near ([Math]::Sqrt(0.5)) $frame.LeftX 'Diagonalkomponenten bleiben symmetrisch'
$profile.StickShape = 'Square'
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 1 $frame.LeftX 'Quadrat erlaubt volle X-Komponente'
Near 1 $frame.LeftY 'Quadrat erlaubt volle Y-Komponente'
$profile.Bindings.Clear(); $profile.Bindings.Add((Binding 'trigger1' 42 'LeftTrigger')); $profile.Bindings.Add((Binding 'trigger2' 7 'LeftTrigger'))
$raw[42] = 0.6
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0.6 $frame.LeftTrigger 'Mehrere Quellen standardmaessig Maximum'
$profile.Aggregation = 'ClampedSum'
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 0.9 $frame.LeftTrigger 'Explizite Summe mehrerer Quellen'
$raw[42] = 0.9
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Near 1 $frame.LeftTrigger 'Summierte Quellen bleiben auf eins begrenzt'
$profile.Bindings.Clear(); $button = Binding 'button' 42 'A'; $button.Processing.ButtonThreshold = 0.6; $profile.Bindings.Add($button)
$raw[42] = 0.5
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Check ($frame.Buttons -eq 0) 'Button unter eigener Schwelle frei'
$raw[42] = 0.6
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Check ($frame.Buttons -eq 0x1000) 'A mit standardmaessigem XInput-Bit'
$button.Enabled = $false
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Check ($frame.Buttons -eq 0 -and -not $states['button'].IsPressed) 'Deaktiviertes Binding neutral und zurueckgesetzt'
$profile.Bindings.Clear(); $profile.Bindings.Add((Binding 'up' 42 'DpadUp')); $profile.Bindings.Add((Binding 'down' 42 'DpadDown'))
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$profile,$states,0.01)
Check ($frame.Buttons -eq 0) 'Gegensaetzliche Dpad-Bits neutralisiert'
$expectedMasks = [ordered]@{ A=0x1000; B=0x2000; X=0x4000; Y=0x8000; LB=0x0100; RB=0x0200; Back=0x0020; Start=0x0010; LeftThumb=0x0040; RightThumb=0x0080; DpadUp=1; DpadDown=2; DpadLeft=4; DpadRight=8 }
foreach ($entry in $expectedMasks.GetEnumerator()) {
    $buttonProfile = [Tk75.Mapping.Profile]::new(); $buttonProfile.Bindings.Add((Binding 'mask' 42 $entry.Key))
    $frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$buttonProfile,$states,0.01)
    Check ($frame.Buttons -eq $entry.Value) ('Unabhaengig geprueftes Controllerbit fuer ' + $entry.Key)
}
$axisProfile = [Tk75.Mapping.Profile]::new(); $axisProfile.Bindings.Add((Binding 'right-negative' 42 'RightXNegative'))
$axisProfile.Bindings.Add((Binding 'right-positive' 7 'RightYPositive'))
$frame = [Tk75.Mapping.MappingEngine]::Compose($raw,$calibrations,$axisProfile,$states,0.01)
Near -0.6 $frame.RightX 'Negative Richtung am rechten Stick'
Near 0.3 $frame.RightY 'Unabhaengige positive Y-Richtung rechts'

$json = [Tk75.Mapping.ProfileJson]::Serialize($profile)
$roundtrip = [Tk75.Mapping.ProfileJson]::Deserialize($json)
Check ($roundtrip.Bindings.Count -eq 2 -and $roundtrip.Bindings[0].BindingId -ceq 'up') 'JSON erhaelt Bindings und IDs'
Check ($roundtrip.Name -ceq $profile.Name) 'JSON erhaelt Profilnamen'
Check ($json -notmatch 'Calibration|Rest|Bottom"|MeasuredTravel|SmoothedValue') 'Profil enthaelt keine Hardwarekalibrierung oder Laufzeitzustaende'
$minimal = [Tk75.Mapping.ProfileJson]::Deserialize('{"Version":1,"Name":"Minimal","Bindings":[{"BindingId":"b","KeyIndex":1,"Target":8}]}')
Check ($minimal.Bindings[0].Enabled) 'Fehlendes optionales Enabled verwendet true'
Near 1 $minimal.Bindings[0].Processing.MaxOutput 'Fehlende optionale Verarbeitung erhaelt definierte Defaults'
Check ($minimal.Controller -eq [Tk75.Mapping.ControllerKind]::Xbox360) 'Altes Profil ohne Controllertyp bleibt Xbox 360'
Check ([Tk75.Mapping.Profile]::new().Controller -eq [Tk75.Mapping.ControllerKind]::Xbox360) 'Neues Profil hat expliziten Xbox-Standard'
$dualSense = [Tk75.Mapping.ProfileJson]::Clone($minimal); $dualSense.Controller = [Tk75.Mapping.ControllerKind]::DualSense
$dualJson = [Tk75.Mapping.ProfileJson]::Serialize($dualSense)
Check ($dualJson -match '"Controller":1') 'DualSense-Auswahl wird im Profil gespeichert'
Check ([Tk75.Mapping.ProfileJson]::Deserialize($dualJson).Controller -eq [Tk75.Mapping.ControllerKind]::DualSense) 'DualSense ueberlebt Laden und Speichern'
$dualClone = [Tk75.Mapping.ProfileJson]::Clone($dualSense)
Check ($dualClone.Controller -eq [Tk75.Mapping.ControllerKind]::DualSense) 'Clone behaelt DualSense'
$dualClone.Controller = [Tk75.Mapping.ControllerKind]::Xbox360; $dualClone.Bindings[0].Processing.Scale = .25
Check ($dualSense.Controller -eq [Tk75.Mapping.ControllerKind]::DualSense -and $dualSense.Bindings[0].Processing.Scale -eq 1) 'Kopie veraendert weder Auswahl noch Verarbeitung des Quellprofils'
$controllerHistory = [Tk75.Mapping.EditHistory]::new($minimal)
$controllerHistory.Commit($dualSense)
Check ($controllerHistory.Current.Controller -eq [Tk75.Mapping.ControllerKind]::DualSense) 'History uebernimmt DualSense'
Check ($controllerHistory.Undo() -and $controllerHistory.Current.Controller -eq [Tk75.Mapping.ControllerKind]::Xbox360) 'Undo stellt Xbox-Auswahl wieder her'
Check ($controllerHistory.Redo() -and $controllerHistory.Current.Controller -eq [Tk75.Mapping.ControllerKind]::DualSense) 'Redo stellt DualSense-Auswahl wieder her'
$badController = [Tk75.Mapping.ProfileJson]::Clone($minimal); $badController.Controller = [System.Enum]::ToObject([Tk75.Mapping.ControllerKind], 99)
Check ([Tk75.Mapping.MappingValidation]::ValidateProfile($badController).Count -gt 0) 'Ungueltiger Controllertyp ist Validierungsfehler'
Reject { [Tk75.Mapping.ProfileJson]::Serialize($badController) } 'Speichern deutet unbekannten Controllertyp nicht um'
Reject { $controllerHistory.Commit($badController) } 'History lehnt unbekannten Controllertyp atomar ab'
Check ($controllerHistory.Current.Controller -eq [Tk75.Mapping.ControllerKind]::DualSense) 'Abgelehnter Commit behaelt letzte gueltige Controller-Auswahl'
foreach ($badJson in @(
    '', '{broken', '[]', 'null', '{}',
    '{"Version":1,"Version":1,"Name":"bad","Bindings":[]}',
    '{"Version":"1","Name":"bad","Bindings":[]}',
    '{"Version":1.5,"Name":"bad","Bindings":[]}',
    '{"Version":2,"Name":"bad","Bindings":[]}',
    '{"Version":1,"Name":"bad","Bindings":[],"Calibration":{}}',
    '{"Version":1,"Name":"bad","Bindings":[],"Aggregation":99}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":99}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":-1}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":0.5}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":"1"}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":"DualSense"}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":null}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":true}',
    '{"Version":1,"Name":"bad","Bindings":[],"Controller":0,"Controller":1}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1,"Target":99}]}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1.5,"Target":8}]}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1,"Target":8.5}]}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1,"Target":8,"Processing":{"Scale":1e999}}]}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1,"Target":8,"Processing":{"Scale":"NaN"}}]}',
    '{"Version":1,"Name":"bad","Bindings":[{"BindingId":"x","KeyIndex":1,"Target":8,"Processing":null}]}',
    '{"Version":1,"Name":"bad","Bindings":[]} {}'
)) { Reject { [Tk75.Mapping.ProfileJson]::Deserialize($badJson) } ('Malformed Profil abgelehnt: ' + $badJson) }
$duplicate = [Tk75.Mapping.ProfileJson]::Clone($profile); $duplicate.Bindings[1].BindingId = $duplicate.Bindings[0].BindingId
Reject { [Tk75.Mapping.ProfileJson]::Serialize($duplicate) } 'Doppelte BindingId beim Speichern abgelehnt'

$preset = [Tk75.Mapping.SignalSettings]::new(); $preset.Curve = 'Exponential'; $preset.Exponent = 3; $preset.OutputDeadzone = 0.15
$edited = [Tk75.Mapping.ProfileEditing]::ApplySettings($profile,[string[]]@('up','down'),$preset)
Check ($edited.Bindings[0].Processing.Curve -eq 'Exponential' -and $edited.Bindings[1].Processing.Curve -eq 'Exponential') 'Bulk-Verarbeitung auf mehrere Bindings'
Near 0.15 $edited.Bindings[1].Processing.OutputDeadzone 'Ausgabe-Deadzone in Bulk-Preset enthalten'
Check ($profile.Bindings[0].Processing.Curve -eq 'Linear') 'Bulk laesst Ursprungsprofil unberuehrt'
Check ($edited.Bindings[0].Target -eq 'DpadUp' -and $edited.Bindings[1].Target -eq 'DpadDown') 'Preset ersetzt keine Ausgabeziele'
$preset.CustomPoints[0].Y = 0.5
Near 0 $edited.Bindings[0].Processing.CustomPoints[0].Y 'Presetpunkte werden tief kopiert'
Reject { [Tk75.Mapping.ProfileEditing]::ApplySettings($profile,[string[]]@('missing'),[Tk75.Mapping.SignalSettings]::new()) } 'Unbekannte Bulkauswahl wird atomar abgelehnt'
$history = [Tk75.Mapping.EditHistory]::new($profile,3)
$history.Commit($edited)
Check $history.Undo() 'Undo nach Bulk-Transaktion'
Check ($history.Current.Bindings[0].Processing.Curve -eq 'Linear') 'Undo stellt Original wieder her'
Near 0 $history.Current.Bindings[0].Processing.OutputDeadzone 'Undo stellt Ausgabe-Deadzone wieder her'
Check $history.Redo() 'Redo nach Undo'
Check ($history.Current.Bindings[0].Processing.Curve -eq 'Exponential') 'Redo stellt Bulk wieder her'
Near 0.15 $history.Current.Bindings[0].Processing.OutputDeadzone 'Redo stellt Ausgabe-Deadzone wieder her'
$history.Undo() | Out-Null
$branch = $history.Current; $branch.Name = 'Alternative'; $history.Commit($branch)
Check (-not $history.CanRedo) 'Neue Aenderung entfernt Redo-Zweig'
$external = $history.Current; $external.Name = 'Extern mutiert'
Check ($history.Current.Name -ceq 'Alternative') 'History gibt getrennte Kopien heraus'
$invalid = $history.Current; $invalid.Version = 999
Reject { $history.Commit($invalid) } 'Ungueltiger History-Commit abgelehnt'
Check ($history.Current.Name -ceq 'Alternative') 'Fehlgeschlagener Commit veraendert Historie nicht'
Write-Output ('PASS: {0} Assertions; reiner Mapping-Core, ausschliesslich synthetische Werte, kein Hardware- oder XInput-Zugriff.' -f $script:assertions)
