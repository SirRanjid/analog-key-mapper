# Pure deterministic input/deadline coordination. No HID, native calls or sleeping.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\src\RgbInputSettling.cs'), (Join-Path $PSScriptRoot 'RgbInputSettlingHarness.cs')
$taskChecks = [RgbInputSettlingHarness]::Run()
Write-Output "PASS: $taskChecks RGB input-settling checks; fake monotonic clock and input events only."
