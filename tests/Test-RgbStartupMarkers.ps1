#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$markerRoot = Split-Path -Parent $PSScriptRoot
# Pure snapshots and inference; no UI, device reads or hardware writes.
Add-Type -Path (Join-Path $markerRoot 'src\Tk75RgbProtocol.cs'), (Join-Path $markerRoot 'src\Tk75RgbExchange.cs'), (Join-Path $markerRoot 'src\App\RgbStartupMarkers.cs'), (Join-Path $PSScriptRoot 'RgbStartupMarkersHarness.cs')
[RgbStartupMarkersHarness]::Run()
