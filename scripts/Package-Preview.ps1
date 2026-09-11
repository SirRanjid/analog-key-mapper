#requires -Version 5.1
[CmdletBinding()]
param([string] $Version, [string] $BuildDirectory = 'bin')
# Compatibility entry point; shared packaging validates the exact build version.
& (Join-Path $PSScriptRoot 'Package-Release.ps1') @PSBoundParameters
