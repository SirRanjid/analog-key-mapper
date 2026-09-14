#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$preferenceRoot = Split-Path -Parent $PSScriptRoot
Add-Type -Path (Join-Path $preferenceRoot 'src\App\RgbStartupPreferences.cs'), (Join-Path $PSScriptRoot 'RgbStartupPreferencesHarness.cs')
[RgbStartupPreferencesHarness]::Run($PSScriptRoot)
