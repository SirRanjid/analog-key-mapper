#requires -Version 5.1
[CmdletBinding()]
param([string] $Directory = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checksumRoot = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\', '/')
$checksumManifest = Join-Path $checksumRoot 'SHA256SUMS.txt'
$checksumSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$checksumCount = 0
$checksumProblems = @()
foreach ($checksumLine in [IO.File]::ReadAllLines($checksumManifest)) {
    if ([string]::IsNullOrWhiteSpace($checksumLine)) { continue }
    if ($checksumLine -notmatch '^([0-9A-Fa-f]{64})  ([^\r\n]+)$') { throw 'Invalid SHA256SUMS.txt record.' }
    $checksumExpected = $Matches[1]
    $checksumRelative = $Matches[2]
    if ($checksumRelative.Contains('\') -or $checksumRelative.Contains('//') -or $checksumRelative.Contains(':') -or [IO.Path]::IsPathRooted($checksumRelative) -or
        $checksumRelative -match '(^|/)(\.|\.\.|\.git)(/|$)' -or -not $checksumSeen.Add($checksumRelative)) {
        throw ('Unsafe or duplicate checksum path: ' + $checksumRelative)
    }
    $checksumPath = [IO.Path]::GetFullPath((Join-Path $checksumRoot $checksumRelative))
    if (-not $checksumPath.StartsWith($checksumRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Checksum path escaped the selected folder.' }
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { $checksumProblems += ('Missing: ' + $checksumRelative); continue }
    $checksumItem = Get-Item -LiteralPath $checksumPath
    for ($checksumWalk = $checksumItem; $null -ne $checksumWalk -and $checksumWalk.FullName -ne $checksumRoot; $checksumWalk = $(if ($checksumWalk -is [IO.FileInfo]) { $checksumWalk.Directory } else { $checksumWalk.Parent })) {
        if ($checksumWalk.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Reparse points are not accepted: ' + $checksumRelative) }
    }
    if ((Get-FileHash -LiteralPath $checksumPath -Algorithm SHA256).Hash -ine $checksumExpected) { $checksumProblems += ('Changed: ' + $checksumRelative) }
    $checksumCount++
}
if ($checksumSeen.Count -eq 0) { throw 'The checksum manifest is empty.' }
if ($checksumProblems.Count) { throw ($checksumProblems -join [Environment]::NewLine) }
Write-Output ('PASS: {0} files match SHA-256 checksums.' -f $checksumCount)
Write-Output 'Checksums detect changed files; they are not a publisher signature.'
