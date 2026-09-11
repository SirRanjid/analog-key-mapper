#requires -Version 5.1
<# Rein synthetische Presets. Kein Profilpfad, keine GUI und keine Hardware. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot; $core = Join-Path $workspace 'src\Core'
if ('Tk75.Mapping.SignalPresetJson' -as [type]) { throw 'Den Test in einer neuen PowerShell-Sitzung starten.' }
Add-Type -Path @((Join-Path $core 'MappingModels.cs'), (Join-Path $core 'MappingValidation.cs'),
    (Join-Path $core 'BezierCurve.cs'), (Join-Path $core 'MappingEngine.cs'), (Join-Path $core 'ProfileJson.cs'), (Join-Path $core 'SignalPreset.cs'))
$script:checks = 0
function Check([bool] $Ok, [string] $Message) { $script:checks++; if (-not $Ok) { throw ('FAIL: ' + $Message) } }
function Equal($Expected, $Actual, [string] $Message) { Check ($Expected -ceq $Actual) ($Message + '; actual=' + $Actual) }
function Reject([scriptblock] $Action, [string] $Message) {
    $caught = $false; try { & $Action | Out-Null } catch { $caught = $true }; Check $caught $Message
}
function Json($Preset) { return [Tk75.Mapping.SignalPresetJson]::Serialize($Preset) }

$preset = [Tk75.Mapping.SignalPreset]::new(); $preset.Name = 'Synthetisch: ä } [ " Kurve'
$s = $preset.Settings; $s.TopDeadzone = 0.04; $s.BottomDeadzone = 0.08; $s.Curve = 'Custom'; $s.Exponent = 2.5
$s.CustomPoints.Insert(1,[Tk75.Mapping.CurvePoint]::new(0.4,0.2))
$s.MinOutput = 0.1; $s.MaxOutput = 0.85; $s.Scale = 0.75; $s.OutputDeadzone = 0.03
$s.Hysteresis = 0.015; $s.SmoothingTimeConstant = 0.02; $s.ButtonThreshold = 0.55
$json = Json $preset
$document = $json | ConvertFrom-Json
$fieldNames = @($document.PSObject.Properties.Name)
Equal 3 $fieldNames.Count 'Externes Preset hat genau drei Top-Level-Felder'
foreach ($field in @('Version','Name','Settings')) { Check ($fieldNames -contains $field) ('Presetfeld vorhanden: ' + $field) }
Check ($json -notmatch 'Bindings|BindingId|KeyIndex|preset-validation|Calibration|MeasuredTravel|SmoothedValue') 'Export enthaelt keine internen Mapping-Platzhalter oder Hardwaredaten'
$restored = [Tk75.Mapping.SignalPresetJson]::Deserialize($json)
Equal $preset.Name $restored.Name 'Name mit Unicode, Anfuehrungszeichen und Klammern roundtripped'
Equal 1 $restored.Version 'Presetversion erhalten'
Equal $json (Json $restored) 'Saemtliche Einstellungen und Reihenfolge roundtripped'
foreach ($field in [Tk75.Mapping.SignalSettings].GetFields()) {
    $expected = ConvertTo-Json -InputObject $field.GetValue($preset.Settings) -Depth 8 -Compress
    $actual = ConvertTo-Json -InputObject $field.GetValue($restored.Settings) -Depth 8 -Compress
    Equal $expected $actual ('Settings-Feld roundtripped: ' + $field.Name)
}
$restored.Settings.CustomPoints[1].Y = 0.3
Equal 0.2 $preset.Settings.CustomPoints[1].Y 'Importdaten haben eigene Kurvenpunkte'
$clone = [Tk75.Mapping.SignalPresetJson]::Clone($preset); $clone.Name = 'Kopie'; $clone.Settings.CustomPoints[1].Y = 0.35
Equal $json (Json $preset) 'Preset-Clone laesst Quelle unveraendert'
$signal = [Tk75.Mapping.SignalPresetJson]::CloneSignal($preset.Settings); $signal.CustomPoints[1].Y = 0.4
Equal 0.2 $preset.Settings.CustomPoints[1].Y 'CloneSignal ist tief getrennt'
Equal 0.03 $signal.OutputDeadzone 'CloneSignal erhaelt Ausgabe-Deadzone'

$minimal = [Tk75.Mapping.SignalPresetJson]::Deserialize('  {"Version":1,"Name":"Minimal","Settings":{}}  ')
Equal 1 $minimal.Settings.Scale 'Optional fehlende Settings-Felder haben validierte Defaults'
Equal 1 $minimal.Settings.MaxOutput 'Standardausgabemaximum bleibt eins'
Equal 0 $minimal.Settings.SmoothingTimeConstant 'Kein impliziter Glaettungszustand'
Equal 2 $minimal.Settings.CustomPoints.Count 'Standardkurve ist unabhaengig initialisiert'
Reject { [Tk75.Mapping.SignalPresetJson]::Serialize($null) } 'Null-Preset beim Speichern abgelehnt'
foreach ($badJson in @(
    '', 'null', '[]', '{}', '{broken',
    '{"Name":"x","Settings":{}}',
    '{"Version":1,"Settings":{}}',
    '{"Version":1,"Name":"x"}',
    '{"Version":2,"Name":"x","Settings":{}}',
    '{"Version":"1","Name":"x","Settings":{}}',
    '{"Version":1.5,"Name":"x","Settings":{}}',
    '{"Version":1,"Name":1,"Settings":{}}',
    '{"Version":1,"Name":"","Settings":{}}',
    '{"Version":1,"Name":"x","Settings":null}',
    '{"Version":1,"Name":"x","Settings":[]}',
    '{"Version":1,"Name":"x","Settings":{},"Bindings":[]}',
    '{"Version":1,"Name":"x","Settings":{},"Mapping":{}}',
    '{"Version":1,"Name":"x","Settings":{},"Calibration":{}}',
    '{"Version":1,"Name":"x","Settings":{},"Unknown":1}',
    '{"Version":1,"Version":1,"Name":"x","Settings":{}}',
    '{"Version":1,"Name":"x","Name":"y","Settings":{}}',
    '{"Version":1,"Name":"x","Settings":{},"Settings":{}}',
    '{"Version":1,"Name":"x","Settings":{"Scale":1,"Scale":0.5}}',
    '{"Version":1,"Name":"x","Settings":{"Scale":"0.5"}}',
    '{"Version":1,"Name":"x","Settings":{"Scale":"NaN"}}',
    '{"Version":1,"Name":"x","Settings":{"Scale":1e999}}',
    '{"Version":1,"Name":"x","Settings":{"Scale":-1}}',
    '{"Version":1,"Name":"x","Settings":{"OutputDeadzone":1.1}}',
    '{"Version":1,"Name":"x","Settings":{"Curve":99}}',
    '{"Version":1,"Name":"x","Settings":{"Curve":"Linear"}}',
    '{"Version":1,"Name":"x","Settings":{"Enabled":false}}',
    '{"Version":1,"Name":"x","Settings":{"Rest":0}}',
    '{"Version":1,"Name":"x","Settings":{"CustomPoints":[{"X":0,"Y":0},{"X":1,"X":1,"Y":1}]}}',
    '{"Version":1,"Name":"x","Settings":{"CustomPoints":[{"X":0,"Y":0,"Unknown":1},{"X":1,"Y":1}]}}',
    '{"Version":1,"Name":"x","Settings":{"CustomPoints":[{"X":0,"Y":0},{"X":0,"Y":1}]}}',
    '{"Version":1,"Name":"x","Settings":{"TopDeadzone":0.8,"BottomDeadzone":0.2}}',
    '{"Version":1,"Name":"x","Settings":{}} {}',
    '{"Version":1,"Name":"x","Settings":{}} trailing',
    '{"__type":"SignalPreset","Version":1,"Name":"x","Settings":{}}',
    '{"Version":1,"Name":"x","Settings":{"__type":"SignalSettings"}}'
)) { Reject { [Tk75.Mapping.SignalPresetJson]::Deserialize($badJson) } ('Ungueltiger Presetimport abgelehnt: ' + $badJson) }
Reject { [Tk75.Mapping.SignalPresetJson]::Deserialize((' ' * 65537)) } 'Zeichengrenze wird vor Parsing geprueft'
$longName = '{"Version":1,"Name":"' + ('x' * 129) + '","Settings":{}}'
Reject { [Tk75.Mapping.SignalPresetJson]::Deserialize($longName) } 'Name ist auf 128 Zeichen begrenzt'
$nested = '{"Version":1,"Name":"x","Settings":{"CustomPoints":' + ('[' * 22) + (' ]' * 22) + '}}'
Reject { [Tk75.Mapping.SignalPresetJson]::Deserialize($nested) } 'Tiefe Eingabe wird begrenzt abgelehnt'
$invalid = [Tk75.Mapping.SignalPresetJson]::Clone($preset); $invalid.Version = 2
Reject { [Tk75.Mapping.SignalPresetJson]::Serialize($invalid) } 'Unbekannte Version beim Export abgelehnt'
$invalid = [Tk75.Mapping.SignalPresetJson]::Clone($preset); $invalid.Settings = $null
Reject { [Tk75.Mapping.SignalPresetJson]::Serialize($invalid) } 'Fehlende Settings beim Export abgelehnt'
$invalid = [Tk75.Mapping.SignalPresetJson]::Clone($preset); $invalid.Settings.Scale = [double]::NaN
Reject { [Tk75.Mapping.SignalPresetJson]::Serialize($invalid) } 'NaN beim Export abgelehnt'
$invalid = [Tk75.Mapping.SignalPresetJson]::Clone($preset); $invalid.Settings.CustomPoints[1].Y = -1
Reject { [Tk75.Mapping.SignalPresetJson]::Serialize($invalid) } 'Ungueltige Kurve beim Export abgelehnt'
Equal $json (Json $preset) 'Fehlgeschlagene Importe und Exporte veraendern Quelle nicht'

# Real integration with the existing pure bulk/history methods: applying a saved
# signal preset does not overwrite independently chosen mapping or Enabled state.
$profile = [Tk75.Mapping.Profile]::new(); $a = [Tk75.Mapping.Binding]::new(); $a.BindingId = 'a'; $a.KeyIndex = 42; $a.Target = 'RightTrigger'; $a.Enabled = $false
$b = [Tk75.Mapping.Binding]::new(); $b.BindingId = 'b'; $b.KeyIndex = 99; $b.Target = 'LeftYNegative'
$profile.Bindings.Add($a); $profile.Bindings.Add($b)
$before = [Tk75.Mapping.ProfileJson]::Serialize($profile)
$imported = [Tk75.Mapping.SignalPresetJson]::Deserialize($json)
$applied = [Tk75.Mapping.ProfileEditing]::ApplySettings($profile,[string[]]@('a','b'),$imported.Settings)
Equal 'RightTrigger' $applied.Bindings[0].Target.ToString() 'Preset-Anwendung erhaelt erstes Mapping'
Equal 'LeftYNegative' $applied.Bindings[1].Target.ToString() 'Preset-Anwendung erhaelt zweites Mapping'
Equal $false $applied.Bindings[0].Enabled 'Signal-Preset aktiviert kein inaktives Binding'
Equal 0.03 $applied.Bindings[1].Processing.OutputDeadzone 'Importiertes Preset wirkt auf gesamte Bulkauswahl'
$imported.Settings.CustomPoints[1].Y = 0.5
Equal 0.2 $applied.Bindings[0].Processing.CustomPoints[1].Y 'Angewandtes Preset bleibt vom Importobjekt getrennt'
$history = [Tk75.Mapping.EditHistory]::new($profile); $history.Commit($applied)
Check $history.Undo() 'Gesamter Presetimport ist ein Undo-Schritt'
Equal $before ([Tk75.Mapping.ProfileJson]::Serialize($history.Current)) 'Undo stellt unabhaengige Ziele und Settings exakt wieder her'
Check $history.Redo() 'Redo stellt Preset-Anwendung wieder her'
Equal 0.03 $history.Current.Bindings[0].Processing.OutputDeadzone 'Redo erhaelt importierte Ausgabe-Deadzone'
Write-Output ('PASS: {0} Assertions; striktes Signal-Preset-JSON ohne Mapping oder Hardwaredaten.' -f $script:checks)
