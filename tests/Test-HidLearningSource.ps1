#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Pure descriptor/data-index checks. No HID DLL, device or window is opened.
$taskArguments = @{ Path = @(
    (Join-Path $taskRoot 'src\Core\InputLearning.cs'),
    (Join-Path $taskRoot 'src\HidNative.cs'),
    (Join-Path $taskRoot 'src\KeyMap.cs'),
    (Join-Path $taskRoot 'src\App\ILearnedInputDeviceSource.cs'),
    (Join-Path $taskRoot 'src\App\HidLearningSource.cs'),
    (Join-Path $PSScriptRoot 'HidLearningSourceHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $taskArguments.ReferencedAssemblies = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll') }
Add-Type @taskArguments
[HidLearningSourceHarness]::Run()
