#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$testHostPath = (Get-Process -Id $PID).Path
$testSuites = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter 'Test-*.ps1' -File | Sort-Object Name
$testFailed = @()
foreach ($testSuite in $testSuites) {
    Write-Host $testSuite.Name
    & $testHostPath -NoProfile -File $testSuite.FullName
    if ($LASTEXITCODE -ne 0) { $testFailed += $testSuite.Name }
}
if ($testFailed.Count) { throw ('Failed suites: ' + ($testFailed -join ', ')) }
Write-Host ('PASS: {0} offline suites. Not a hardware, driver or game acceptance test.' -f $testSuites.Count)
