#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Pure layout arithmetic only: no forms, native controls, windows or hardware.
Add-Type -Path @(
    (Join-Path $taskRoot 'src\App\CurveEditorGeometry.cs'),
    (Join-Path $PSScriptRoot 'CurveEditorGeometryHarness.cs')
)
[CurveEditorGeometryHarness]::Run()
