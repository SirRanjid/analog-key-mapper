#requires -Version 5.1
[CmdletBinding()]
param([switch] $KeepArtifacts)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$dialogRoot = Split-Path -Parent $PSScriptRoot
$dialogFolder = Join-Path $PSScriptRoot ('synthetic-rgb-dialog-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dialogFolder)
$dialogProcess = $null; $dialogSuccess = $false
try {
    $dialogCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $dialogExecutable = Join-Path $dialogFolder 'RgbStartupDialogHarness.exe'
    $dialogShared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
        ForEach-Object { Join-Path (Join-Path $dialogRoot 'src') $_ }
    $dialogSources = Get-ChildItem -LiteralPath (Join-Path $dialogRoot 'src\App'), (Join-Path $dialogRoot 'src\Core'), (Join-Path $dialogRoot 'src\Output') -Filter '*.cs' -File | ForEach-Object FullName
    & $dialogCompiler /nologo /target:exe /platform:x64 /langversion:5 /warnaserror+ /main:RgbStartupDialogHarness /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll "/out:$dialogExecutable" $dialogSources $dialogShared (Join-Path $PSScriptRoot 'RgbStartupDialogHarness.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Startup dialog harness compilation failed.' }
    $dialogStdout = Join-Path $dialogFolder 'stdout.txt'; $dialogStderr = Join-Path $dialogFolder 'stderr.txt'
    # Only synthetic offscreen dialogs; no application startup or device access.
    $dialogProcess = Start-Process -FilePath $dialogExecutable -ArgumentList ('"' + $dialogFolder + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $dialogStdout -RedirectStandardError $dialogStderr
    if (-not $dialogProcess.WaitForExit(30000)) { $dialogProcess.Kill(); $dialogProcess.WaitForExit(); throw 'Startup dialog tests exceeded 30 seconds.' }
    $dialogProcess.Refresh()
    if (Test-Path -LiteralPath $dialogStdout) { [Console]::Write([IO.File]::ReadAllText($dialogStdout)) }
    if (Test-Path -LiteralPath $dialogStderr) { [Console]::Error.Write([IO.File]::ReadAllText($dialogStderr)) }
    if ($dialogProcess.ExitCode -ne 0) { throw ('Startup dialog tests failed. Artifacts: ' + $dialogFolder) }
    $dialogSuccess = $true
    if ($KeepArtifacts) { Write-Output ('Artifacts: ' + $dialogFolder) }
}
finally {
    if ($null -ne $dialogProcess) {
        if (-not $dialogProcess.HasExited) { $dialogProcess.Kill(); $dialogProcess.WaitForExit() }
        $dialogProcess.Dispose()
    }
    if ($dialogSuccess -and -not $KeepArtifacts) {
        $dialogResolved = [IO.Path]::GetFullPath($dialogFolder)
        $dialogParent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
        if (-not $dialogResolved.StartsWith($dialogParent, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($dialogResolved).StartsWith('synthetic-rgb-dialog-')) { throw 'Refusing cleanup outside the dedicated test directory.' }
        Remove-Item -LiteralPath $dialogResolved -Recurse -Force
    }
}
