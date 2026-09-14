[CmdletBinding()]
param(
    [string] $OutputDirectory = 'bin',
    [switch] $AllowUnsignedMonitor
)
$ErrorActionPreference = 'Stop'
$releaseMetadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'RELEASE_VERSION.json') -Raw | ConvertFrom-Json
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x (64-bit) compiler not found.' }
$applicationIcon = Join-Path $PSScriptRoot 'src\App\Assets\AnalogKeyMapper.ico'
if (-not (Test-Path -LiteralPath $applicationIcon -PathType Leaf)) { throw 'Application icon source is missing.' }
$target = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$taskBuildRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if (-not $target.StartsWith($taskBuildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside this repository.' }
foreach ($taskRunningApp in @(Get-Process AnalogKeyMapper,Tk75Monitor,Tk75Diag -ErrorAction SilentlyContinue)) {
    if ($taskRunningApp.Path -and [System.IO.Path]::GetDirectoryName($taskRunningApp.Path) -eq $target) { throw 'An application in the output folder is running. Close it or choose another OutputDirectory.' }
}
New-Item -ItemType Directory -Force -Path $target | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'captures') | Out-Null
$shared = @('src\HidNative.cs', 'src\Tk75TravelReport.cs', 'src\TravelProtocols.cs', 'src\RawLiveView.cs', 'src\KeyMap.cs', 'src\KeyLearner.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll "/out:$target\Tk75Diag.exe" $shared (Join-Path $PSScriptRoot 'src\Program.cs') (Join-Path $PSScriptRoot 'src\DiagAssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Web.Extensions.dll "/out:$target\Tk75Monitor.exe" (Join-Path $PSScriptRoot 'src\HidNative.cs') (Join-Path $PSScriptRoot 'src\Tk75TravelReport.cs') (Join-Path $PSScriptRoot 'src\Tk75InputEvent.cs') (Join-Path $PSScriptRoot 'src\MonitorProtocol.cs') (Join-Path $PSScriptRoot 'src\Tk75RgbProtocol.cs') (Join-Path $PSScriptRoot 'src\Tk75RgbExchange.cs') (Join-Path $PSScriptRoot 'src\MonitorHostProtocol.cs') (Join-Path $PSScriptRoot 'src\RgbInputSettling.cs') (Join-Path $PSScriptRoot 'src\MonitorShutdownOrder.cs') (Join-Path $PSScriptRoot 'src\MonitorStreamHost.cs') (Join-Path $PSScriptRoot 'src\TravelMonitor.cs') (Join-Path $PSScriptRoot 'src\MonitorAssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Monitor build failed.' }
Write-Host "Built: $target\Tk75Diag.exe"
Write-Host "Built: $target\Tk75Monitor.exe"
$appSources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\App'), (Join-Path $PSScriptRoot 'src\Core'), (Join-Path $PSScriptRoot 'src\Output') -Filter '*.cs' -File | ForEach-Object FullName
$appBuildOptions = @()
if ($AllowUnsignedMonitor) { $appBuildOptions += '/define:ALLOW_UNSIGNED_MONITOR' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/win32manifest:$PSScriptRoot\src\App\app.manifest" "/win32icon:$applicationIcon" "/out:$target\AnalogKeyMapper.exe" @appBuildOptions $shared (Join-Path $PSScriptRoot 'src\Tk75RgbProtocol.cs') (Join-Path $PSScriptRoot 'src\Tk75RgbExchange.cs') $appSources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$cursorLicenseDirectory = Join-Path $target 'licenses\chromium-cursors'
New-Item -ItemType Directory -Force -Path $cursorLicenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\App\Assets\Cursors\LICENSE') -Destination (Join-Path $cursorLicenseDirectory 'LICENSE') -Force
Write-Host "Built: $target\AnalogKeyMapper.exe"
foreach ($releaseExecutable in @('AnalogKeyMapper.exe','Tk75Monitor.exe','Tk75Diag.exe')) {
    $releaseFileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $target $releaseExecutable))
    if ($releaseFileInfo.FileVersion -ne $releaseMetadata.fileVersion -or $releaseFileInfo.ProductVersion -ne $releaseMetadata.version) {
        throw ('Executable metadata disagrees with RELEASE_VERSION.json: ' + $releaseExecutable)
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RELEASE_VERSION.json') -Destination (Join-Path $target 'RELEASE_VERSION.json') -Force
Write-Host ('Verified build version: ' + $releaseMetadata.version)
if ($AllowUnsignedMonitor) { Write-Host 'Local build: unsigned keyboard helper allowed; Windows still checks execution.' }
