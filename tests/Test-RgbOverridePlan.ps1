#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
if ('RgbOverridePlanHarness' -as [type]) { throw 'Run RGB plan tests in a fresh PowerShell session.' }
# Pure metadata/planning only; no UI, HID, helper, native output or lighting I/O.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'), (Join-Path $taskCore 'ControllerRouting.cs'),
    (Join-Path $taskCore 'RgbOverridePlan.cs'), (Join-Path $PSScriptRoot 'RgbOverridePlanHarness.cs')
)
[RgbOverridePlanHarness]::Run()
