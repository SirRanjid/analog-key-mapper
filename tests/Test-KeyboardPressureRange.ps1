#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskDirectory = Join-Path $PSScriptRoot ('tmp/pressure-range-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskDirectory)
$taskBinary = Join-Path $taskDirectory 'PressureRangeTests.exe'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /langversion:5 /warnaserror+ /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll "/out:$taskBinary" `
    "$taskWorkspace\src\HidNative.cs" "$taskWorkspace\src\Core\MappingModels.cs" "$taskWorkspace\src\Core\MappingValidation.cs" `
    "$taskWorkspace\src\Core\DefaultCalibration.cs" "$taskWorkspace\src\Core\PressureRangeCapture.cs" `
    "$taskWorkspace\src\App\WorkspaceStore.cs" "$taskWorkspace\src\App\KeyboardPressureRange.cs" "$PSScriptRoot\KeyboardPressureRangeHarness.cs"
if ($LASTEXITCODE -ne 0) { throw 'Pressure range test compilation failed.' }
& $taskBinary $taskDirectory
if ($LASTEXITCODE -ne 0) { throw 'Pressure range tests failed.' }
