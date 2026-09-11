#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $HelperPath,
    [ValidateRange(1, 4)][int] $XboxCount = 2,
    [ValidateRange(1, 28)][int] $DualSenseCount = 2
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($XboxCount + $DualSenseCount -gt 32) { throw 'At most 32 output helpers may be requested.' }
# Deliberate live acceptance only. This subdirectory is not enumerated by Test-All.
# The harness refuses a running mapper/monitor/helper before creating devices.
$liveRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$liveHelper = (Resolve-Path -LiteralPath $HelperPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $liveHelper -PathType Leaf)) { throw 'HelperPath must identify an existing output executable.' }
if ('LiveMixedOutputHarness' -as [type]) { throw 'Run live acceptance in a fresh PowerShell session.' }
$liveSources = @(
    'src/Core/MappingModels.cs', 'src/Output/ViGEmOutput.cs', 'src/Output/OutputHost.cs',
    'src/Output/IsolatedOutput.cs', 'tests/live/LiveMixedOutputHarness.cs'
) | ForEach-Object { Join-Path $liveRoot $_ }
$liveArguments = @{ Path = $liveSources }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $liveArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Core.dll') }
Add-Type @liveArguments
Write-Output ("EXPLICIT LIVE TEST: creating {0} Xbox and {1} DualSense outputs; measuring owned helpers for 10 seconds. No crash injection or protection-policy changes." -f $XboxCount, $DualSenseCount)
[LiveMixedOutputHarness]::Run($liveHelper, $XboxCount, $DualSenseCount)
