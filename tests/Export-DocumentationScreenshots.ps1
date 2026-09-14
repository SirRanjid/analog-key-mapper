#requires -Version 5.1
<#
Renders the real application in its passive preview mode, using only a new
synthetic workspace. Never starts the product app, monitor or a controller.
#>
[CmdletBinding()]
param(
    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $OutputDirectory = '',
    [string] $ArtifactDirectory = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Documentation screenshots require Windows.' }
$workspace = [IO.Path]::GetFullPath($SourceRoot)
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $workspace ('build\documentation-screenshots-' + [Guid]::NewGuid().ToString('N')) }
$artifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
[void][System.IO.Directory]::CreateDirectory($artifactDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler unavailable.' }
$executable = Join-Path $artifactDirectory 'DocumentationScreenshots.exe'
$shared = @('HidNative.cs','Tk75TravelReport.cs','Tk75RgbProtocol.cs','Tk75RgbExchange.cs','TravelProtocols.cs','RawLiveView.cs','KeyMap.cs','KeyLearner.cs') |
    ForEach-Object { Join-Path (Join-Path $workspace 'src') $_ }
$appSources = Get-ChildItem -LiteralPath (Join-Path $workspace 'src\App'), (Join-Path $workspace 'src\Core'), (Join-Path $workspace 'src\Output') -Filter '*.cs' -File |
    Sort-Object FullName | ForEach-Object FullName
$arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/optimize+','/warnaserror+','/main:Tk75.Tests.PresentationEntry',
    '/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    "/win32manifest:$workspace\src\App\app.manifest", "/win32icon:$workspace\src\App\Assets\AnalogKeyMapper.ico", "/out:$executable")
$arguments += $shared
$arguments += $appSources
$arguments += (Join-Path $PSScriptRoot 'DocumentationScreenshots.cs')
$arguments += (Join-Path $PSScriptRoot 'PresentationEntry.cs')
foreach ($harness in @('AppUiHarness','AppDialogHarness','KeyboardGestureUiHarness','ControllerModifierUiHarness',
    'MappingSummaryUiHarness','SocdDragUiHarness','PressureRangeSliderUiHarness','ThemeControlsUiHarness',
    'KeyAnnotationUiHarness','CurveSettingsSliderUiHarness','CurveShapePickerUiHarness','CurveDynamicsUiHarness',
    'CurveRangeRailUiHarness','SliderPrecisionUiHarness','InputThresholdUiHarness','InputThresholdCaptureUiHarness',
    'LearnInputsUiHarness','PresentationUiHarness')) {
    $arguments += (Join-Path $workspace ('tests\' + $harness + '.cs'))
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Screenshot renderer compilation failed.' }
$stdout = Join-Path $artifactDirectory 'stdout.txt'; $stderr = Join-Path $artifactDirectory 'stderr.txt'
$data = Join-Path $artifactDirectory 'synthetic-data'
$images = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'docs\images' }
$process = $null
try {
    $process = Start-Process -FilePath $executable -ArgumentList ('"' + $data + '" "' + $images + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(30000)) { throw 'Documentation rendering timed out.' }
    $process.Refresh()
    [Console]::Write([System.IO.File]::ReadAllText($stdout))
    [Console]::Error.Write([System.IO.File]::ReadAllText($stderr))
    if ($process.ExitCode -ne 0) { throw ('Documentation rendering failed. See ' + $artifactDirectory) }
    Write-Output ('Images: ' + $images)
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
