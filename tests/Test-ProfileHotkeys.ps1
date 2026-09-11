#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$hotkeyCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
if ('ProfileHotkeysHarness' -as [type]) { throw 'Run profile shortcut tests in a fresh PowerShell session.' }
# Pure profile metadata only; no UI, native key reads, hotkey registration or hook.
Add-Type -Path @(
    (Join-Path $hotkeyCore 'MappingModels.cs'), (Join-Path $hotkeyCore 'MappingValidation.cs'),
    (Join-Path $hotkeyCore 'ProfileJson.cs'), (Join-Path $hotkeyCore 'ControllerRouting.cs'), (Join-Path $hotkeyCore 'ProfileChangeImpact.cs'),
    (Join-Path $PSScriptRoot 'ProfileHotkeysHarness.cs')
)
[ProfileHotkeysHarness]::Run()
