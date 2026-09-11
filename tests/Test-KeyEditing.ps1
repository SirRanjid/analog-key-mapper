#requires -Version 5.1
<# Synthetische Profilbearbeitung; keine GUI, Hardware, Dateien oder Controller. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$core = Join-Path $workspace 'src\Core'
if ('Tk75.Mapping.KeyEditing' -as [type]) { throw 'Den Test in einer neuen PowerShell-Sitzung starten.' }
Add-Type -Path @(
    (Join-Path $core 'MappingModels.cs'), (Join-Path $core 'MappingValidation.cs'),
    (Join-Path $core 'BezierCurve.cs'), (Join-Path $core 'MappingEngine.cs'), (Join-Path $core 'ProfileJson.cs'), (Join-Path $core 'KeyEditing.cs')
)
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
public sealed class SyntheticOneShotIds : IEnumerable<string>
{
    private bool used;
    private readonly string[] values;
    public SyntheticOneShotIds(string[] values) { this.values = values; }
    public IEnumerator<string> GetEnumerator() {
        if (used) throw new InvalidOperationException("Selection was enumerated twice.");
        used = true; return ((IEnumerable<string>)values).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
}
'@
$script:checks = 0
function Check([bool] $Ok, [string] $Message) { $script:checks++; if (-not $Ok) { throw ('FAIL: ' + $Message) } }
function Equal($Expected, $Actual, [string] $Message) { Check ($Expected -ceq $Actual) ($Message + '; actual=' + $Actual) }
function Reject([scriptblock] $Action, [string] $Message) {
    $threw = $false; try { & $Action | Out-Null } catch { $threw = $true }; Check $threw $Message
}
function MakeBinding([string] $Id, [int] $Key, [Tk75.Mapping.OutputTarget] $Target, [double] $Top, [double] $Maximum, [bool] $Enabled) {
    $b = [Tk75.Mapping.Binding]::new(); $b.BindingId = $Id; $b.KeyIndex = $Key; $b.Target = $Target; $b.Enabled = $Enabled
    $b.Processing.TopDeadzone = $Top; $b.Processing.MaxOutput = $Maximum; return $b
}
function At($Profile, [int] $Key) { return @($Profile.Bindings | Where-Object KeyIndex -eq $Key) }
function Json($Profile) { return [Tk75.Mapping.ProfileJson]::Serialize($Profile) }
function FieldText($Settings, [string] $Field) { return (ConvertTo-Json -InputObject ($Settings.GetType().GetField($Field).GetValue($Settings)) -Depth 8 -Compress) }
function SignalJson($Settings) {
    $p = [Tk75.Mapping.Profile]::new(); $b = MakeBinding 'signal' 0 'A' 0 1 $true; $b.Processing = $Settings; $p.Bindings.Add($b); return (Json $p)
}

$profile = [Tk75.Mapping.Profile]::new(); $profile.Name = 'Synthetische Copy/Paste-Pruefung'; $profile.StickShape = 'Square'; $profile.OpposedPolicy = 'Subtract'; $profile.Aggregation = 'ClampedSum'
$source1 = MakeBinding 's1' 5 'LeftXPositive' 0.08 0.9 $false
$source1.Processing.BottomDeadzone = 0.12; $source1.Processing.Curve = 'Custom'; $source1.Processing.Exponent = 3
$source1.Processing.CustomPoints.Insert(1,[Tk75.Mapping.CurvePoint]::new(0.5,0.3))
$source1.Processing.MinOutput = 0.1; $source1.Processing.Scale = 0.7; $source1.Processing.OutputDeadzone = 0.04
$source1.Processing.Hysteresis = 0.02; $source1.Processing.SmoothingTimeConstant = 0.2; $source1.Processing.ButtonThreshold = 0.6
$profile.Bindings.Add($source1)
$profile.Bindings.Add((MakeBinding 's2' 5 'RightTrigger' 0.02 0.6 $true))
$profile.Bindings.Add((MakeBinding 'd1' 20 'LeftYPositive' 0.1 0.7 $true))
$profile.Bindings.Add((MakeBinding 'd2' 20 'B' 0.2 0.8 $false))
$profile.Bindings.Add((MakeBinding 'e1' 21 'X' 0.03 0.5 $true))
$profile.Bindings.Add((MakeBinding 'unrelated' 23 'DpadUp' 0.04 0.4 $true))
$baseline = Json $profile
$copy = [Tk75.Mapping.KeyEditing]::CopyKey($profile,5)
Equal 2 $copy.Count 'Copy erhaelt alle Bindings einer physischen Taste'
Equal 's1' $copy[0].BindingId 'Copy erhaelt stabile Clipboard-Reihenfolge und Herkunfts-ID'
$copy[0].Processing.CustomPoints[1].Y = 0.4; $copy[0].Enabled = $true
Equal $baseline (Json $profile) 'Clipboard ist vom Quellprofil tief getrennt'
$copy = [Tk75.Mapping.KeyEditing]::CopyKey($profile,5)
Reject { [Tk75.Mapping.KeyEditing]::CopyKey($profile,22) } 'Leere Quelltaste wird abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::CopyKey($profile,256) } 'Unbekannter physischer Index wird abgelehnt'

$all = [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,22),$copy,'All')
Equal 8 $all.Bindings.Count 'Alles ersetzt bestehende und legt neue Zieltasten an'
Equal $baseline (Json $profile) 'Paste veraendert Eingabeprofil nicht'
Equal 'Square' $all.StickShape.ToString() 'Paste erhaelt Profilverhalten'
Equal $profile.Name $all.Name 'Paste erhaelt Profilname'
$ids = @($all.Bindings | ForEach-Object BindingId)
Equal $ids.Count @($ids | Select-Object -Unique).Count 'Alle Ergebnis-IDs eindeutig'
foreach ($key in @(20,22)) {
    $dest = @(At $all $key)
    Equal 2 $dest.Count 'Quelle wird auf jedes ausgewaehlte Ziel kopiert'
    for ($i=0; $i -lt 2; $i++) {
        Equal $copy[$i].Target $dest[$i].Target 'Alles kopiert beliebige Zielkombination'
        Equal $copy[$i].Enabled $dest[$i].Enabled 'Alles kopiert Enabled pro Binding'
        Equal (SignalJson $copy[$i].Processing) (SignalJson $dest[$i].Processing) 'Alles kopiert unabhaengige Signalparameter pro Binding'
        Check (-not (@($profile.Bindings | ForEach-Object BindingId) -contains $dest[$i].BindingId)) 'Ersetzte Bindings erhalten frische IDs'
    }
}
(At $all 20)[0].Processing.CustomPoints[1].Y = 0.45
Equal 0.3 (At $all 22)[0].Processing.CustomPoints[1].Y 'Zieltasten teilen keine Kurvenreferenzen'
Equal 0.3 $copy[0].Processing.CustomPoints[1].Y 'Paste teilt keine Kurvenreferenz mit Clipboard'

$mapped = [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,21),$copy,'Mapping')
$dest20 = @(At $mapped 20); $dest21 = @(At $mapped 21); $old20 = @(At $profile 20); $old21 = @(At $profile 21)
for ($i=0; $i -lt 2; $i++) {
    Equal $copy[$i].Target $dest20[$i].Target 'Mapping ersetzt Target-Liste'
    Equal $copy[$i].Enabled $dest20[$i].Enabled 'Mapping kopiert Enabled zusammen mit Ziel'
    Equal (SignalJson $old20[$i].Processing) (SignalJson $dest20[$i].Processing) 'Gleiche Anzahl erhaelt Zielverarbeitung positionsweise'
    Equal (SignalJson $old21[0].Processing) (SignalJson $dest21[$i].Processing) 'Andere Anzahl verwendet erste bestehende Zielverarbeitung'
    Check ($dest20[$i].BindingId -cne $old20[$i].BindingId) 'Mapping-Neuaufbau hat frische IDs'
}
$allExcept = [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,21),$copy,'AllExceptMapping')
foreach ($b in @($allExcept.Bindings | Where-Object { $_.KeyIndex -in @(20,21) })) {
    $prior = @($profile.Bindings | Where-Object BindingId -eq $b.BindingId)[0]
    Equal $prior.Target $b.Target 'Alles ausser Mapping behaelt Target und ID'
    Equal $false $b.Enabled 'Alles ausser Mapping kopiert Enabled vom ersten Quellbinding'
    Equal (SignalJson $copy[0].Processing) (SignalJson $b.Processing) 'Alles ausser Mapping kopiert erstes Quellsignal auf alle Zielbindungen'
}
Equal 6 $allExcept.Bindings.Count 'Signalpaste veraendert keine Mapping-Anzahl'

$groups = [ordered]@{
    Deadzones = @('TopDeadzone','BottomDeadzone','OutputDeadzone')
    Curve = @('Curve','Exponent','CustomPoints')
    Filters = @('Hysteresis','SmoothingTimeConstant')
    OutputRange = @('MinOutput','MaxOutput','Scale','OutputDeadzone','ButtonThreshold')
}
foreach ($group in $groups.GetEnumerator()) {
    $partial = [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,21),$copy,$group.Key)
    foreach ($b in @($partial.Bindings | Where-Object { $_.KeyIndex -in @(20,21) })) {
        $prior = @($profile.Bindings | Where-Object BindingId -eq $b.BindingId)[0]
        Equal $prior.Enabled $b.Enabled ($group.Key + ' laesst Enabled unveraendert')
        Equal $prior.Target $b.Target ($group.Key + ' laesst Targets unveraendert')
        foreach ($field in [Tk75.Mapping.SignalSettings].GetFields()) {
            $expected = if ($group.Value -contains $field.Name) { $copy[0].Processing } else { $prior.Processing }
            Equal (FieldText $expected $field.Name) (FieldText $b.Processing $field.Name) ($group.Key + ' kopiert ausschliesslich definierte Gruppe: ' + $field.Name)
        }
    }
    Equal (SignalJson (At $profile 23)[0].Processing) (SignalJson (At $partial 23)[0].Processing) 'Nicht gewaehlte Taste bleibt unveraendert'
}
foreach ($part in @('Deadzones','Curve','Filters','OutputRange','Mapping','AllExceptMapping')) {
    Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,22),$copy,$part) } ($part + ' lehnt Auswahl mit ungemappter Taste vollstaendig ab')
    Equal $baseline (Json $profile) 'Fehlgeschlagene Auswahl erzeugt keine Teilaenderung'
}
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(),$copy,'All') } 'Leere Zieltastenauswahl abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,20),$copy,'All') } 'Doppelte Zieltastenauswahl abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20,300),$copy,'All') } 'Ungueltiger Index verhindert gesamten Paste'
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20),$null,'All') } 'Leeres Clipboard abgelehnt'
$mixedSource = [System.Collections.Generic.List[Tk75.Mapping.Binding]]::new(); $mixedSource.Add($copy[0]); $mixedSource.Add((At $profile 20)[0])
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20),$mixedSource,'All') } 'Clipboard mit mehreren physischen Quellen abgelehnt'
$invalidClipboard = [Tk75.Mapping.KeyEditing]::CopyKey($profile,5); $invalidClipboard[0].Processing.Scale = [double]::NaN
Reject { [Tk75.Mapping.KeyEditing]::Paste($profile,[int[]]@(20),$invalidClipboard,'All') } 'Ungueltiges Clipboard wird vollstaendig abgelehnt'
$conflicting = [Tk75.Mapping.ProfileJson]::Clone($profile); (At $conflicting 20)[0].Processing.TopDeadzone = 0.99
Reject { [Tk75.Mapping.KeyEditing]::Paste($conflicting,[int[]]@(20,21),$copy,'Filters') } 'Teilgruppe wird gegen verbleibende Zielparameter validiert'

$edited = [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1','e1'),'Scale','0,25')
Equal 0.25 (At $edited 20)[0].Processing.Scale 'Dezimalkomma wird als Dezimalzahl verarbeitet'
Equal 0.25 (At $edited 21)[0].Processing.Scale 'Property-Bulk wirkt auf verschiedene physische Tasten'
Equal 1 (At $edited 20)[1].Processing.Scale 'Nicht ausgewaehltes Binding derselben Taste bleibt erhalten'
Equal $baseline (Json $profile) 'Property-Bulk veraendert Original nicht'
$edited = [Tk75.Mapping.KeyEditing]::ApplyProperty($edited,[string[]]@('d1','e1'),'Enabled',$false)
Check (-not (At $edited 20)[0].Enabled -and -not (At $edited 21)[0].Enabled) 'Enabled ist als Bulk-Eigenschaft bearbeitbar'
$edited = [Tk75.Mapping.KeyEditing]::ApplyProperty($edited,[string[]]@('d1','e1'),'Target','RightYNegative')
Equal 'RightYNegative' (At $edited 20)[0].Target.ToString() 'Target-Whitelist unterstuetzt Richtungswechsel'
$edited = [Tk75.Mapping.KeyEditing]::ApplyProperty($edited,[string[]]@('d1','e1'),'Curve','smoothstep')
Equal 'Smoothstep' (At $edited 21)[0].Processing.Curve.ToString() 'Enum-Namen werden ohne Gross-/Kleinschreibung angenommen'
$oneShot = [SyntheticOneShotIds]::new([string[]]@('d1','e1'))
$once = [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,$oneShot,'Scale',0.8)
Equal 0.8 (At $once 21)[0].Processing.Scale 'Auswahl wird nur einmal konsumiert'

$numericValues = [ordered]@{ TopDeadzone=0.05; BottomDeadzone=0.03; Exponent=2.5; MinOutput=0.1; MaxOutput=0.6; Scale=0.8; OutputDeadzone=0.02; Hysteresis=0.01; SmoothingTimeConstant=0.1; ButtonThreshold=0.4 }
foreach ($entry in $numericValues.GetEnumerator()) {
    $next = [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1','e1'),$entry.Key,$entry.Value)
    Equal $entry.Value ([Tk75.Mapping.KeyEditing]::MixedValue($next,[string[]]@('d1','e1'),$entry.Key)) ('Numerische Whitelist: ' + $entry.Key)
}
$points = [System.Collections.Generic.List[Tk75.Mapping.CurvePoint]]::new()
$points.Add([Tk75.Mapping.CurvePoint]::new(0,0)); $points.Add([Tk75.Mapping.CurvePoint]::new(0.5,0.2)); $points.Add([Tk75.Mapping.CurvePoint]::new(1,1))
$curved = [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1','e1'),'CustomPoints',$points)
Equal 'Linear' (At $curved 20)[0].Processing.Curve.ToString() 'CustomPoints veraendert den Kurventyp nicht implizit'
$points[1].Y = 0.4
Equal 0.2 (At $curved 20)[0].Processing.CustomPoints[1].Y 'Property-Kurvenpunkte vom Aufrufer getrennt'
(At $curved 20)[0].Processing.CustomPoints[1].Y = 0.3
Equal 0.2 (At $curved 21)[0].Processing.CustomPoints[1].Y 'Property-Kurvenpunkte je Binding getrennt'
Check ($null -eq [Tk75.Mapping.KeyEditing]::MixedValue($curved,[string[]]@('d1','e1'),'CustomPoints')) 'Unterschiedliche Kurvenpunkte ergeben Gemischt'
Check ($null -eq [Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@('d1','d2'),'TopDeadzone')) 'Unterschiedliche Skalarwerte ergeben Gemischt'
Equal $true ([Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@('d1','e1'),'Enabled')) 'Gleiche boolesche Werte bleiben typisiert'
$sharedPoints = [Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@('d1','e1'),'CustomPoints')
$sharedPoints[1].Y = 0.1
Equal 1 (At $profile 20)[0].Processing.CustomPoints[1].Y 'MixedValue gibt eine getrennte Kurvenkopie heraus'

foreach ($property in @('BindingId','KeyIndex','Rest','Bottom','Name','Unknown')) {
    Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),$property,1) } ('Nicht freigegebene Eigenschaft: ' + $property)
    Reject { [Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@('d1'),$property) } ('Nicht freigegebene Leseeigenschaft: ' + $property)
}
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1','missing'),'Scale',0.3) } 'Unbekannte BindingId verhindert Teilaenderung'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1','d1'),'Scale',0.3) } 'Doppelte BindingId abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@(),'Scale',0.3) } 'Leere Bulk-Auswahl abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@(),'Scale') } 'Leere Auswahl wird nicht als Gemischt kaschiert'
Reject { [Tk75.Mapping.KeyEditing]::MixedValue($profile,[string[]]@('d1','missing'),'Scale') } 'MixedValue ignoriert keine fehlende Auswahl'
foreach ($value in @([double]::NaN,[double]::PositiveInfinity,-0.2,$true,'1,2.3')) {
    Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),'Scale',$value) } 'Ungueltiger Zahlenwert abgelehnt'
}
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),'Curve','Unknown') } 'Unbekannter Kurvenwert abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),'Target',99) } 'Unbekanntes Ausgabeziel abgelehnt'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),'Target',[Tk75.Mapping.CurveKind]::Linear) } 'Falscher Enumtyp wird nicht numerisch umgedeutet'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('d1'),'Enabled',1) } 'Enabled akzeptiert keine implizite Zahlenkonversion'
Reject { [Tk75.Mapping.KeyEditing]::ApplyProperty($profile,[string[]]@('s1','e1'),'TopDeadzone',0.9) } 'Interdependente Deadzonen verhindern die gesamte Bulk-Aenderung'
Equal $baseline (Json $profile) 'Alle fehlgeschlagenen Eigenschaften lassen Quelle unveraendert'

$history = [Tk75.Mapping.EditHistory]::new($profile)
$firstPaste = [Tk75.Mapping.KeyEditing]::Paste($history.Current,[int[]]@(20,21),$copy,'All')
$history.Commit($firstPaste); $firstJson = Json $history.Current
$cycleCopy = [Tk75.Mapping.KeyEditing]::CopyKey($history.Current,20)
$secondPaste = [Tk75.Mapping.KeyEditing]::Paste($history.Current,[int[]]@(5),$cycleCopy,'All')
$history.Commit($secondPaste); $secondJson = Json $history.Current
Check ($firstJson -cne $secondJson) 'Copy/Paste-Zyklus erneuert nur Zielbindings mit neuen IDs'
Check $history.Undo() 'Gesamter Multi-Key-Paste ist ein Undo-Schritt'
Equal $firstJson (Json $history.Current) 'Undo stellt vorherigen Paste inklusive IDs exakt wieder her'
Check $history.Undo() 'Undo bis Originalprofil'
Equal $baseline (Json $history.Current) 'Originalprofil nach zwei Undo-Schritten exakt wiederhergestellt'
Check $history.Redo() 'Redo der Mehrfachauswahl'
Check $history.Redo() 'Redo des Copy/Paste-Zyklus'
Equal $secondJson (Json $history.Current) 'Redo erhaelt die bereits vergebenen IDs'
Write-Output ('PASS: {0} Assertions; atomare Key-Editing-API, synthetische Profile, keine GUI oder Hardware.' -f $script:checks)
