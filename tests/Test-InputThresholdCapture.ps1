$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# Pure pressure samples only: no control handles, windows, HID or controller access.
Add-Type -Path @(
    (Join-Path $taskWorkspace 'src\Core\InputThresholdCapture.cs'),
    (Join-Path $PSScriptRoot 'InputThresholdCaptureHarness.cs')
)
$taskChecks = [InputThresholdCaptureHarness]::Run()
Write-Output "Input threshold capture checks: $taskChecks PASS"
