#requires -Version 5.1
[CmdletBinding()]
param([switch] $WithController, [switch] $Strict)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$localBuildRoot = Split-Path -Parent $PSScriptRoot
$localOutput = Join-Path $localBuildRoot 'bin'
if (Test-Path -LiteralPath (Join-Path $localBuildRoot 'SHA256SUMS.txt')) {
    & (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $localBuildRoot
}
$localBuildArgs = @{OutputDirectory='bin'}
if (-not $Strict) { $localBuildArgs.AllowUnsignedMonitor = $true }
& (Join-Path $localBuildRoot 'Build.ps1') @localBuildArgs
if ($WithController) { & (Join-Path $localBuildRoot 'Build-OutputHelper.ps1') -OutputDirectory 'bin' }
Copy-Item -LiteralPath (Join-Path $localBuildRoot 'LICENSE') -Destination (Join-Path $localOutput 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $localBuildRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $localOutput 'THIRD-PARTY-NOTICES.md') -Force
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $localOutput -BuildOutput
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $localOutput
if ($Strict) { Write-Output 'Strict build: live keyboard input requires a valid signature on Tk75Monitor.exe.' }
else { Write-Output 'Local development build: accepts an unsigned keyboard helper. Windows policies are unchanged.' }
if (-not $WithController) { Write-Output 'Virtual controller output additionally needs Build.bat --with-controller, Go and the USB/IP driver. See docs/building.md.' }
Write-Output 'No application, hardware helper or driver was started or installed.'
