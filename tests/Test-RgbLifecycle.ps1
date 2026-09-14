#requires -Version 5.1
[CmdletBinding()]
param([switch] $KeepArtifacts)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The RGB lifecycle harness requires Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
# Keep execution in the ordinary test folder. Journal data has an independent
# temporary root so a nested checkout does not consume the product's MAX_PATH
# budget before the lifecycle scenario can reach its fake transport.
$testFolder = Join-Path $PSScriptRoot ('rlc-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$dataFolder = Join-Path ([IO.Path]::GetTempPath()) ('akm-rgb-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
[void][System.IO.Directory]::CreateDirectory($testFolder)
[void][System.IO.Directory]::CreateDirectory($dataFolder)
[IO.File]::WriteAllText((Join-Path $testFolder 'data-location.txt'), $dataFolder)
$testProcess = $null; $success = $false
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $executable = Join-Path $testFolder 'RgbLifecycleHarness.exe'
    $shared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
        ForEach-Object { Join-Path (Join-Path $workspace 'src') $_ }
    $appSources = Get-ChildItem -LiteralPath (Join-Path $workspace 'src\App'), (Join-Path $workspace 'src\Core'), (Join-Path $workspace 'src\Output') -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName
    $arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/optimize+','/warnaserror+','/main:Tk75.Tests.RgbLifecycleHarness',
        '/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
        "/win32manifest:$workspace\src\App\app.manifest", "/out:$executable")
    $arguments += $shared; $arguments += $appSources; $arguments += (Join-Path $PSScriptRoot 'RgbLifecycleHarness.cs')
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'RGB lifecycle harness compilation failed.' }
    $stdout = Join-Path $testFolder 'stdout.txt'; $stderr = Join-Path $testFolder 'stderr.txt'
    # Only this synthetic harness; never start the production app or monitor.
    # If Windows refuses the executable, stop without an alternate launch path.
    Write-Output ('RGB test data: ' + $dataFolder)
    $testProcess = Start-Process -FilePath $executable -ArgumentList ('"' + $dataFolder + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $testProcess.WaitForExit(60000)) {
        $testProcess.Kill(); $testProcess.WaitForExit()
        throw ('RGB lifecycle harness exceeded 60 seconds. Artifacts: ' + $testFolder)
    }
    $testProcess.Refresh()
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([System.IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([System.IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('RGB lifecycle harness failed. Artifacts: ' + $testFolder) }
    $success = $true
    if ($KeepArtifacts) { Write-Output ('Artifacts: ' + $testFolder) }
}
finally {
    if ($null -ne $testProcess) {
        if (-not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit() }
        $testProcess.Dispose()
    }
    if ($success -and -not $KeepArtifacts) {
        $resolvedData = [System.IO.Path]::GetFullPath($dataFolder)
        $temporaryParent = [System.IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedData.StartsWith($temporaryParent,[StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolvedData).StartsWith('akm-rgb-')) { throw 'Refusing cleanup outside the dedicated RGB data directory.' }
        Remove-Item -LiteralPath $resolvedData -Recurse -Force
        $resolved = [System.IO.Path]::GetFullPath($testFolder)
        $parent = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolved).StartsWith('rlc-')) { throw 'Refusing cleanup outside the dedicated test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
