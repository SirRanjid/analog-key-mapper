#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Physical key-code parsing only; no windows, registration or keyboard access.
Add-Type -Path @(
    (Join-Path $taskRoot 'src\App\KeyboardLearningControls.cs'),
    (Join-Path $PSScriptRoot 'KeyboardLearningControlsHarness.cs')
)
[KeyboardLearningControlsHarness]::Run()
