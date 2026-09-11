#requires -Version 5.1
<#
Kompiliert echte App-Quellen mit eigenem Testeinstieg und startet ausschliesslich
MainForm(..., preview:true). Eigenes Fenster offscreen, keine fremde Anwendung,
keine Hardware und kein Controller. Bei Windows-Blockade kein Umgehungsweg.
#>
[CmdletBinding()]
param([switch] $KeepArtifacts, [ValidateRange(5,120)] [int] $TimeoutSeconds = 40)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Der App-UI-Test benoetigt Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
$testFolder = Join-Path $PSScriptRoot ('synthetic-app-ui-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testFolder)
$testProcess = $null; $success = $false
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET-Framework-Compiler fehlt.' }
    $executable = Join-Path $testFolder 'AppUiHarness.exe'
    $shared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
        ForEach-Object { Join-Path (Join-Path $workspace 'src') $_ }
    $appSources = Get-ChildItem -LiteralPath (Join-Path $workspace 'src\App'), (Join-Path $workspace 'src\Core'), (Join-Path $workspace 'src\Output') -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName
    $arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/optimize+','/warnaserror+','/main:Tk75.Tests.AppUiHarness',
        '/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
        "/win32manifest:$workspace\src\App\app.manifest", "/out:$executable")
    $arguments += $shared
    $arguments += $appSources
    $arguments += (Join-Path $PSScriptRoot 'AppUiHarness.cs')
    $arguments += (Join-Path $PSScriptRoot 'AppDialogHarness.cs')
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'App-UI-Harness konnte nicht kompiliert werden.' }
    $stdout = Join-Path $testFolder 'stdout.txt'; $stderr = Join-Path $testFolder 'stderr.txt'
    # Only this freshly built test executable, never the app/monitor executable.
    $testProcess = Start-Process -FilePath $executable -ArgumentList ('"' + $testFolder + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $testProcess.WaitForExit($TimeoutSeconds * 1000)) {
        $testProcess.Kill(); $testProcess.WaitForExit()
        throw ('UI-Test nach {0} Sekunden beendet. Diagnose: {1}' -f $TimeoutSeconds, $testFolder)
    }
    $testProcess.Refresh()
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([System.IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([System.IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('App-UI-Test fehlgeschlagen (Exit {0}). Diagnose: {1}' -f $testProcess.ExitCode, $testFolder) }
    $success = $true
    if ($KeepArtifacts) { Write-Output ('Artefakte: ' + $testFolder) }
}
finally {
    if ($null -ne $testProcess) {
        if (-not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit() }
        $testProcess.Dispose()
    }
    if ($success -and -not $KeepArtifacts) {
        $resolved = [System.IO.Path]::GetFullPath($testFolder)
        $parent = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolved).StartsWith('synthetic-app-ui-')) {
            throw 'Testbereinigung ausserhalb des dedizierten Testordners verhindert.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
