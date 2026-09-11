#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$integrationRoot = Split-Path -Parent $PSScriptRoot
if ('MultiMappingIntegrationHarness' -as [type]) { throw 'Run this integration suite in a fresh PowerShell session.' }
$integrationSources = @(
    'src/Core/MappingModels.cs', 'src/Core/MappingValidation.cs', 'src/Core/MappingEngine.cs',
    'src/Core/PreviewSnapshot.cs', 'src/Core/PreviewComposer.cs', 'src/Core/BezierCurve.cs',
    'src/Core/ProfileJson.cs', 'src/Core/MappingAssignments.cs', 'src/Core/ControllerRouting.cs',
    'src/Output/ViGEmOutput.cs', 'src/Output/ControllerOutputs.cs', 'src/Output/OutputHost.cs',
    'src/Output/IsolatedOutput.cs', 'src/App/ControllerRelease.cs', 'src/App/MappingSession.cs', 'src/App/MultiControllerSession.cs',
    'tests/MultiMappingIntegrationHarness.cs'
) | ForEach-Object { Join-Path $integrationRoot $_ }
$integrationArguments = @{ Path = $integrationSources }
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $integrationArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Core.dll')
}
# Native declarations compile, but every output factory returns a C# fake.
# This runs the real coordinator and real worker loops without a device/helper.
Add-Type @integrationArguments
[MultiMappingIntegrationHarness]::Run()
