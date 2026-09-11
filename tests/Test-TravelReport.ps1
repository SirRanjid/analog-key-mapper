#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot

# Load only the pure parser. Never load a monitor executable or HID/native code.
Add-Type -Path (Join-Path $workspace 'src\Tk75TravelReport.cs')
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}
function Parse-Expected([byte[]] $Report, [string] $Context) {
    $sample = [Tk75.Diagnostics.TravelSample]::new()
    $errorText = $null
    $accepted = [Tk75.Diagnostics.Tk75TravelReport]::TryParse($Report, [ref]$sample, [ref]$errorText)
    Assert-Equal $true $accepted $Context
    Assert-Equal $null $errorText ($Context + ': no error on valid input')
    return $sample
}
function Assert-Rejected([byte[]] $Report, [string] $Context) {
    # Seed outputs to ensure failure does not expose a previous valid sample.
    $sample = [Tk75.Diagnostics.TravelSample]::new()
    $sample.KeyIndex = 14; $sample.RawValue = 123; $sample.KeyLabel = 'stale'
    $errorText = $null
    Assert-Equal $false ([Tk75.Diagnostics.Tk75TravelReport]::TryParse($Report, [ref]$sample, [ref]$errorText)) $Context
    Assert-Equal $false ([string]::IsNullOrWhiteSpace($errorText)) ($Context + ': reason available')
    Assert-Equal $null $sample.KeyLabel ($Context + ': stale sample cleared')
}

# REAL hardware captures from 10 September 2026, replayed offline from sanitized,
# versioned fixtures. Original hex strings, arrival times and report order are retained.
# No ignored captures/ directory or device identity is needed to reproduce these tests.
# See fixtures/capture-notes.md for provenance and limits of self-paced observations.
function Replay-HardwareFixture([string] $Name, [string] $ExpectedHash) {
    $capturePath = Join-Path $PSScriptRoot ('fixtures\' + $Name)
    $captureHashBefore = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash
    Assert-Equal $ExpectedHash $captureHashBefore ('Recorded fixture integrity: ' + $Name)
    $count = 0
    $stats = @{}
    $labels = @{ 9 = 'A'; 14 = 'W'; 15 = 'S'; 21 = 'D' }
    foreach ($line in [IO.File]::ReadLines($capturePath)) {
        $record = $line | ConvertFrom-Json
        Assert-Equal 'elapsedMs,hex,reportId,reportLength,schemaVersion' (($record.PSObject.Properties.Name | Sort-Object) -join ',') 'Fixture contains only approved non-identifying fields'
        Assert-Equal 1 $record.schemaVersion 'Fixture schema version'
        Assert-Equal 5 $record.reportId 'Fixture report ID'
        Assert-Equal 32 $record.reportLength 'Fixture report length'
        $parts = $record.hex.Split('-')
        [byte[]] $bytes = $parts | ForEach-Object { [Convert]::ToByte($_, 16) }
        $count++
        $sample = Parse-Expected $bytes ('Hardware replay record ' + $count)
        # Independent explicit-hex oracle, supplemented by fixed observed totals below.
        $expectedIndex = [Convert]::ToInt32($parts[4], 16)
        $expectedRaw = [Convert]::ToInt32(($parts[3] + $parts[2]), 16)
        Assert-Equal $expectedIndex $sample.KeyIndex 'Recorded index retained'
        Assert-Equal $expectedRaw $sample.RawValue 'Recorded unsigned little-endian raw value retained'
        $expectedLabel = if ($labels.ContainsKey($expectedIndex)) { $labels[$expectedIndex] } else { 'Index ' + $expectedIndex }
        Assert-Equal $expectedLabel $sample.KeyLabel 'Known WASD and unknown index labels retained'
        if (-not $stats.ContainsKey($sample.KeyIndex)) {
            $stats[$sample.KeyIndex] = @{ Count = 0; Min = 65535; Max = 0; Last = $null; Zeros = 0 }
        }
        $entry = $stats[$sample.KeyIndex]
        $entry.Count++
        $entry.Min = [Math]::Min($entry.Min, $sample.RawValue)
        $entry.Max = [Math]::Max($entry.Max, $sample.RawValue)
        $entry.Last = $sample.RawValue
        if ($sample.RawValue -eq 0) { $entry.Zeros++ }
    }
    Assert-Equal $captureHashBefore (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash 'Saved hardware fixture unchanged'
    return [pscustomobject]@{ Count = $count; Stats = $stats }
}
$firstCapture = Replay-HardwareFixture 'outside-sandbox-w.jsonl' '072F0B7CDDCD3FBE26E94EA2E8616E08D28B09960E196FFFA4667A7450FFF12D'
$stats = $firstCapture.Stats
Assert-Equal 1341 $firstCapture.Count 'Complete first real capture decoded'
Assert-Equal 4 $stats.Count 'All four recorded indices preserved'
Assert-Equal 766 $stats[14].Count 'Observed W report count'
Assert-Equal 0 $stats[14].Min 'Observed W minimum (not automatic calibration)'
Assert-Equal 343 $stats[14].Max 'Observed W maximum (not an imposed clamp)'
Assert-Equal 13 $stats[14].Zeros 'Observed W zero reports retained'
Assert-Equal 0 $stats[14].Last 'Observed final W release retained'
Assert-Equal 193 $stats[41].Count 'Other index 41 report count'
Assert-Equal 351 $stats[41].Max 'Other index 41 retains independent range'
Assert-Equal 324 $stats[58].Count 'Unknown index 58 report count'
Assert-Equal 365 $stats[58].Max 'Unknown index 58 retains independent range'
Assert-Equal 58 $stats[77].Count 'Other index 77 report count'
Assert-Equal 368 $stats[77].Max 'Other index 77 retains independent range'

$secondCapture = Replay-HardwareFixture 'live-wasd-verification.jsonl' '7E062314F9002B2B6E4321F554A592BB949D4A12666ED77D4E43A27BDE264F10'
$stats = $secondCapture.Stats
Assert-Equal 3557 $secondCapture.Count 'Complete second real capture decoded with no rejected frames'
Assert-Equal 11 $stats.Count 'All second-capture key indices preserved'
foreach ($observed in @(
    @{ Index = 14; Label = 'W'; Count = 733; Max = 369; Zeros = 14 },
    @{ Index = 9; Label = 'A'; Count = 789; Max = 385; Zeros = 16 },
    @{ Index = 15; Label = 'S'; Count = 657; Max = 385; Zeros = 13 },
    @{ Index = 21; Label = 'D'; Count = 970; Max = 385; Zeros = 19 }
)) {
    $entry = $stats[$observed.Index]
    Assert-Equal $observed.Count $entry.Count ('Second capture report count: ' + $observed.Label)
    Assert-Equal 0 $entry.Min ('Second capture observed minimum: ' + $observed.Label)
    Assert-Equal $observed.Max $entry.Max ('Second capture observed maximum, not calibration: ' + $observed.Label)
    Assert-Equal $observed.Zeros $entry.Zeros ('Second capture observed zero count: ' + $observed.Label)
    Assert-Equal 0 $entry.Last ('Second capture final release: ' + $observed.Label)
}
foreach ($entry in $stats.Values) { Assert-Equal 0 $entry.Last 'Every second-capture index ends at zero' }
$totalReplayed = $firstCapture.Count + $secondCapture.Count

# SYNTHETIC boundary fixtures below are separate from the real WASD replay above.
# Extreme raw values test protocol decoding; they do not imply physical sensor ranges.
foreach ($case in @(
    @{ Bytes = [byte[]]@(5, 27, 0x34, 0x12, 9); Index = 9; Raw = 4660; Label = 'A' },
    @{ Bytes = [byte[]]@(5, 27, 0x00, 0x01, 15); Index = 15; Raw = 256; Label = 'S' },
    @{ Bytes = [byte[]]@(5, 27, 0x01, 0x00, 21); Index = 21; Raw = 1; Label = 'D' },
    @{ Bytes = [byte[]]@(5, 27, 0x00, 0x00, 0); Index = 0; Raw = 0; Label = 'Index 0' },
    @{ Bytes = [byte[]]@(5, 27, 0xff, 0xff, 255); Index = 255; Raw = 65535; Label = 'Index 255' }
)) {
    [byte[]] $report = $case.Bytes + [byte[]]::new(27)
    $sample = Parse-Expected $report ('Synthetic fixture: ' + $case.Label)
    Assert-Equal $case.Index $sample.KeyIndex 'Synthetic index'
    Assert-Equal $case.Raw $sample.RawValue 'Full uint16 range; no inferred physical clamp'
    Assert-Equal $case.Label $sample.KeyLabel 'Manufacturer-derived or unknown label'
}

Assert-Rejected $null 'Null report'
foreach ($length in @(0, 1, 5, 31, 33, 65)) { Assert-Rejected ([byte[]]::new($length)) ('Wrong length: ' + $length) }
[byte[]] $valid = @(5, 27, 0x57, 0x01, 14) + [byte[]]::new(27)
$invalid = [byte[]] $valid.Clone(); $invalid[0] = 0
Assert-Rejected $invalid 'Wrong report ID'
$invalid = [byte[]] $valid.Clone(); $invalid[1] = 0x8f
Assert-Rejected $invalid 'Wrong opcode'
foreach ($offset in 5..31) {
    $invalid = [byte[]] $valid.Clone(); $invalid[$offset] = 1
    Assert-Rejected $invalid ('Unsupported changed layout at trailing offset ' + $offset)
}
$sample = Parse-Expected $valid 'Valid report still accepted after malformed input'
Assert-Equal 343 $sample.RawValue 'Golden W value after errors'

Write-Output ('PASS: {0} assertions; replayed {1} real saved reports from two sanitized fixtures plus clearly marked synthetic cases; no device access.' -f $script:assertions, $totalReplayed)
