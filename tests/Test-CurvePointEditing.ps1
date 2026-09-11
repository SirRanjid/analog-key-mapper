$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# New pure geometry/gesture state only. No WinForms, HID, native output,
# rendering, or assertions imported from previously blocked suites.
Add-Type -Path @(
    (Join-Path $taskWorkspace 'src\Core\MappingModels.cs'),
    (Join-Path $taskWorkspace 'src\Core\BezierCurve.cs'),
    (Join-Path $taskWorkspace 'src\Core\CurvePointEditing.cs'),
    (Join-Path $PSScriptRoot 'CurvePointEditingHarness.cs')
)
$taskChecks = [CurvePointEditingHarness]::Run()
Write-Output "Curve point geometry and gesture checks: $taskChecks PASS"
