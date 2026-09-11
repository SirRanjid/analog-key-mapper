#requires -Version 5.1
[CmdletBinding()]
param([switch] $ProtocolOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
# Pure packet/state only. No native calls, device helper or HID code is loaded.
Add-Type -Path (Join-Path $taskWorkspace 'src\Tk75RgbProtocol.cs'), (Join-Path $PSScriptRoot 'Tk75RgbProtocolHarness.cs')
$taskChecks = [Tk75RgbProtocolHarness]::Run()
foreach ($taskModel in $(if ($ProtocolOnly) { @() } else { @(3590,3591) })) {
    $taskFixtures = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures\rgb-supported-key-indices.json') -Raw | ConvertFrom-Json)
    $taskFixture = @($taskFixtures | Where-Object { $_.Model -eq $taskModel })
    if ($taskFixture.Count -ne 1) { throw 'Pinned manufacturer key-index fixture missing or duplicated.' }
    $taskExpected = @($taskFixture[0].SupportedKeyIndices | ForEach-Object { [int]$_ })
    $taskActual = [Tk75.Diagnostics.Tk75RgbProtocol]::GetSupportedKeyIndices($taskModel)
    if (($taskExpected -join ',') -cne ($taskActual -join ',')) { throw "RGB positions differ from pinned manufacturer model $taskModel." }
    $taskChecks++
}
Write-Output "PASS: $taskChecks pure RGB protocol checks; pinned manufacturer matrix comparison: $(-not $ProtocolOnly); no HID, device writes or monitor executable."
