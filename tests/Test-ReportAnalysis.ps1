#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $workspace 'src\Analyze-Reports.ps1'
$testRoot = Join-Path $PSScriptRoot ('synthetic-offline-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testRoot)
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) {
        throw ('FEHLER: {0}; erwartet: {1}; erhalten: {2}' -f $Message, $Expected, $Actual)
    }
}
function Assert-True([bool] $Condition, [string] $Message) {
    $script:assertions++
    if (-not $Condition) { throw ('FEHLER: ' + $Message) }
}
function Write-Lines([string] $Name, [string[]] $Lines) {
    $path = Join-Path $testRoot $Name
    [System.IO.File]::WriteAllLines($path, $Lines, [System.Text.UTF8Encoding]::new($false))
    return $path
}
function Report([string] $Device, [int] $Id, [int] $Length, [string] $Hex, $Elapsed) {
    return (ConvertTo-Json -Compress -InputObject ([ordered]@{
        type = 'report'; utc = '2026-01-01T00:00:00.000Z'; synthetic = $true
        devicePath = $Device; reportId = $Id; reportLength = $Length; hex = $Hex; elapsedMs = $Elapsed
    }))
}
try {
    # Saemtliche Reports in dieser Datei sind erfundene Testdaten.
    $fixture = Write-Lines 'synthetic-grouping.jsonl' @(
        '{"type":"session","synthetic":true}'
        (Report 'synthetic-device-A' 1 3 '01-00-00' 0)
        (Report 'synthetic-device-B' 1 3 '010000' 50)
        (Report 'synthetic-device-A' 1 3 '01 01 00' 10)
        (Report 'synthetic-device-A' 2 3 '02-10-00' 11)
        (Report 'synthetic-device-A' 1 2 '01-FF' 12)
        (Report 'synthetic-device-A' 1 3 '01-01-01' 30)
        (Report 'synthetic-device-A' 1 3 '010101' 30)
        '{"type":"device","hex":"not a report"}'
    )
    $output = Join-Path $testRoot 'synthetic-analysis.json'
    $analysis = (& $analyzer -InputPath $fixture -OutputPath $output -IncludeUint16 | ConvertFrom-Json)
    Assert-Equal 4 $analysis.groups.Count 'Gruppierung nach Pfad, ID UND Laenge'
    Assert-Equal 7 $analysis.summary.acceptedReports 'Gueltige Reportzahl'
    Assert-Equal 2 $analysis.summary.ignoredEvents 'Metadaten ignoriert'
    $group = $analysis.groups[0]
    Assert-Equal 4 $group.reportCount 'Nur Reports derselben Gruppe'
    Assert-Equal 3 $group.uniqueReports.count 'Identische Bytes trotz anderer Hexformatierung'
    Assert-True $group.uniqueReports.isExact 'Unique-Zahl exakt unter Speichergrenze'
    Assert-Equal 3 $group.timing.observedIntervalsMs.count 'Benachbarte Gruppenreports'
    Assert-Equal 0 $group.timing.observedIntervalsMs.min 'Gleiche Ankunftszeit erlaubt'
    Assert-Equal 20 $group.timing.observedIntervalsMs.max 'Zeitabstand aus gleicher Gruppe'
    Assert-Equal 10 $group.timing.observedIntervalsMs.mean 'Arithmetisches Mittel der Abstaende'
    Assert-Equal 1 $group.timing.observedIntervalsMs.zeroCount 'Nullintervalle separat gezaehlt'
    Assert-Equal 2 $group.changedByteOffsets.Count 'Nur veraenderte Offsets'
    Assert-Equal 1 $group.changedByteOffsets[0].offset 'Offsets sind nullbasiert'
    Assert-Equal 0 $group.changedByteOffsets[0].min 'Byte-Minimum'
    Assert-Equal 1 $group.changedByteOffsets[0].max 'Byte-Maximum'
    Assert-Equal 2 $group.changedByteOffsets[0].distinctCount 'Byte-Distinct'
    $word = $group.uint16LECandidates[1]
    Assert-Equal 1 $word.offset 'Auch ueberlappende uint16-Paare'
    Assert-Equal 257 $word.max 'uint16 little endian'
    Assert-Equal 3 $word.distinct.count 'uint16-Distinct'
    Assert-True ($null -eq $analysis.groups[1].timing.observedIntervalsMs.mean) 'Ein Report ergibt kein Intervall'
    Assert-Equal 0 $analysis.groups[1].changedByteOffsets.Count 'Ein Report ergibt keine Byteaenderung'
    $saved = [System.IO.File]::ReadAllText($output) | ConvertFrom-Json
    Assert-Equal 7 $saved.summary.acceptedReports 'Gespeicherter JSON-Bericht ist lesbar'

    $fixture = Write-Lines 'synthetic-timing.jsonl' @(
        (Report 'synthetic-clock' 0 2 '00-01' 10)
        (Report 'synthetic-clock' 0 2 '00-02' $null)
        (Report 'synthetic-clock' 0 2 '00-03' 20)
        (Report 'synthetic-clock' 0 2 '00-04' 5)
        (Report 'synthetic-clock' 0 2 '00-05' 9)
    )
    $analysis = (& $analyzer $fixture | ConvertFrom-Json)
    Assert-Equal 1 $analysis.groups[0].timing.reportsWithoutTime 'Nullzeit wird separat gezaehlt'
    Assert-Equal 1 $analysis.groups[0].timing.observedIntervalsMs.count 'Fehlende Zeit unterbricht Zeitkette'
    Assert-Equal 4 $analysis.groups[0].timing.observedIntervalsMs.mean 'Nach Ruecksprung neue Zeitkette'
    Assert-Equal 1 $analysis.groups[0].timing.observedIntervalsMs.backwardTimeSteps 'Ruecksprung separat gezaehlt'
    Assert-True ($null -eq $analysis.groups[0].uint16LECandidates) 'uint16-Pruefung ist optional'

    $fixture = Write-Lines 'synthetic-invalid.jsonl' @(
        '{broken json'
        '[]'
        '[{"type":"report","devicePath":"synthetic","reportId":0,"reportLength":1,"hex":"00","elapsedMs":0}]'
        '{"hex":"00"}'
        '{"type":"report","devicePath":"synthetic","reportId":256,"reportLength":1,"hex":"00","elapsedMs":0}'
        '{"type":"report","devicePath":"synthetic","reportId":0.5,"reportLength":1,"hex":"00","elapsedMs":0}'
        '{"type":"report","devicePath":"synthetic","reportId":0,"reportLength":0,"hex":"","elapsedMs":0}'
        '{"type":"report","devicePath":"synthetic","reportId":0,"reportLength":1.5,"hex":"00","elapsedMs":0}'
        (Report 'synthetic' 0 2 '00' 0)
        (Report 'synthetic' 1 1 '00' 0)
        (Report 'synthetic' 0 1 'GG' 0)
        (Report 'synthetic' 0 1 '0' 0)
        (Report 'synthetic' 0 2 '00--01' 0)
        (Report 'synthetic' 0 1 '00' -1)
        (Report 'synthetic' 0 1 '00' '1')
        (Report '' 0 1 '00' 0)
        (Report 'synthetic' 0 1 '00' 0)
        ' '
    )
    $analysis = (& $analyzer $fixture -MaxErrorExamples 3 | ConvertFrom-Json)
    Assert-Equal 16 $analysis.summary.rejectedLines 'Malformed Reports werden verworfen'
    Assert-Equal 1 $analysis.summary.acceptedReports 'Gueltiger Report nach Fehlern verarbeitet'
    Assert-Equal 1 $analysis.summary.blankLines 'Leerzeilen'
    Assert-Equal 3 $analysis.errorExamples.Count 'Fehlerbeispiele bleiben begrenzt'
    Assert-Equal 2 $analysis.summary.rejectionReasons.recordNotObject 'Auch Ein-Element-Arrays bleiben ungueltig'
    Assert-Equal 2 $analysis.summary.rejectionReasons.invalidReportId 'ID-Bereich und Ganzzahligkeit'
    Assert-Equal 2 $analysis.summary.rejectionReasons.invalidReportLength 'Laengenbereich und Ganzzahligkeit'
    Assert-Equal 1 $analysis.summary.rejectionReasons.reportIdMismatch 'ID muss erstem Byte entsprechen'
    Assert-Equal 1 $analysis.summary.rejectionReasons.reportLengthMismatch 'Laenge muss Bytes entsprechen'
    Assert-Equal 2 $analysis.summary.rejectionReasons.invalidElapsedMs 'Zeit muss numerisch und nichtnegativ sein'

    $fixture = Write-Lines 'synthetic-bounds.jsonl' @(
        ('x' * 2000)
        (Report 'synthetic-A' 0 2 '00-00' 0)
        (Report 'synthetic-A' 0 2 '00-01' 1)
        (Report 'synthetic-A' 0 2 '00-02' 2)
        (Report 'synthetic-A' 0 2 '00-02' 3)
        (Report 'synthetic-B' 0 2 '00-01' 4)
        (Report 'synthetic-A' 0 3 '00-01-02' 5)
    )
    $analysis = (& $analyzer $fixture -IncludeUint16 -MaxGroups 1 -MaxReportLength 2 -MaxLineChars 1024 -MaxUniqueReportsPerGroup 1 -MaxDistinctUint16PerOffset 1 | ConvertFrom-Json)
    Assert-Equal 1 $analysis.summary.rejectionReasons.lineTooLong 'Lange Zeile wird begrenzt verworfen'
    Assert-Equal 1 $analysis.summary.rejectionReasons.groupLimitReached 'Gruppenspeicher begrenzt'
    Assert-Equal 1 $analysis.summary.rejectionReasons.invalidReportLength 'Reportlaenge begrenzt'
    Assert-Equal 4 $analysis.summary.acceptedReports 'Leser setzt nach ueberlanger Zeile fort'
    Assert-True (-not $analysis.groups[0].uniqueReports.isExact) 'Unique-Cap als ungenau markiert'
    Assert-True ($null -eq $analysis.groups[0].uniqueReports.count) 'Kein erfundener Gesamtwert bei Cap'
    Assert-Equal 2 $analysis.groups[0].uniqueReports.lowerBound 'Gesicherte untere Grenze'
    Assert-Equal 3 $analysis.groups[0].changedByteOffsets[0].distinctCount 'Bytewerte bleiben exakt'
    Assert-True (-not $analysis.groups[0].uint16LECandidates[0].distinct.isExact) 'uint16-Cap markiert'
    Assert-Equal 512 $analysis.groups[0].uint16LECandidates[0].max 'Min/Max auch nach Cap aktuell'

    $fixture = Write-Lines 'synthetic-empty.jsonl' @('', '{"type":"end","synthetic":true}')
    $analysis = (& $analyzer $fixture | ConvertFrom-Json)
    Assert-Equal 0 $analysis.groups.Count 'Keine Reports: leere Gruppenliste'
    Assert-Equal 0 $analysis.summary.acceptedReports 'Keine Reports: Reportzahl null'

    $fixture = Write-Lines 'synthetic-no-time.jsonl' @(
        '{"type":"report","synthetic":true,"devicePath":"synthetic","reportId":0,"reportLength":1,"hex":"00"}'
    )
    $analysis = (& $analyzer $fixture | ConvertFrom-Json)
    Assert-Equal 1 $analysis.summary.acceptedReports 'Fehlendes elapsedMs erlaubt Byteanalyse'
    Assert-True ($null -eq $analysis.groups[0].timing.firstElapsedMs) 'Keine Zeitreihe: null'
    Assert-True ($null -eq $analysis.groups[0].timing.observedIntervalsMs.min) 'Keine Intervalle: null'
    $before = [System.IO.File]::ReadAllText($fixture)
    $rejectedOverwrite = $false
    try { & $analyzer $fixture -OutputPath $fixture | Out-Null }
    catch { $rejectedOverwrite = $true }
    Assert-True $rejectedOverwrite 'Eingabedatei vor Ueberschreiben geschuetzt'
    Assert-Equal $before ([System.IO.File]::ReadAllText($fixture)) 'Eingabedatei unveraendert'
    Write-Output ('PASS: {0} Assertions; ausschliesslich synthetische Offline-Testdaten.' -f $script:assertions)
}
finally {
    # Ausschliesslich den oben erzeugten und auf den Workspace geprueften Ordner entfernen.
    $resolvedTest = [System.IO.Path]::GetFullPath($testRoot)
    $resolvedParent = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if ($resolvedTest.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedTest).StartsWith('synthetic-offline-')) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
