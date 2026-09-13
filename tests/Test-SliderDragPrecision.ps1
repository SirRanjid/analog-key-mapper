#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Pure pointer arithmetic only: no controls, native input, windows or hardware.
Add-Type -Path @(
    (Join-Path $taskRoot 'src\App\SliderDragPrecision.cs'),
    (Join-Path $PSScriptRoot 'SliderDragPrecisionHarness.cs')
)
[SliderDragPrecisionHarness]::Run()
