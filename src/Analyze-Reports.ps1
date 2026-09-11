#requires -Version 5.1
<#
.SYNOPSIS
Analysiert aufgezeichnete HID-Reports offline, ohne Geraetezugriff.
.DESCRIPTION
Gruppiert exakt nach devicePath, reportId und reportLength. Die Ergebnisse
beschreiben ausschliesslich beobachtete Bytes und Ankunftsabstaende.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $InputPath,
    [string] $OutputPath,
    [switch] $IncludeUint16,
    [ValidateRange(1, 4096)] [int] $MaxGroups = 128,
    [ValidateRange(1, 4096)] [int] $MaxReportLength = 1024,
    [ValidateRange(1, 65536)] [int] $MaxUniqueReportsPerGroup = 1024,
    [ValidateRange(1, 65536)] [int] $MaxDistinctUint16PerOffset = 256,
    [ValidateRange(1024, 1048576)] [int] $MaxLineChars = 65536,
    [ValidateRange(0, 1000)] [int] $MaxErrorExamples = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ReadLine() allein koennte eine beliebig grosse Eingabezeile allokieren.
# Dieser kleine Leser begrenzt den Puffer und verwirft den Rest langer Zeilen.
if (-not ('Tk75Offline.BoundedLineReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
namespace Tk75Offline {
    public sealed class BoundedLineReader : IDisposable {
        private readonly StreamReader reader;
        private readonly int limit;
        public BoundedLineReader(string path, int limit) {
            reader = new StreamReader(path, Encoding.UTF8, true, 4096);
            this.limit = limit;
        }
        public bool TryReadLine(out string line, out bool exceeded) {
            var buffer = new StringBuilder(Math.Min(limit, 4096));
            exceeded = false;
            bool any = false;
            int c;
            while ((c = reader.Read()) != -1) {
                any = true;
                if (c == '\n') break;
                if (buffer.Length < limit) buffer.Append((char)c);
                else exceeded = true;
            }
            if (!any) { line = null; return false; }
            if (buffer.Length > 0 && buffer[buffer.Length - 1] == '\r')
                buffer.Length--;
            line = exceeded ? null : buffer.ToString();
            return true;
        }
        public void Dispose() { reader.Dispose(); }
    }
}
'@
}

function Test-Number($Value) {
    return ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal])
}

function Get-Field($Object, [string] $Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$summary = [ordered]@{
    lines = [long]0; blankLines = [long]0; ignoredEvents = [long]0
    acceptedReports = [long]0; rejectedLines = [long]0
    rejectionReasons = [ordered]@{}
}
$errorExamples = [System.Collections.Generic.List[object]]::new()
function Add-Rejection([string] $Code) {
    $summary.rejectedLines++
    if (-not $summary.rejectionReasons.Contains($Code)) {
        $summary.rejectionReasons[$Code] = [long]0
    }
    $summary.rejectionReasons[$Code]++
    if ($errorExamples.Count -lt $MaxErrorExamples) {
        $errorExamples.Add([pscustomobject]@{ line = $summary.lines; reason = $Code })
    }
}

function New-DistinctSummary($Values, [bool] $Truncated, [int] $Limit) {
    if ($Truncated) {
        return [pscustomobject][ordered]@{
            count = $null; lowerBound = $Values.Count + 1
            isExact = $false; storageLimit = $Limit
        }
    }
    return [pscustomobject][ordered]@{
        count = $Values.Count; lowerBound = $Values.Count
        isExact = $true; storageLimit = $Limit
    }
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).ProviderPath
if (-not [System.IO.File]::Exists($resolvedInput)) {
    throw 'InputPath muss eine vorhandene JSONL-Datei sein.'
}
$resolvedOutput = $null
if ($OutputPath) {
    $resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if ([string]::Equals($resolvedInput, $resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputPath darf nicht die Eingabedatei sein.'
    }
}

$groups = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$groupOrder = [System.Collections.Generic.List[object]]::new()
$reader = [Tk75Offline.BoundedLineReader]::new($resolvedInput, $MaxLineChars)
try {
    $line = $null
    $exceeded = $false
    while ($reader.TryReadLine([ref]$line, [ref]$exceeded)) {
        $summary.lines++
        if ($exceeded) { Add-Rejection 'lineTooLong'; continue }
        if ([string]::IsNullOrWhiteSpace($line)) { $summary.blankLines++; continue }
        try { $entry = ConvertFrom-Json -InputObject $line -ErrorAction Stop }
        catch { Add-Rejection 'malformedJson'; continue }
        # ConvertFrom-Json kann ein JSON-Array mit genau einem Element
        # automatisch auspacken. Die Wurzelform deshalb ebenfalls pruefen.
        if ($line.TrimStart()[0] -ne '{' -or $null -eq $entry -or $entry -isnot [pscustomobject]) {
            Add-Rejection 'recordNotObject'; continue
        }
        $type = Get-Field $entry 'type'
        if ($type -isnot [string] -or [string]::IsNullOrEmpty($type)) {
            Add-Rejection 'missingRecordType'; continue
        }
        if ($type -cne 'report') { $summary.ignoredEvents++; continue }

        $devicePath = Get-Field $entry 'devicePath'
        $reportId = Get-Field $entry 'reportId'
        $reportLength = Get-Field $entry 'reportLength'
        $hex = Get-Field $entry 'hex'
        $elapsed = Get-Field $entry 'elapsedMs'
        if ($devicePath -isnot [string] -or [string]::IsNullOrWhiteSpace($devicePath) -or
            $devicePath.IndexOf([char]0) -ge 0) {
            Add-Rejection 'invalidDevicePath'; continue
        }
        if (-not (Test-Number $reportId) -or [double]$reportId -lt 0 -or
            [double]$reportId -gt 255 -or [double]$reportId -ne [Math]::Truncate([double]$reportId)) {
            Add-Rejection 'invalidReportId'; continue
        }
        if (-not (Test-Number $reportLength) -or [double]$reportLength -lt 1 -or
            [double]$reportLength -gt $MaxReportLength -or
            [double]$reportLength -ne [Math]::Truncate([double]$reportLength)) {
            Add-Rejection 'invalidReportLength'; continue
        }
        if ($hex -isnot [string] -or $hex.Trim() -notmatch '^[0-9A-Fa-f]{2}(?:(?:-|[ \t]+)?[0-9A-Fa-f]{2})*$') {
            Add-Rejection 'invalidHex'; continue
        }
        $normalizedHex = ($hex.Trim() -replace '[- \t]', '').ToUpperInvariant()
        if ($normalizedHex.Length -ne ([int]$reportLength * 2)) {
            Add-Rejection 'reportLengthMismatch'; continue
        }
        if ([Convert]::ToByte($normalizedHex.Substring(0, 2), 16) -ne [int]$reportId) {
            Add-Rejection 'reportIdMismatch'; continue
        }
        # Ohne Zeitwert bleiben Bytebeobachtungen nutzbar. Ein vorhandener,
        # aber ungueltiger Wert ist ein fehlerhafter Report.
        if ($null -ne $elapsed -and (-not (Test-Number $elapsed) -or
            [double]::IsNaN([double]$elapsed) -or [double]::IsInfinity([double]$elapsed) -or
            [double]$elapsed -lt 0)) {
            Add-Rejection 'invalidElapsedMs'; continue
        }
        $reportId = [int]$reportId
        $reportLength = [int]$reportLength
        $key = $devicePath + [char]0 + $reportId + [char]0 + $reportLength
        if (-not $groups.ContainsKey($key)) {
            if ($groups.Count -ge $MaxGroups) { Add-Rejection 'groupLimitReached'; continue }
            $byteStats = [System.Collections.Generic.List[object]]::new()
            for ($offset = 0; $offset -lt $reportLength; $offset++) {
                $byteStats.Add([pscustomobject]@{
                    offset = $offset; min = 256; max = -1
                    values = [System.Collections.Generic.HashSet[byte]]::new()
                })
            }
            $wordStats = [System.Collections.Generic.List[object]]::new()
            if ($IncludeUint16) {
                for ($offset = 0; $offset -lt ($reportLength - 1); $offset++) {
                    $wordStats.Add([pscustomobject]@{
                        offset = $offset; min = 65536; max = -1; truncated = $false
                        values = [System.Collections.Generic.HashSet[int]]::new()
                    })
                }
            }
            $group = [pscustomobject]@{
                devicePath = $devicePath; reportId = $reportId; reportLength = $reportLength
                reportCount = [long]0; uniqueTruncated = $false
                unique = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                bytes = $byteStats; words = $wordStats
                firstElapsedMs = $null; lastElapsedMs = $null; previousElapsedMs = $null
                reportsWithTime = [long]0; reportsWithoutTime = [long]0
                intervalCount = [long]0; intervalMin = $null; intervalMax = $null
                intervalMean = $null; zeroIntervals = [long]0; backwardTimeSteps = [long]0
            }
            $groups.Add($key, $group)
            $groupOrder.Add($group)
        }
        $group = $groups[$key]
        $summary.acceptedReports++
        $group.reportCount++
        $bytes = New-Object byte[] $reportLength
        for ($offset = 0; $offset -lt $reportLength; $offset++) {
            $value = [Convert]::ToByte($normalizedHex.Substring($offset * 2, 2), 16)
            $bytes[$offset] = $value
            $stat = $group.bytes[$offset]
            if ($value -lt $stat.min) { $stat.min = [int]$value }
            if ($value -gt $stat.max) { $stat.max = [int]$value }
            [void]$stat.values.Add($value)
        }
        if (-not $group.unique.Contains($normalizedHex)) {
            if ($group.unique.Count -lt $MaxUniqueReportsPerGroup) { [void]$group.unique.Add($normalizedHex) }
            else { $group.uniqueTruncated = $true }
        }
        foreach ($stat in $group.words) {
            $value = [int]$bytes[$stat.offset] + (256 * [int]$bytes[$stat.offset + 1])
            if ($value -lt $stat.min) { $stat.min = $value }
            if ($value -gt $stat.max) { $stat.max = $value }
            if (-not $stat.values.Contains($value)) {
                if ($stat.values.Count -lt $MaxDistinctUint16PerOffset) { [void]$stat.values.Add($value) }
                else { $stat.truncated = $true }
            }
        }
        if ($null -eq $elapsed) {
            $group.reportsWithoutTime++
            $group.previousElapsedMs = $null
            continue
        }
        $elapsed = [double]$elapsed
        $group.reportsWithTime++
        if ($null -eq $group.firstElapsedMs) { $group.firstElapsedMs = $elapsed }
        if ($null -ne $group.previousElapsedMs) {
            $interval = $elapsed - $group.previousElapsedMs
            if ($interval -lt 0) { $group.backwardTimeSteps++ }
            else {
                $group.intervalCount++
                if ($interval -eq 0) { $group.zeroIntervals++ }
                if ($null -eq $group.intervalMin -or $interval -lt $group.intervalMin) {
                    $group.intervalMin = $interval
                }
                if ($null -eq $group.intervalMax -or $interval -gt $group.intervalMax) {
                    $group.intervalMax = $interval
                }
                if ($null -eq $group.intervalMean) { $group.intervalMean = $interval }
                else { $group.intervalMean += ($interval - $group.intervalMean) / $group.intervalCount }
            }
        }
        $group.lastElapsedMs = $elapsed
        $group.previousElapsedMs = $elapsed
    }
}
finally {
    $reader.Dispose()
}

$groupResults = [System.Collections.Generic.List[object]]::new()
foreach ($group in $groupOrder) {
    $changedBytes = [System.Collections.Generic.List[object]]::new()
    foreach ($stat in $group.bytes) {
        if ($stat.min -ne $stat.max) {
            $changedBytes.Add([pscustomobject][ordered]@{
                offset = $stat.offset; min = $stat.min; max = $stat.max; distinctCount = $stat.values.Count
            })
        }
    }
    $candidates = $null
    if ($IncludeUint16) {
        $candidateList = [System.Collections.Generic.List[object]]::new()
        foreach ($stat in $group.words) {
            if ($stat.min -ne $stat.max) {
                $candidateList.Add([pscustomobject][ordered]@{
                    offset = $stat.offset; byteOffsets = @($stat.offset, ($stat.offset + 1))
                    min = $stat.min; max = $stat.max
                    distinct = New-DistinctSummary $stat.values $stat.truncated $MaxDistinctUint16PerOffset
                })
            }
        }
        $candidates = $candidateList.ToArray()
    }
    $groupResults.Add([pscustomobject][ordered]@{
        devicePath = $group.devicePath; reportId = $group.reportId; reportLength = $group.reportLength
        reportCount = $group.reportCount
        uniqueReports = New-DistinctSummary $group.unique $group.uniqueTruncated $MaxUniqueReportsPerGroup
        timing = [pscustomobject][ordered]@{
            reportsWithTime = $group.reportsWithTime; reportsWithoutTime = $group.reportsWithoutTime
            firstElapsedMs = $group.firstElapsedMs; lastElapsedMs = $group.lastElapsedMs
            observedIntervalsMs = [pscustomobject][ordered]@{
                count = $group.intervalCount; min = $group.intervalMin
                max = $group.intervalMax; mean = $group.intervalMean
                zeroCount = $group.zeroIntervals; backwardTimeSteps = $group.backwardTimeSteps
            }
        }
        changedByteOffsets = $changedBytes.ToArray()
        uint16LECandidates = $candidates
    })
}
$result = [pscustomobject][ordered]@{
    schemaVersion = 1
    kind = 'offlineHidReportStatistics'
    inputPath = $resolvedInput
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    interpretation = 'Byte statistics only; observed arrival intervals are not USB polling intervals. No analog interpretation.'
    limits = [pscustomobject][ordered]@{
        maxGroups = $MaxGroups; maxReportLength = $MaxReportLength; maxLineChars = $MaxLineChars
        maxUniqueReportsPerGroup = $MaxUniqueReportsPerGroup
        maxDistinctUint16PerOffset = $MaxDistinctUint16PerOffset
        maxErrorExamples = $MaxErrorExamples
    }
    summary = [pscustomobject]$summary
    errorExamples = $errorExamples.ToArray()
    groups = $groupResults.ToArray()
}
$json = ConvertTo-Json -InputObject $result -Depth 12
if ($resolvedOutput) {
    [System.IO.File]::WriteAllText($resolvedOutput, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}
$json
