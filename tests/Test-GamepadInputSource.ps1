#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Synthetic protocol/identity/lifecycle checks. Never opens controller hardware.
$taskArguments = @{ Path = @(
    (Join-Path $taskRoot 'src\Core\InputLearning.cs'),
    (Join-Path $taskRoot 'src\Core\MappingModels.cs'),
    (Join-Path $taskRoot 'src\HidNative.cs'),
    (Join-Path $taskRoot 'src\App\ILearnedInputDeviceSource.cs'),
    (Join-Path $taskRoot 'src\App\GamepadInputSource.cs'),
    (Join-Path $PSScriptRoot 'GamepadInputSourceHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $taskArguments.ReferencedAssemblies = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll') }
Add-Type @taskArguments
[GamepadInputSourceHarness]::Run()
