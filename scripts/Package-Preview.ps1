#requires -Version 5.1
[CmdletBinding()]
param([string] $Version = '0.1.0-preview.1')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+-preview\.\d+$') { throw 'Use a preview version such as 0.1.0-preview.1.' }
$packageRoot = Split-Path -Parent $PSScriptRoot
$packageBin = Join-Path $packageRoot 'bin'
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageRoot
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageBin
foreach ($packageRequired in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageBin $packageRequired) -PathType Leaf)) { throw ('Missing build output: ' + $packageRequired + '. Run Build.bat --with-controller first.') }
}
$packageOutput = Join-Path $packageRoot ('release\' + $Version)
if (Test-Path -LiteralPath $packageOutput) { throw 'This release folder already exists; use a new preview version or review it manually.' }
$packageSource = Join-Path $packageOutput 'staging-source\AnalogKeyMapper'
$packageWindows = Join-Path $packageOutput 'staging-windows\AnalogKeyMapper'
New-Item -ItemType Directory -Path $packageSource,$packageWindows -Force | Out-Null
foreach ($packageLine in [IO.File]::ReadAllLines((Join-Path $packageRoot 'SHA256SUMS.txt'))) {
    if ([string]::IsNullOrWhiteSpace($packageLine)) { continue }
    $packageRelative = $packageLine.Substring(66)
    $packageDestination = Join-Path $packageSource $packageRelative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($packageDestination)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageRoot $packageRelative) -Destination $packageDestination
}
Copy-Item -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Destination (Join-Path $packageSource 'SHA256SUMS.txt')
foreach ($packageName in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe','LICENSE','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $packageBin $packageName) -Destination (Join-Path $packageWindows $packageName)
}
Copy-Item -LiteralPath (Join-Path $packageBin 'licenses') -Destination (Join-Path $packageWindows 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $packageRoot 'Verify-Checksums.bat') -Destination (Join-Path $packageWindows 'Verify-Checksums.bat')
New-Item -ItemType Directory -Path (Join-Path $packageWindows 'scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Destination (Join-Path $packageWindows 'scripts\Verify-Checksums.ps1')
$packageStart = @"
Analog Key Mapper $Version - Windows x64 development preview

1. Extract the entire folder to a writable location.
2. Run Verify-Checksums.bat to check the included files.
3. Open AnalogKeyMapper.exe and connect a supported TK75 TMR by USB.

Experimental Xbox output additionally requires the separate usbip-win2 driver.
Setup: https://github.com/SirRanjid/analog-key-mapper/blob/main/docs/building.md
Guide: https://github.com/SirRanjid/analog-key-mapper/blob/main/docs/user-guide.md

This preview is unsigned. Windows may block it; no protection-policy changes
are required or recommended. The local build accepts an unsigned keyboard helper.
Only one experimental Xbox controller is currently supported. DualSense output
is disabled. Keep data/ when updating, and close the mapper before replacing it.

Original code: MIT. The included VIIPER-based helper: GPL v3 or later.
Complete corresponding source and build scripts are provided alongside this
package in AnalogKeyMapper-$Version-source.zip. Keep all license notices.

Developed with ChatGPT, iterative testing and user feedback.
"@
[IO.File]::WriteAllText((Join-Path $packageWindows 'START-HERE.txt'), $packageStart, (New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $packageWindows -BuildOutput
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageSource
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageWindows
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageArchives = @(
    @{Source=(Split-Path -Parent $packageSource);Name=('AnalogKeyMapper-' + $Version + '-source.zip')},
    @{Source=(Split-Path -Parent $packageWindows);Name=('AnalogKeyMapper-' + $Version + '-windows-x64.zip')}
)
$packageHashes = @()
foreach ($packageArchive in $packageArchives) {
    $packageZip = Join-Path $packageOutput $packageArchive.Name
    [IO.Compression.ZipFile]::CreateFromDirectory($packageArchive.Source, $packageZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $packageHashes += (Get-FileHash -LiteralPath $packageZip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $packageArchive.Name
}
[IO.File]::WriteAllLines((Join-Path $packageOutput 'SHA256SUMS.txt'), $packageHashes, (New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageOutput
Write-Output ('Preview archives ready: ' + $packageOutput)
Write-Output 'Packaging does not sign, publish, install or start any executable.'
