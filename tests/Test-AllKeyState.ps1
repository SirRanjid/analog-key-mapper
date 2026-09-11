#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
# These two files contain only parsing/state/rendering logic, no hardware access.
Add-Type -Path @((Join-Path $workspace 'src\Tk75TravelReport.cs'), (Join-Path $workspace 'src\RawLiveView.cs'))
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}
function Sample([int] $Index, [int] $Raw, [string] $Label) {
    $sample = [Tk75.Diagnostics.TravelSample]::new()
    $sample.KeyIndex = $Index; $sample.RawValue = $Raw; $sample.KeyLabel = $Label
    return $sample
}

# Replay all 3557 REAL reports: each update must preserve every other key's last
# independent value, including when one key releases while another remains pressed.
$view = [Tk75.Diagnostics.RawLiveView]::new()
$expected = @{}
$reportCount = 0
$nonzeroOtherAtRelease = 0
$lastTime = 0.0
foreach ($line in [IO.File]::ReadLines((Join-Path $PSScriptRoot 'fixtures\live-wasd-verification.jsonl'))) {
    $record = $line | ConvertFrom-Json
    $parts = $record.hex.Split('-')
    [byte[]] $bytes = $parts | ForEach-Object { [Convert]::ToByte($_, 16) }
    $sample = [Tk75.Diagnostics.TravelSample]::new()
    $errorText = $null
    Assert-Equal $true ([Tk75.Diagnostics.Tk75TravelReport]::TryParse($bytes, [ref]$sample, [ref]$errorText)) 'Real report parses'
    $keyIndex = [Convert]::ToInt32($parts[4], 16)
    $raw = [Convert]::ToInt32(($parts[3] + $parts[2]), 16)
    if ($raw -eq 0) {
        foreach ($other in $expected.Keys) {
            if ($other -ne $keyIndex -and $expected[$other] -gt 0) { $nonzeroOtherAtRelease++ }
        }
    }
    $expected[$keyIndex] = $raw
    $lastTime = [double]$record.elapsedMs
    $view.Publish($sample, $lastTime)
    $snapshot = $view.Snapshot($lastTime)
    Assert-Equal $expected.Count $snapshot.Length 'Every seen real index retained'
    foreach ($state in $snapshot) {
        Assert-Equal $expected[$state.KeyIndex] $state.RawValue 'Independent real key values survive another key update/release'
        Assert-Equal $true $state.Known 'Real observed state remains known without invalidation'
    }
    $reportCount++
}
Assert-Equal 3557 $reportCount 'All real reports replayed'
Assert-Equal 11 $view.Snapshot($lastTime).Length 'All 11 real indices retained, not only WASD'
foreach ($state in $view.Snapshot($lastTime)) { Assert-Equal 0 $state.RawValue 'Every real index finishes at its observed release value' }

# SYNTHETIC explicit overlap ensures independent release even if the real self-paced
# capture happened to contain no particular simultaneous-key sequence.
$overlap = [Tk75.Diagnostics.RawLiveView]::new()
$overlap.Publish((Sample 9 200 'A'), 0)
$overlap.Publish((Sample 14 300 'W'), 10)
$overlap.Publish((Sample 9 0 'A'), 20)
$snapshot = $overlap.Snapshot(20)
Assert-Equal 0 $snapshot[0].RawValue 'A release stored independently'
Assert-Equal 300 $snapshot[1].RawValue 'W remains physically reported pressed after A release'
Assert-Equal 10 $snapshot[1].AgeMs 'Unchanged W retains its own timestamp'

# SYNTHETIC full index domain: labels come from each sample, with no fixed layout.
$all = [Tk75.Diagnostics.RawLiveView]::new()
Assert-Equal 0 $all.Snapshot(0).Length 'Unseen keys omitted'
for ($index = 0; $index -lt 256; $index++) { $all.Publish((Sample $index (1000 + $index) ('Synthetic ' + $index)), $index) }
$snapshot = $all.Snapshot(255)
Assert-Equal 256 $snapshot.Length 'Entire byte-sized key-index domain retained'
for ($index = 0; $index -lt 256; $index++) {
    Assert-Equal $index $snapshot[$index].KeyIndex 'Snapshot indices sorted and complete'
    Assert-Equal ('Synthetic ' + $index) $snapshot[$index].KeyLabel 'Incoming labels preserved'
    Assert-Equal (1000 + $index) $snapshot[$index].RawValue 'All index values independent'
    Assert-Equal (255 - $index) $snapshot[$index].AgeMs 'Per-index age retained'
    Assert-Equal $true $snapshot[$index].Known 'Every published index known'
}
$all.Invalidate('Synthetic disconnect')
Assert-Equal 'Synthetic disconnect' $all.InvalidationReason 'Invalidation reason retained'
foreach ($state in $all.Snapshot(1000)) {
    Assert-Equal $false $state.Known 'Disconnect makes each old value unknown'
    Assert-Equal (1000 + $state.KeyIndex) $state.RawValue 'Old raw retained solely for diagnostics'
}
$all.Publish((Sample 14 77 'W renewed'), 1000)
foreach ($state in $all.Snapshot(1001)) {
    Assert-Equal ($state.KeyIndex -eq 14) $state.Known 'Reconnect revives only the individually republished key'
}
$snapshot = $all.Snapshot(1001)
$snapshot[0] = [Tk75.Diagnostics.KeyStateSnapshot]::new()
Assert-Equal 1000 $all.Snapshot(1001)[0].RawValue 'Returned snapshot cannot mutate stored state'

# Staleness is an age label, never a fabricated release or automatic invalidation.
$age = [Tk75.Diagnostics.RawLiveView]::new()
$age.Publish((Sample 255 432 'Index 255'), 100)
Assert-Equal $false $age.Snapshot(600)[0].Stale 'Exactly 500ms is not stale'
Assert-Equal $true $age.Snapshot(600.001)[0].Stale 'Strictly greater than 500ms is stale'
Assert-Equal 432 $age.Snapshot(10000)[0].RawValue 'Silence does not synthesize zero'
Assert-Equal $true $age.Snapshot(10000)[0].Known 'Silence alone does not invalidate known last observation'
Assert-Equal 0 $age.Snapshot(99)[0].AgeMs 'Clock comparison does not expose negative age'

# Console output occurs only when Display is called; invalidated raw values must
# never be presented as currently active. Repeated unchanged snapshots stay quiet.
$console = [Console]::Out
$writer = [IO.StringWriter]::new()
try {
    [Console]::SetOut($writer)
    $display = [Tk75.Diagnostics.RawLiveView]::new()
    $display.Publish((Sample 255 432 'Index 255'), 0)
    Assert-Equal '' $writer.ToString() 'Publish never writes to console'
    $display.Display(0)
    Assert-Equal $true ($writer.ToString().Contains('255')) 'Display includes non-WASD index'
    Assert-Equal $true ($writer.ToString().Contains('432')) 'Display includes observed raw value'
    $firstLength = $writer.ToString().Length
    $display.Display(100)
    Assert-Equal $firstLength $writer.ToString().Length 'No redraw for age change without status/value change'
    $display.Display(501)
    Assert-Equal $true ($writer.ToString().Contains('[letzter Wert]')) 'Stale observation visibly labelled'
    $beforeInvalidation = $writer.ToString().Length
    $display.Invalidate('Synthetic disconnect')
    $display.Display(600)
    $unknownOutput = $writer.ToString().Substring($beforeInvalidation)
    Assert-Equal $true ($unknownOutput.Contains('unbekannt')) 'Invalidated display is unknown'
    Assert-Equal $false ($unknownOutput.Contains('432')) 'Invalidated raw is not shown as active'
    Assert-Equal $true ($unknownOutput.Contains('Synthetic disconnect')) 'Display explains invalidation'
}
finally { [Console]::SetOut($console); $writer.Dispose() }

Write-Output ('PASS: {0} assertions; {1} real replay reports, all 11 observed indices, 256-index synthetic state, independent release/invalidation/staleness; no hardware access.' -f $script:assertions, $reportCount)
