#requires -Version 5.1
[CmdletBinding()]
param([switch] $KeepArtifacts, [switch] $CompileOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The pending controller harness requires Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
$testFolder = Join-Path $PSScriptRoot ('synthetic-controller-pending-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testFolder)
$testProcess = $null; $success = $false
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $executable = Join-Path $testFolder 'ControllerPendingUiHarness.exe'
    $shared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
        ForEach-Object { Join-Path (Join-Path $workspace 'src') $_ }
    $appSources = Get-ChildItem -LiteralPath (Join-Path $workspace 'src\App'), (Join-Path $workspace 'src\Core'), (Join-Path $workspace 'src\Output') -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName
    $arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/optimize+','/warnaserror+','/main:Tk75.Tests.ControllerPendingUiHarness',
        '/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
        "/win32manifest:$workspace\src\App\app.manifest", "/out:$executable")
    $arguments += $shared; $arguments += $appSources; $arguments += (Join-Path $PSScriptRoot 'ControllerPendingUiHarness.cs')
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Pending controller harness compilation failed.' }
    if ($CompileOnly) { Write-Output ('Compiled without execution: ' + $executable); return }
    $stdout = Join-Path $testFolder 'stdout.txt'; $stderr = Join-Path $testFolder 'stderr.txt'
    # This synthetic entry point uses preview mode and fake controller sessions.
    # Stop on a Windows application-control rejection; no alternate launch path.
    $testProcess = Start-Process -FilePath $executable -ArgumentList ('"' + $testFolder + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $testProcess.WaitForExit(30000)) {
        $testProcess.Kill(); $testProcess.WaitForExit()
        throw ('Pending controller harness exceeded 30 seconds. Artifacts: ' + $testFolder)
    }
    $testProcess.Refresh()
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([System.IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([System.IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('Pending controller harness failed. Artifacts: ' + $testFolder) }
    $success = $true
    if ($KeepArtifacts) { Write-Output ('Artifacts: ' + $testFolder) }
}
finally {
    if ($null -ne $testProcess) {
        if (-not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit() }
        $testProcess.Dispose()
    }
    if ($success -and -not $KeepArtifacts) {
        $resolved = [System.IO.Path]::GetFullPath($testFolder)
        $parent = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolved).StartsWith('synthetic-controller-pending-')) { throw 'Refusing cleanup outside the dedicated test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
