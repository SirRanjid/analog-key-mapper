$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# Pure mapping metadata only. No OLE, UI, rendering, HID, native output,
# or code/assertions imported from previously blocked suites.
Add-Type -Path @(
    (Join-Path $taskWorkspace 'src\Core\MappingModels.cs'),
    (Join-Path $taskWorkspace 'src\Core\MappingValidation.cs'),
    (Join-Path $taskWorkspace 'src\Core\MappingPairingPolicy.cs'),
    (Join-Path $PSScriptRoot 'MappingPairingPolicyHarness.cs')
)
$taskChecks = [MappingPairingPolicyHarness]::Run()
Write-Output "Mapping partner policy checks: $taskChecks PASS"
