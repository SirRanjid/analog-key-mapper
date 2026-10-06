#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$typeArguments = @{ Path = @(
    (Join-Path $taskRoot 'src\Core\MappingModels.cs'), (Join-Path $taskRoot 'src\Core\MappingValidation.cs'),
    (Join-Path $taskRoot 'src\Core\ProfileJson.cs'), (Join-Path $taskRoot 'src\Core\InputLearning.cs'),
    (Join-Path $taskRoot 'src\Core\GamepadInputProfile.cs'), (Join-Path $taskRoot 'src\Core\BezierCurve.cs'),
    (Join-Path $taskRoot 'src\Core\MappingEngine.cs'), (Join-Path $taskRoot 'src\Tk75TravelReport.cs'),
    (Join-Path $taskRoot 'src\RawLiveView.cs'), (Join-Path $taskRoot 'src\App\ILearnedInputDeviceSource.cs'),
    (Join-Path $taskRoot 'src\App\LearnedInputRouting.cs'), (Join-Path $PSScriptRoot 'GamepadRoutingHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Core.dll', 'System.Xml.dll') }
# Synthetic input sources only; no HID handles or virtual output drivers.
Add-Type @typeArguments
[GamepadRoutingHarness]::Run()
