#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
# Pure profiles and synthetic controller values; no hardware or output drivers.
$typeArguments = @{ Path = @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'), (Join-Path $taskCore 'InputLearning.cs'),
    (Join-Path $taskCore 'GamepadInputProfile.cs'), (Join-Path $taskCore 'ControllerRouting.cs'),
    (Join-Path $taskCore 'ProfileChangeImpact.cs'), (Join-Path $taskCore 'BezierCurve.cs'),
    (Join-Path $taskCore 'MappingEngine.cs'), (Join-Path $PSScriptRoot 'GamepadInputProfilesHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Core.dll', 'System.Xml.dll') }
Add-Type @typeArguments
[GamepadInputProfilesHarness]::Run()
