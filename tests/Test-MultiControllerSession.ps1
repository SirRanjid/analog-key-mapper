#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskCore = Join-Path $taskWorkspace 'src\Core'
if ('Tk75.App.MultiControllerSession' -as [type]) { throw 'Run this source check in a fresh PowerShell session.' }
# Compile only new pure coordination and actual core routing. The production
# adapter and all MappingSession/reader/UI/native code are intentionally absent.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'PreviewSnapshot.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'), (Join-Path $taskCore 'ProfileJson.cs'),
    (Join-Path $taskCore 'MappingAssignments.cs'), (Join-Path $taskCore 'ControllerRouting.cs'),
    (Join-Path $taskWorkspace 'src\App\MultiControllerSession.cs'),
    (Join-Path $taskWorkspace 'src\App\ControllerRelease.cs'),
    (Join-Path $PSScriptRoot 'MultiControllerSessionHarness.cs')
)
[MultiControllerSessionHarness]::Run()
