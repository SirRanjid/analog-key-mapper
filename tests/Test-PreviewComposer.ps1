#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
if ('Tk75.Mapping.Profile' -as [type]) { throw 'Run this pure-core test in a fresh PowerShell session.' }
# Synthetic raw values and detached state dictionaries only. No workers, output,
# GUI, native imports, HID, helper starts or filesystem profile writes.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'), (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'BezierCurve.cs'), (Join-Path $taskCore 'MappingEngine.cs'),
    (Join-Path $taskCore 'PreviewSnapshot.cs'), (Join-Path $taskCore 'PreviewComposer.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'), (Join-Path $taskCore 'ControllerRouting.cs'),
    (Join-Path $PSScriptRoot 'PreviewComposerHarness.cs')
)
[PreviewComposerHarness]::Run()
