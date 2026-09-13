#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
# Pure in-memory arithmetic only; no controls, native calls, HID or controller.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'),
    (Join-Path $taskCore 'CurvePointEditing.cs'), (Join-Path $taskCore 'CurveResponsePreview.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'), (Join-Path $taskCore 'KeyEditing.cs'), (Join-Path $taskCore 'CurveDynamicsPreview.cs'),
    (Join-Path $PSScriptRoot 'CurveResponsePreviewHarness.cs')
)
[CurveResponsePreviewHarness]::Run()
