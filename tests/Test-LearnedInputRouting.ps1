#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('LearnedInputRoutingHarness' -as [type]) { throw 'Run the current source in a fresh PowerShell session.' }
$typeArguments = @{ Path = @(
    (Join-Path $workspace 'src\Core\MappingModels.cs'),
    (Join-Path $workspace 'src\Core\InputLearning.cs'),
    (Join-Path $workspace 'src\Tk75TravelReport.cs'),
    (Join-Path $workspace 'src\RawLiveView.cs'),
    (Join-Path $workspace 'src\App\ILearnedInputDeviceSource.cs'),
    (Join-Path $workspace 'src\App\LearnedInputRouting.cs'),
    (Join-Path $PSScriptRoot 'LearnedInputRoutingHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Core.dll') }
# Fake devices only. No native window, HID handle, keyboard hook, or driver.
Add-Type @typeArguments
[LearnedInputRoutingHarness]::Run()
