# Pure injected registration only: no native calls, process shutdown, GUI or HID.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\src\MonitorShutdownOrder.cs'), (Join-Path $PSScriptRoot 'MonitorShutdownOrderHarness.cs')
$shutdownOrderChecks = [MonitorShutdownOrderHarness]::Run()
Write-Output "PASS: $shutdownOrderChecks helper shutdown-order checks; injected setter only, no native calls or hardware."
