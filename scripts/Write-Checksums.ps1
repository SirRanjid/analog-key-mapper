#requires -Version 5.1
[CmdletBinding()]
param([string] $Directory = (Split-Path -Parent $PSScriptRoot), [switch] $BuildOutput, [switch] $VerifyExisting)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checksumRoot = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\', '/')
$checksumRecords = @()
foreach ($checksumFile in Get-ChildItem -LiteralPath $checksumRoot -Recurse -Force -File) {
    $checksumRelative = $checksumFile.FullName.Substring($checksumRoot.Length + 1).Replace('\', '/')
    if ($checksumRelative -eq 'SHA256SUMS.txt') { continue }
    if ($checksumRelative -match '(^|/)(\.git|\.cache|data|captures)(/|$)') { continue }
    if (-not $BuildOutput -and $checksumRelative -match '^(bin|build|release|backups|tests/tmp|tests/synthetic-[^/]+|tests/rlc-[^/]+)/') { continue }
    if ($checksumFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not valid checksum inputs.' }
    $checksumRecords += [pscustomobject]@{Path=$checksumRelative;Hash=(Get-FileHash -LiteralPath $checksumFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
if ($checksumRecords.Count -eq 0) { throw 'No files were selected for checksums.' }
$checksumLines = @($checksumRecords | Sort-Object Path | ForEach-Object { $_.Hash + '  ' + $_.Path })
if ($VerifyExisting) {
    $checksumExisting = [IO.File]::ReadAllLines((Join-Path $checksumRoot 'SHA256SUMS.txt'))
    if (@(Compare-Object -ReferenceObject $checksumLines -DifferenceObject $checksumExisting -CaseSensitive).Count -ne 0) {
        throw 'The checksum manifest does not cover the exact current file set. Review changes and regenerate it before building or packaging.'
    }
    Write-Output ('PASS: exact manifest coverage for {0} files.' -f $checksumLines.Count)
    return
}
[IO.File]::WriteAllLines((Join-Path $checksumRoot 'SHA256SUMS.txt'), $checksumLines, (New-Object Text.UTF8Encoding($false)))
Write-Output ('Wrote SHA256SUMS.txt for {0} files.' -f $checksumLines.Count)
