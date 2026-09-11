#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$testParent = Join-Path $PSScriptRoot 'tmp'
$testRoot = Join-Path $testParent ('workspace-store-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    # Compile/run a normal .NET Framework process: System.Web.Extensions is not
    # loaded into PowerShell 7. The harness invokes only metadata/file operations.
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $binary = Join-Path $testRoot 'WorkspaceStoreTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /warnaserror+ /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll "/out:$binary" `
        (Join-Path $workspace 'src\HidNative.cs') (Join-Path $workspace 'src\Core\MappingModels.cs') (Join-Path $workspace 'src\Core\MappingValidation.cs') `
        (Join-Path $workspace 'src\App\WorkspaceStore.cs') (Join-Path $PSScriptRoot 'WorkspaceStoreHarness.cs')
    if ($LASTEXITCODE -ne 0) { throw 'WorkspaceStore harness compile failed.' }
    & $binary $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'WorkspaceStore harness failed.' }
}
finally {
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    $resolvedParent = [IO.Path]::GetFullPath($testParent).TrimEnd('\') + '\'
    if ($resolvedTest.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedTest).StartsWith('workspace-store-')) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
