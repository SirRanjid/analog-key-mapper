#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
if ('Tk75.Mapping.Profile' -as [type]) { throw 'Run this pure-core test in a fresh PowerShell session.' }
# Pure arithmetic, synthetic samples and in-memory JSON only; no GUI/native/HID.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'), (Join-Path $taskCore 'KeyEditing.cs'),
    (Join-Path $PSScriptRoot 'BezierCurveHarness.cs')
)
[BezierCurveHarness]::Run()
