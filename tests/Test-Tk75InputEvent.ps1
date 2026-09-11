#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('Tk75InputEventHarness' -as [type]) { throw 'Use a fresh PowerShell session for current input-event sources.' }
$sources = @(
    (Join-Path $workspace 'src\Tk75TravelReport.cs'),
    (Join-Path $workspace 'src\Tk75InputEvent.cs'),
    (Join-Path $PSScriptRoot 'Tk75InputEventHarness.cs')
)
Add-Type -Path $sources
[Tk75InputEventHarness]::Run()
