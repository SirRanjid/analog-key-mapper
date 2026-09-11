#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$taskCore = Join-Path $workspace 'src\Core'
# Pure algorithms and synthetic vectors only. No blocked harness replay or native code.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'), (Join-Path $taskCore 'ProfileJson.cs'),
    (Join-Path $taskCore 'KeyEditing.cs'), (Join-Path $PSScriptRoot 'KeyInputHarness.cs')
)
[KeyInputHarness]::Run()
