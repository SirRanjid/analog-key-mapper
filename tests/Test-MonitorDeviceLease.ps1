# Pure monotonic lease checks. No HID, process, file loading or native calls.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\src\App\MonitorWire.cs')
$script:checks = 0
function Check([bool]$value, [string]$message) { $script:checks++; if (-not $value) { throw $message } }
function Rejected([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    Check $rejected $message
}
$lease = [Tk75.App.MonitorDeviceLease]::new()
$lease.Start(0)
Check ($lease.Status(0) -eq 0) 'No device proof at startup.'
$lease.AcceptProof('LIVE 1 1200', 10)
Check ($lease.Status(509) -eq 1) 'Held state stays live within device lease.'
Check ($lease.Status(510) -eq 2) 'Exactly 500ms device silence expires.'
Rejected { $lease.AcceptProof('LIVE 2 1700', 510) } 'Late proof must never revive held state.'
Rejected { $lease.AcceptValidData(511) } 'Late DATA must never revive held state.'
Check ($lease.Status(520) -eq 2) 'Expiry is permanent.'
foreach ($line in @('LIVE 0 1','LIVE 2 1','LIVE 01 1','LIVE 1 +1','LIVE 1 -1','LIVE 1 01','LIVE 1 1 ',' LIVE 1 1','LIVE 1','LIVE 1 1 1')) {
    $item = [Tk75.App.MonitorDeviceLease]::new(); $item.Start(0)
    Rejected { $item.AcceptProof($line, 1) } 'Strict proof grammar and sequence.'
}
$data = [Tk75.App.MonitorDeviceLease]::new(); $data.Start(0)
Rejected { $data.AcceptValidData(1) } 'DATA cannot establish event protocol without initial device proof.'
$data.AcceptProof('LIVE 1 0', 1)
for ($i = 1; $i -le 20; $i++) {
    $data.AcceptValidData($i * 150)
    $data.AcceptProof(('LIVE ' + ($i + 1) + ' ' + ($i * 150)), $i * 150 + 1)
    Check ($data.Status($i * 150 + 149) -eq 1) 'Real evidence supports indefinitely held key without synthetic samples.'
}
Rejected { $data.AcceptProof('LIVE 21 3000', 3151) } 'Duplicate proof rejected.'
Rejected { $data.AcceptProof('LIVE 22 2999', 3151) } 'Backward helper time rejected.'
$queue = [Tk75.App.MonitorDeviceLease]::new(); $queue.Start(0); $queue.AcceptProof('LIVE 1 0',0)
$queue.AcceptValidData(400); $queue.AcceptProof('LIVE 2 100',400)
$queue.AcceptValidData(600)
Rejected { $queue.AcceptProof('LIVE 3 100',600) } 'Queued stale proof cannot keep stale DATA alive.'
Check ($queue.Status(601) -eq 2) 'Stale queued proof permanently expires.'
$back = [Tk75.App.MonitorDeviceLease]::new(); $back.Start(10); $back.AcceptProof('LIVE 1 0',10)
Check ($back.Status(9) -eq 2) 'Backward parent clock invalidates evidence.'
Write-Output ('PASS: ' + $script:checks + ' device lease checks; pure state only.')
