#requires -Version 5.1
[CmdletBinding()]
param([switch] $WithController, [switch] $Strict, [string] $OutputDirectory = 'bin', [string] $GoExecutable = 'go', [switch] $Offline)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$localBuildRoot = Split-Path -Parent $PSScriptRoot
$localOutput = [IO.Path]::GetFullPath((Join-Path $localBuildRoot $OutputDirectory))
$localSourceHash = $null
if (Test-Path -LiteralPath (Join-Path $localBuildRoot 'SHA256SUMS.txt')) {
    & (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $localBuildRoot
    & (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $localBuildRoot -VerifyExisting
    $localSourceHash = (Get-FileHash -LiteralPath (Join-Path $localBuildRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$localBuildArgs = @{OutputDirectory=$OutputDirectory}
if (-not $Strict) { $localBuildArgs.AllowUnsignedMonitor = $true }
& (Join-Path $localBuildRoot 'Build.ps1') @localBuildArgs
if ($WithController) { & (Join-Path $localBuildRoot 'Build-OutputHelper.ps1') -OutputDirectory $OutputDirectory -GoExecutable $GoExecutable -Offline:$Offline }
Copy-Item -LiteralPath (Join-Path $localBuildRoot 'LICENSE') -Destination (Join-Path $localOutput 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $localBuildRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $localOutput 'THIRD-PARTY-NOTICES.md') -Force
if ($WithController -and $localSourceHash) {
    & (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $localBuildRoot
    & (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $localBuildRoot -VerifyExisting
    if ((Get-FileHash -LiteralPath (Join-Path $localBuildRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $localSourceHash) { throw 'The source manifest changed during the build.' }
    $localMetadata = Get-Content -LiteralPath (Join-Path $localBuildRoot 'RELEASE_VERSION.json') -Raw | ConvertFrom-Json
    $localReceipt = [ordered]@{
        version=$localMetadata.version
        sourceManifestSha256=$localSourceHash
        executables=@('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe') | ForEach-Object {
            [ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $localOutput $_) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    }
    $localReceipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $localOutput 'BUILD-RECEIPT.json') -Encoding utf8
}
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $localOutput -BuildOutput
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $localOutput
if ($Strict) { Write-Output 'Strict build: live keyboard input requires a valid signature on Tk75Monitor.exe.' }
else { Write-Output 'Local development build: accepts an unsigned keyboard helper. Windows policies are unchanged.' }
if (-not $WithController) { Write-Output 'Virtual controller output additionally needs Build.bat --with-controller, Go and the USB/IP driver. See docs/building.md.' }
Write-Output 'No application, hardware helper or driver was started or installed.'
