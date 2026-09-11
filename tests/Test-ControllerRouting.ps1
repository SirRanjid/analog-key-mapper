#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
if ('Tk75.Mapping.Profile' -as [type]) { throw 'Run controller routing tests in a fresh PowerShell session.' }
# Pure profiles and synthetic data. No native, GUI, output or HID code is loaded.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'), (Join-Path $taskCore 'ProfileJson.cs'),
    (Join-Path $taskCore 'MappingAssignments.cs'), (Join-Path $taskCore 'KeyEditing.cs'),
    (Join-Path $taskCore 'DefaultCalibration.cs'), (Join-Path $taskCore 'ControllerRouting.cs'),
    (Join-Path $PSScriptRoot 'ControllerRoutingHarness.cs')
)
[ControllerRoutingHarness]::Run()
