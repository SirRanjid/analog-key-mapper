#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('Tk75.Output.ControllerStartupObservation' -as [type]) { throw 'Bitte eine neue PowerShell-Sitzung verwenden.' }
# Pure XInput observation, with synthetic snapshots. No P/Invoke, process or HID.
Add-Type -Path (Join-Path $workspace 'src\Output\ControllerStartupObservation.cs')
$script:checks = 0
function Check([bool]$value, [string]$message) {
    $script:checks++
    if (-not $value) { throw ('FAIL: ' + $message) }
}
function Rejected([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    Check $rejected $message
}
function Snapshot([int[]]$active = @()) {
    $slots = [Tk75.Output.ControllerStartupObservation+Slot[]]::new(4)
    for ($i = 0; $i -lt 4; $i++) {
        $slots[$i] = [Tk75.Output.ControllerStartupObservation+Slot]::new()
        $slots[$i].Index = $i
        $slots[$i].Status = $(if ($active -contains $i) { 0 } else { 1167 })
    }
    return ,$slots
}

$before = Snapshot @(0)
$before[0].LX = 32000
$watch = [Tk75.Output.ControllerStartupObservation]::new($before)
Check ($watch.NewSlot -eq -1 -and $watch.Samples -eq 0) 'No new device invented'
$watch.Add($before)
Check ($null -eq $watch.Failure) 'Existing active controller is untouched'
$sample = Snapshot @(0,2)
$sample[0].LT = 255
$watch.Add($sample)
Check ($watch.NewSlot -eq 2 -and $watch.First.IsNeutral -and $watch.Samples -eq 1) 'Exactly one new neutral slot'
$sample[2].LX = 1
Check $watch.Last.IsNeutral 'Input snapshot was copied'
$returned = $watch.Last
$returned.LX = 123
Check $watch.Last.IsNeutral 'Returned observation cannot mutate stored evidence'
$before[1].Status = 0
$sample = Snapshot @(0,2)
$watch.Add($sample)
Check ($null -eq $watch.Failure -and $watch.Samples -eq 2) 'Baseline was copied'
Check (-not $watch.AllNewSlotsRemoved($sample)) 'Still-visible controller is not removed'
Check $watch.AllNewSlotsRemoved((Snapshot @(0))) 'Existing controller does not prevent own removal'
Check (-not $watch.AllNewSlotsRemoved((Snapshot @(0,1)))) 'Unobserved new slot prevents unproven cleanup'

foreach ($channel in @('Buttons','LT','RT','LX','LY','RX','RY')) {
    $watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
    $sample = Snapshot @(1)
    $sample[1].$channel = 1
    $watch.Add($sample)
    Check (-not [string]::IsNullOrEmpty($watch.Failure)) ('Active start detected: ' + $channel)
    $watch.Add((Snapshot @(1)))
    Check (-not [string]::IsNullOrEmpty($watch.Failure)) ('Later neutral cannot erase fault: ' + $channel)
    Check (-not $watch.First.IsNeutral -and $watch.Last.IsNeutral) ('First and later samples preserved: ' + $channel)
}
foreach ($axis in @('LX','LY','RX','RY')) {
    $watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
    $sample = Snapshot @(0); $sample[0].$axis = -32768
    $watch.Add($sample)
    Check (-not [string]::IsNullOrEmpty($watch.Failure)) ('Negative axis detected: ' + $axis)
}
$watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
$watch.Add((Snapshot @(1,2)))
Check (-not [string]::IsNullOrEmpty($watch.Failure)) 'Simultaneous new slots are ambiguous'
$watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
$watch.Add((Snapshot @(1))); $watch.Add((Snapshot))
Check (-not [string]::IsNullOrEmpty($watch.Failure)) 'Disappearance before deliberate cleanup fails'
$watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
$watch.Add((Snapshot @(1))); $watch.Add((Snapshot @(2)))
Check (-not [string]::IsNullOrEmpty($watch.Failure)) 'Unexpected slot change fails'
Rejected { [Tk75.Output.ControllerStartupObservation]::new((Snapshot @(0,1,2,3))) } 'Full controller slots reject before creation'
Rejected { [Tk75.Output.ControllerStartupObservation]::new($null) } 'Missing baseline rejects'
$bad = Snapshot; $bad[3].Index = 0
Rejected { [Tk75.Output.ControllerStartupObservation]::new($bad) } 'Slot identity mismatch rejects'
$bad = Snapshot; $bad[2].Status = 5
Rejected { [Tk75.Output.ControllerStartupObservation]::new($bad) } 'API error is not mistaken for free slot'
$watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
Rejected { $watch.Add($bad) } 'API error cannot pass neutral observation'
Rejected { $watch.AllNewSlotsRemoved($bad) } 'API error cannot prove removal'
$bad = Snapshot; $bad[1] = $null
Rejected { $watch.Add($bad) } 'Missing slot rejects'
$slot = [Tk75.Output.ControllerStartupObservation+Slot]::new()
$slot.Status = 1167
Check (-not $slot.IsNeutral) 'Disconnected does not mean proven neutral'
$watch = [Tk75.Output.ControllerStartupObservation]::new((Snapshot))
$watch.Add((Snapshot), 10)
Check ($null -eq $watch.FirstObservedMs) 'No-device time does not count toward neutral observation'
foreach ($time in @(100,150,200,300,400,500,600)) { $watch.Add((Snapshot @(1)), $time) }
Check ($watch.FirstObservedMs -eq 100 -and $watch.LastObservedMs -eq 600) 'Time span belongs to actual new-slot samples'
Check ($watch.LargestGapMs -eq 100 -and $null -eq $watch.Failure) 'Bounded sampling gaps are recorded'
Rejected { $watch.Add((Snapshot @(1)), 599) } 'Backward time rejects before changing observation'
Check ($watch.LastObservedMs -eq 600) 'Rejected time cannot change evidence'
$watch.Add((Snapshot @(1)), 701)
Check (-not [string]::IsNullOrEmpty($watch.Failure)) 'Missing sampling interval cannot falsely prove sustained neutral'
$watch.Add((Snapshot @(1)), 705)
Check ($watch.LargestGapMs -eq 101 -and -not [string]::IsNullOrEmpty($watch.Failure)) 'Later fast sampling cannot hide an observation gap'
Write-Output ('PASS: {0} pure controller startup observation checks; no native calls.' -f $script:checks)
