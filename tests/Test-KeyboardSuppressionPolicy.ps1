#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskApp = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\App'
if ('Tk75.App.KeyboardSuppressionPolicy' -as [type]) { throw 'Run in a fresh PowerShell session.' }
# Pure transitions and scan-map data only. The native service is deliberately
# excluded: no hook, worker, GUI, input synthesis, HID, or output is started.
Add-Type -Path @(
    (Join-Path $taskApp 'KeyboardSuppressionPolicy.cs'),
    (Join-Path $taskApp 'KeyboardScanCodes.cs'),
    (Join-Path $PSScriptRoot 'KeyboardSuppressionPolicyHarness.cs')
)
[KeyboardSuppressionPolicyHarness]::Run()
