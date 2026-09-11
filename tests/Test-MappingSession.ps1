#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('MappingSessionHarness' -as [type]) { throw 'Bitte eine neue PowerShell-Sitzung fuer den aktuellen Testquellcode starten.' }
$typeArguments = @{ Path = @(
    (Join-Path $workspace 'src\Core\MappingModels.cs'),
    (Join-Path $workspace 'src\Core\MappingValidation.cs'),
    (Join-Path $workspace 'src\Core\MappingEngine.cs'),
    (Join-Path $workspace 'src\Core\PreviewSnapshot.cs'),
    (Join-Path $workspace 'src\Core\PreviewComposer.cs'),
    (Join-Path $workspace 'src\Core\BezierCurve.cs'),
    (Join-Path $workspace 'src\Core\ProfileJson.cs'),
    (Join-Path $workspace 'src\Output\ViGEmOutput.cs'),
    (Join-Path $workspace 'src\Output\ControllerOutputs.cs'),
    (Join-Path $workspace 'src\Output\OutputHost.cs'),
    (Join-Path $workspace 'src\Output\IsolatedOutput.cs'),
    (Join-Path $workspace 'src\App\MappingSession.cs'),
    (Join-Path $PSScriptRoot 'MappingSessionHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Core.dll') }
# Native output declarations compile only; every actual output is a C# fake.
# Lightweight stand-ins isolate production ReaderSession and file-store startup.
Add-Type @typeArguments
[MappingSessionHarness]::Run()
