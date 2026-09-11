#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# Pure state/transport callbacks only; no native declarations, HID, helper start or physical writes.
Add-Type -Path (Join-Path $taskWorkspace 'src\Tk75RgbProtocol.cs'), (Join-Path $taskWorkspace 'src\Tk75RgbExchange.cs'), (Join-Path $PSScriptRoot 'Tk75RgbExchangeHarness.cs')
$taskChecks = [Tk75RgbExchangeHarness]::Run()
Write-Output "PASS: $taskChecks pure RGB compare/exchange checks; fake device only, no HID or native execution."
