#requires -Version 5.1
[CmdletBinding()]
param([switch]$ProtocolOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if ('IsolatedOutputHarness' -as [type]) { throw 'Bitte eine neue PowerShell-Sitzung fuer die aktuellen Testquellen starten.' }
$sources = @(
    (Join-Path $workspace 'src\Core\MappingModels.cs'),
    (Join-Path $workspace 'src\Output\ViGEmOutput.cs'),
    (Join-Path $workspace 'src\Output\OutputHost.cs'),
    (Join-Path $workspace 'src\Output\IsolatedOutput.cs')
)
$typeArguments = @{Path = @($sources + (Join-Path $PSScriptRoot 'IsolatedOutputHarness.cs'))}
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Core.dll') }
Add-Type @typeArguments
[IsolatedOutputHarness]::RunProtocol()
if (-not $ProtocolOnly) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $target = Join-Path $PSScriptRoot 'tmp\isolation-tests'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $helper = Join-Path $target 'FakeOutputHost.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Runtime.Serialization.dll "/out:$helper" $sources (Join-Path $PSScriptRoot 'FakeOutputHost.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fake-Host-Compile fehlgeschlagen.' }
    # This executable contains only synthetic test behavior at its entry point.
    # It never instantiates the native backend; no protection policy is changed.
    [IsolatedOutputHarness]::RunProcesses($helper)
}
