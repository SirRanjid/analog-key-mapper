#requires -Version 5.1
<#
Runs the real application's passive editor with synthetic controller readers.
Checks source ownership across edits, output changes, disconnect and mode switches.
No physical input discovery, output device, hotkey or monitor.
#>
[CmdletBinding()]
param([switch] $KeepArtifacts, [switch] $CompileOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The gamepad UI harness requires Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
$testFolder = Join-Path $PSScriptRoot ('synthetic-gamepad-lifecycle-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testFolder)
$testProcess = $null; $success = $false
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler unavailable.' }
    $executable = Join-Path $testFolder 'GamepadSourceLifecycleUiHarness.exe'
    $shared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
        ForEach-Object { Join-Path (Join-Path $workspace 'src') $_ }
    $appSources = Get-ChildItem -LiteralPath (Join-Path $workspace 'src\App'), (Join-Path $workspace 'src\Core'), (Join-Path $workspace 'src\Output') -Filter '*.cs' -File |
        Sort-Object FullName | ForEach-Object FullName
    $arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/optimize+','/warnaserror+','/main:Tk75.Tests.GamepadSourceLifecycleUiHarness',
        '/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
        "/win32manifest:$workspace\src\App\app.manifest", "/out:$executable")
    $arguments += $shared; $arguments += $appSources; $arguments += (Join-Path $PSScriptRoot 'GamepadSourceLifecycleUiHarness.cs')
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Gamepad source lifecycle harness compilation failed.' }
    if ($CompileOnly) { Write-Output ('Compiled without execution: ' + $executable); return }
    $stdout = Join-Path $testFolder 'stdout.txt'; $stderr = Join-Path $testFolder 'stderr.txt'
    $runArguments = '"' + $testFolder + '"'
    $testProcess = Start-Process -FilePath $executable -ArgumentList $runArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $testProcess.Handle
    if (-not $testProcess.WaitForExit(60000)) {
        $testProcess.Kill(); $testProcess.WaitForExit()
        throw ('Gamepad source lifecycle harness exceeded 60 seconds. Artifacts: ' + $testFolder)
    }
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([System.IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([System.IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('Gamepad source lifecycle harness failed. Artifacts: ' + $testFolder) }
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
        if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolved).StartsWith('synthetic-gamepad-lifecycle-')) {
            throw 'Refusing cleanup outside the dedicated test directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
