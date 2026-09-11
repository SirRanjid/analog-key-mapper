$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# Pure curve gesture calculations only. No WinForms/OLE/rendering, HID,
# native output or assertions imported from previously blocked suites.
Add-Type -Path @(
    (Join-Path $taskWorkspace 'src\Core\MappingModels.cs'),
    (Join-Path $taskWorkspace 'src\Core\BezierCurve.cs'),
    (Join-Path $taskWorkspace 'src\Core\CurvePointEditing.cs'),
    (Join-Path $PSScriptRoot 'BezierGestureHarness.cs')
)
$taskChecks = [BezierGestureHarness]::Run()
Write-Output "Bezier gesture checks: $taskChecks PASS"
