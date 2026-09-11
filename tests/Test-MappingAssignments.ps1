#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('MappingAssignmentsHarness' -as [type]) { throw 'Bitte eine neue PowerShell-Sitzung fuer den aktuellen Testquellcode starten.' }
$typeArguments = @{ Path = @(
    (Join-Path $workspace 'src\Core\MappingModels.cs'),
    (Join-Path $workspace 'src\Core\MappingValidation.cs'),
    (Join-Path $workspace 'src\Core\ProfileJson.cs'),
    (Join-Path $workspace 'src\Core\MappingAssignments.cs'),
    (Join-Path $PSScriptRoot 'MappingAssignmentsHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Core.dll') }
Add-Type @typeArguments
[MappingAssignmentsHarness]::Run()
