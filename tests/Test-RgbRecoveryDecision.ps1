#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$recoveryRoot = Split-Path -Parent $PSScriptRoot
# Pure immutable snapshots and journal decisions; no files, UI, native or HID.
Add-Type -Path (Join-Path $recoveryRoot 'src\Tk75RgbProtocol.cs'), (Join-Path $recoveryRoot 'src\Tk75RgbExchange.cs'), (Join-Path $recoveryRoot 'src\App\RgbRecoveryDecision.cs'), (Join-Path $PSScriptRoot 'RgbRecoveryDecisionHarness.cs')
[RgbRecoveryDecisionHarness]::Run()
