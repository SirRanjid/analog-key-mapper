#requires -Version 5.1
[CmdletBinding()]
param([switch] $CompileOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$testFolder = Join-Path $PSScriptRoot ('synthetic-controller-reconnect-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testFolder)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $testFolder 'ControllerReconnectStoreHarness.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src\Core') -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName)
$sources += (Join-Path $workspace 'src\App\ControllerReconnectSettings.cs'), (Join-Path $workspace 'src\App\ControllerReconnectStore.cs'), (Join-Path $PSScriptRoot 'ControllerReconnectStoreHarness.cs')
& $compiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' '/optimize+' '/warnaserror+' '/r:System.Web.Extensions.dll' '/r:System.Runtime.Serialization.dll' '/r:System.Xml.dll' "/out:$executable" @sources
if ($LASTEXITCODE -ne 0) { throw 'Controller reconnect persistence compilation failed.' }
if ($CompileOnly) { Write-Output ('Compiled without execution: ' + $executable); return }
$stdout = Join-Path $testFolder 'stdout.txt'; $stderr = Join-Path $testFolder 'stderr.txt'
$testProcess = $null
try {
    # Pure in-memory entry point. Stop on application-control rejection;
    # never retry a blocked executable using another host or launch mechanism.
    $testProcess = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $testProcess.WaitForExit(15000)) { $testProcess.Kill(); $testProcess.WaitForExit(); throw 'Controller reconnect persistence checks timed out.' }
    $testProcess.Refresh()
    if (Test-Path -LiteralPath $stdout) { [Console]::Write([IO.File]::ReadAllText($stdout)) }
    if (Test-Path -LiteralPath $stderr) { [Console]::Error.Write([IO.File]::ReadAllText($stderr)) }
    if ($testProcess.ExitCode -ne 0) { throw ('Controller reconnect persistence checks failed: ' + $testFolder) }
    Write-Output ('Artifacts: ' + $testFolder)
}
finally { if ($null -ne $testProcess) { $testProcess.Dispose() } }
