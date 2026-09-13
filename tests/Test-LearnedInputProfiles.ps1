#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskCore = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Core'
# Pure profile serialization and validation; no devices, controls or native I/O.
Add-Type -Path @(
    (Join-Path $taskCore 'MappingModels.cs'),
    (Join-Path $taskCore 'MappingValidation.cs'),
    (Join-Path $taskCore 'ProfileJson.cs'),
    (Join-Path $taskCore 'InputLearning.cs'),
    (Join-Path $PSScriptRoot 'LearnedInputProfilesHarness.cs')
)
[LearnedInputProfilesHarness]::Run()
