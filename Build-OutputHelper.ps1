#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $GoExecutable = 'go',
    [string] $OutputDirectory = 'bin',
    [switch] $Offline
)
$ErrorActionPreference = 'Stop'
$helperRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/')
$helperSource = Join-Path $helperRoot 'src\ViiperOutputHost'
$helperOutput = [IO.Path]::GetFullPath((Join-Path $helperRoot $OutputDirectory))
if (-not $helperOutput.StartsWith($helperRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output directory must be inside this repository.'
}
$helperGo = Get-Command $GoExecutable -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $helperGo) { throw 'Go is required (1.26.2 or newer; verified with 1.27.1). Install it separately or pass -GoExecutable.' }
$helperBinary = Join-Path $helperOutput 'ViiperOutputHost.exe'
foreach ($helperProcess in @(Get-Process ViiperOutputHost -ErrorAction SilentlyContinue)) {
    if ($helperProcess.Path -and [IO.Path]::GetFullPath($helperProcess.Path) -eq $helperBinary) {
        throw 'The output helper in the target directory is running. Close the mapper before rebuilding it.'
    }
}
$helperVariables = @('CGO_ENABLED','GOOS','GOARCH','GOTOOLCHAIN','GOPROXY')
$helperPrevious = @{}
foreach ($helperVariable in $helperVariables) { $helperPrevious[$helperVariable] = [Environment]::GetEnvironmentVariable($helperVariable, 'Process') }
$helperLocation = Get-Location
try {
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:GOTOOLCHAIN = 'local'
    if ($Offline) { $env:GOPROXY = 'off' }
    Set-Location -LiteralPath $helperSource
    & $helperGo.Source version
    if ($LASTEXITCODE -ne 0) { throw 'Go could not be started normally.' }
    [void][IO.Directory]::CreateDirectory($helperOutput)
    # Build only: no helper, native test, driver installer or controller is run.
    # The source release includes the pinned dependencies in vendor/.
    # A deliberately unvendored checkout can use normal Go module resolution.
    # The checked-in module versions and go.sum remain unchanged.
    $helperModuleMode = if (Test-Path -LiteralPath (Join-Path $helperSource 'vendor\modules.txt') -PathType Leaf) { '-mod=vendor' } else { '-mod=readonly' }
    & $helperGo.Source build $helperModuleMode -trimpath -buildvcs=false -o $helperBinary './cmd/tk75-output-host'
    if ($LASTEXITCODE -ne 0) { throw 'Output helper compilation failed. An offline build needs the included vendor sources or all pinned modules in the Go cache.' }
    $helperLicenseOutput = Join-Path $helperOutput 'licenses\viiper-output-helper'
    [void][IO.Directory]::CreateDirectory($helperLicenseOutput)
    foreach ($helperNotice in @('LICENSE','NOTICE.md','SOURCE-MANIFEST.json','VENDOR-MANIFEST.json')) {
        Copy-Item -LiteralPath (Join-Path $helperSource $helperNotice) -Destination (Join-Path $helperLicenseOutput $helperNotice) -Force
    }
    foreach ($helperNotice in Get-ChildItem -LiteralPath (Join-Path $helperSource 'licenses') -File) {
        Copy-Item -LiteralPath $helperNotice.FullName -Destination (Join-Path $helperLicenseOutput $helperNotice.Name) -Force
    }
    Write-Output ('Built (not started): ' + $helperBinary)
    Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $helperBinary -Algorithm SHA256).Hash)
    Write-Output 'The VIIPER-based helper has its own GPL license. Distribute its corresponding source and third-party notices with any helper binary.'
}
finally {
    Set-Location -LiteralPath $helperLocation.Path
    foreach ($helperVariable in $helperVariables) { [Environment]::SetEnvironmentVariable($helperVariable, $helperPrevious[$helperVariable], 'Process') }
}
