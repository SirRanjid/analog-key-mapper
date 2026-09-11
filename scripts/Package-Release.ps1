#requires -Version 5.1
[CmdletBinding()]
param([string] $Version, [string] $BuildDirectory = 'bin')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$packageMetadata = Get-Content -LiteralPath (Join-Path $packageRoot 'RELEASE_VERSION.json') -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $packageMetadata.version }
if ($Version -notmatch '^\d+\.\d+\.\d+(-(preview|rc)\.\d+)?$' -or $Version -ne $packageMetadata.version) { throw 'The package version must match RELEASE_VERSION.json.' }
$packageChannel = if ($Version -match '-rc\.') { 'release-candidate' } elseif ($Version -match '-preview\.') { 'preview' } else { 'stable' }
if ($packageChannel -ne $packageMetadata.channel) { throw 'The version and release channel disagree.' }
$packageBin = [IO.Path]::GetFullPath((Join-Path $packageRoot $BuildDirectory))
if (-not $packageBin.StartsWith([IO.Path]::GetFullPath($packageRoot).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'BuildDirectory must stay inside this repository.' }
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageRoot
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $packageRoot -VerifyExisting
& (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Directory $packageBin
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $packageBin -BuildOutput -VerifyExisting
foreach ($packageRequired in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageBin $packageRequired) -PathType Leaf)) { throw ('Missing build output: ' + $packageRequired + '. Run Build.bat --with-controller first.') }
}
foreach ($packageExecutable in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe')) {
    $packageVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageBin $packageExecutable))
    if ($packageVersionInfo.FileVersion -ne $packageMetadata.fileVersion -or $packageVersionInfo.ProductVersion -ne $Version) {
        throw ('Stale or mismatched executable: ' + $packageExecutable + '. Rebuild this source version before packaging.')
    }
}
$packageReceipt = Get-Content -LiteralPath (Join-Path $packageBin 'BUILD-RECEIPT.json') -Raw | ConvertFrom-Json
$packageSourceHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
$packageExecutableNames = @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe')
if ($packageReceipt.version -cne $Version -or $packageReceipt.sourceManifestSha256 -cne $packageSourceHash -or @($packageReceipt.executables).Count -ne 4) {
    throw 'The build receipt does not match this exact source manifest. Run Build.bat --with-controller again.'
}
$packageReceiptSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($packageReceiptEntry in $packageReceipt.executables) {
    if ($packageExecutableNames -cnotcontains $packageReceiptEntry.name -or -not $packageReceiptSeen.Add($packageReceiptEntry.name) -or
        (Get-FileHash -LiteralPath (Join-Path $packageBin $packageReceiptEntry.name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $packageReceiptEntry.sha256) {
        throw 'An executable no longer matches the build receipt. Rebuild the complete package.'
    }
}
$packageOutput = Join-Path $packageRoot ('release\' + $Version)
if (Test-Path -LiteralPath $packageOutput) { throw 'This release folder already exists; existing releases are never overwritten.' }
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
foreach ($packageName in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe','LICENSE','THIRD-PARTY-NOTICES.md','BUILD-RECEIPT.json')) {
    Copy-Item -LiteralPath (Join-Path $packageBin $packageName) -Destination (Join-Path $packageWindows $packageName)
}
Copy-Item -LiteralPath (Join-Path $packageBin 'licenses') -Destination (Join-Path $packageWindows 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $packageRoot 'Verify-Checksums.bat') -Destination (Join-Path $packageWindows 'Verify-Checksums.bat')
New-Item -ItemType Directory -Path (Join-Path $packageWindows 'scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Checksums.ps1') -Destination (Join-Path $packageWindows 'scripts\Verify-Checksums.ps1')
Copy-Item -LiteralPath (Join-Path $packageRoot 'RELEASE_VERSION.json') -Destination (Join-Path $packageWindows 'RELEASE_VERSION.json')
$packageProvenance = [ordered]@{
    version = $Version
    channel = $packageChannel
    sourceManifestSha256 = $packageReceipt.sourceManifestSha256
    executables = @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe','ViiperOutputHost.exe') | ForEach-Object {
        $packageExecutablePath = Join-Path $packageBin $_
        $packageSignature = Get-AuthenticodeSignature -LiteralPath $packageExecutablePath
        [ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath $packageExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant();signatureStatus=$packageSignature.Status.ToString()}
    }
}
$packageProvenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageWindows 'BUILD-INFO.json') -Encoding utf8
$packageStart = @"
Analog Key Mapper $Version - Windows x64 ($packageChannel)

1. Extract the entire folder to a writable location.
2. Run Verify-Checksums.bat to check the included files.
3. Open AnalogKeyMapper.exe and connect a supported TK75 TMR by USB.

Xbox and DualSense output additionally require the separate usbip-win2 driver.
Setup: https://github.com/SirRanjid/analog-key-mapper/blob/main/docs/building.md
Guide: https://github.com/SirRanjid/analog-key-mapper/blob/main/docs/user-guide.md
Status: https://github.com/SirRanjid/analog-key-mapper/blob/main/docs/status.md

This free community build is unsigned. Windows may block the application or
its helpers. Do not change protection policies to run it. Building from source
does not guarantee acceptance by Windows. Checksums verify file integrity;
they are not a trusted-publisher signature. The local build accepts an unsigned
keyboard helper within the app, while Windows still decides what may run.
Profiles offer 32 output slots, freely assigned to Xbox or DualSense. Windows
allows at most four XInput controllers, including physical ones. DualSense uses
HID separately and does not consume those XInput slots. An earlier helper build
passed real-device acceptance with two Xbox plus two DualSense outputs.
The complete current build still needs live keyboard, controller and game
acceptance. Automated checks and synthetic mapping measurements do not replace
that test. See the status link above for the exact tested scope.
Common buttons, triggers and sticks are supported. No PS5-console, touchpad,
motion-sensor or other DualSense-extra compatibility is guaranteed.
Update: exit the mapper through its tray menu, back up the entire old folder,
extract this download into a new folder, then copy the old data/ folder into it.
Keep that original backup until your mappings and lighting work as expected.
If startup was enabled, turn it off in the old app before moving the folder,
then enable it in the new app so Windows starts the correct executable.
Remove: turn off Windows startup, exit the mapper and delete its folder.
The optional USB/IP driver is installed/uninstalled separately; see the guide.

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
Write-Output ('Release archives ready: ' + $packageOutput)
Write-Output 'Packaging does not sign, publish, install or start any executable.'
