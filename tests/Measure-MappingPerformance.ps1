#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1,60)][int] $Seconds = 5,
    [ValidateRange(1,32)][int[]] $Counts = @(1,4,8,16,32),
    [string] $OutputPath
)
$ErrorActionPreference = 'Stop'
$benchmarkRoot = Split-Path -Parent $PSScriptRoot
if ('MappingPerformanceHarness' -as [type]) { throw 'Use a fresh PowerShell process for the benchmark.' }
$benchmarkFiles = @(
    'src/Core/MappingModels.cs', 'src/Core/MappingValidation.cs', 'src/Core/MappingEngine.cs',
    'src/Core/PreviewSnapshot.cs', 'src/Core/PreviewComposer.cs', 'src/Core/BezierCurve.cs',
    'src/Core/ProfileJson.cs', 'src/Core/MappingAssignments.cs', 'src/Core/ControllerRouting.cs',
    'src/Output/ViGEmOutput.cs', 'src/Output/ControllerOutputs.cs', 'src/Output/OutputHost.cs',
    'src/Output/IsolatedOutput.cs', 'src/App/ControllerRelease.cs', 'src/App/MappingSession.cs',
    'tests/MappingPerformanceHarness.cs'
) | ForEach-Object { Join-Path $benchmarkRoot $_ }
$benchmarkCompile = @{Path=$benchmarkFiles}
if ($PSVersionTable.PSEdition -eq 'Desktop') { $benchmarkCompile.ReferencedAssemblies = @('System.Runtime.Serialization.dll','System.Xml.dll','System.Core.dll') }
Add-Type @benchmarkCompile
$benchmarkResults = @()
foreach ($benchmarkCount in $Counts) {
    foreach ($benchmarkPreview in @($false,$true)) {
        $benchmarkResult = [MappingPerformanceHarness]::Run($benchmarkCount,$Seconds * 1000,$true,$benchmarkPreview)
        $benchmarkResults += $benchmarkResult
        Write-Host ('{0} synthetic controllers, preview={1}: CPU {2:N3} core equivalents; input-to-fake-output p95 {3:N3} ms.' -f $benchmarkCount,$benchmarkPreview,$benchmarkResult.CpuCoreEquivalent,$benchmarkResult.LatencyP95Milliseconds)
    }
}
$benchmarkResults += [MappingPerformanceHarness]::Run(32,$Seconds * 1000,$false,$false)
$benchmarkDocument = [ordered]@{
    CreatedUtc=[DateTime]::UtcNow.ToString('o')
    Runtime=[Environment]::Version.ToString()
    LogicalProcessors=[Environment]::ProcessorCount
    Scope='Real mapping workers; synthetic input/output. Excludes keyboard, HID, pipes, helper processes, driver and game. Process memory includes the PowerShell benchmark host. This is not a whole-app or end-to-end latency measurement.'
    Results=$benchmarkResults
}
if ($OutputPath) {
    $benchmarkOutput = [IO.Path]::GetFullPath($OutputPath)
    if (-not $benchmarkOutput.StartsWith([IO.Path]::GetFullPath($benchmarkRoot).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Benchmark output must stay in the repository.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($benchmarkOutput)) | Out-Null
    $benchmarkDocument | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $benchmarkOutput -Encoding utf8
    Write-Output ('Benchmark report: ' + $benchmarkOutput)
} else { $benchmarkDocument | ConvertTo-Json -Depth 5 }
