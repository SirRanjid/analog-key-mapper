$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# New pure metadata policy only: no WinForms/HID/Reader/native/helper imports,
# no copied assertions from previously blocked UI or native suites.
Add-Type -Path @(
    (Join-Path $taskWorkspace 'src\App\DeviceDiscoveryPolicy.cs'),
    (Join-Path $PSScriptRoot 'DeviceDiscoveryPolicyHarness.cs')
)
$taskChecks = [DeviceDiscoveryPolicyHarness]::Run()
Write-Output "Device discovery metadata checks: $taskChecks PASS"
