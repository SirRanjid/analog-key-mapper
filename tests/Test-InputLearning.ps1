#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Pure descriptors, report samples and a supplied clock; no UI or hardware.
Add-Type -Path @(
    (Join-Path $taskRoot 'src\Core\InputLearning.cs'),
    (Join-Path $PSScriptRoot 'InputLearningHarness.cs')
)
[InputLearningHarness]::Run()
