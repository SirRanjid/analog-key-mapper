#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $PSScriptRoot 'tmp\visual-keyboard'
[void][IO.Directory]::CreateDirectory($artifactDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $artifactDirectory 'VisualKeyboardHarness.exe'
$sources = @('src\App\KeyboardLayout.cs','src\App\KeyboardLayout.Tk75.cs','src\App\VisualKeyboard.cs','src\App\MappingDragVisual.cs','src\App\DragCursors.cs','src\App\UiText.cs','tests\VisualKeyboardHarness.cs') | ForEach-Object { Join-Path $workspace $_ }
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /optimize+ /warnaserror+ /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/win32manifest:$workspace\src\App\app.manifest" "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { throw 'VisualKeyboard-Tests konnten nicht kompiliert werden.' }
# Own offscreen test window only. No hardware, input hooks, controller or other apps.
# A Windows policy block must be reported, never retried through a different loader.
$stdout = Join-Path $artifactDirectory 'stdout.txt'
$stderr = Join-Path $artifactDirectory 'stderr.txt'
$testProcess = Start-Process -FilePath $executable -ArgumentList ('"' + $workspace + '" "' + $artifactDirectory + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    if (-not $testProcess.WaitForExit(30000)) { $testProcess.Kill(); $testProcess.WaitForExit(); throw 'VisualKeyboard-Tests haben das Zeitlimit überschritten.' }
    $testProcess.Refresh()
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('VisualKeyboard-Test fehlgeschlagen: Exit ' + $testProcess.ExitCode) }
}
finally { $testProcess.Dispose() }
