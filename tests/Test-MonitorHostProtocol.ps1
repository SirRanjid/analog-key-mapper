#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
# Pure grammar/state only: do not compile or load MonitorStreamHost,
# TravelMonitor, FeatureNative or any other active HID code into this process.
Add-Type -Path (Join-Path $workspace 'src\MonitorHostProtocol.cs'), (Join-Path $workspace 'src\Tk75RgbProtocol.cs'), (Join-Path $workspace 'src\Tk75RgbExchange.cs')
$script:taskAssertions = 0
function Assert-State([bool]$condition, [string]$message) {
    $script:taskAssertions++
    if (-not $condition) { throw "FAIL: $message" }
}
function Assert-Rejected([scriptblock]$action, [string]$message) {
    $taskRejected = $false
    try { & $action } catch {
        $taskError = $_.Exception
        while ($null -ne $taskError.InnerException) { $taskError = $taskError.InnerException }
        if ($taskError -isnot [IO.InvalidDataException]) { throw }
        $taskRejected = $true
    }
    Assert-State $taskRejected $message
}
$taskPath = '\\?\hid#vid_3151&pid_5030&mi_01&col05#synthetic#{test}'
$taskStart = 'START ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($taskPath))
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 10)
Assert-State ($taskState.DevicePath -ceq $taskPath) 'Exact synthetic device path preserved.'
Assert-State (-not $taskState.Stopped) 'START does not stop.'
$taskState.Receive('PING', 1000)
$taskState.CheckHeartbeat(2999)
Assert-State (-not $taskState.Stopped) 'Heartbeat is valid before exact deadline.'
$taskState.CheckHeartbeat(3000)
Assert-State ($taskState.Stopped -and $null -ne $taskState.Error) 'Exact deadline stops with error.'
$taskState.Receive('PING', 3001)
Assert-State $taskState.Stopped 'PING cannot revive expired session.'

$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive('PING', 2001)
Assert-State ($taskState.Stopped -and $null -ne $taskState.Error) 'Late PING cannot bypass a not-yet-polled deadline.'
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive('STOP', 20)
Assert-State ($taskState.Stopped -and $null -eq $taskState.Error) 'STOP is clean.'
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive([System.Management.Automation.Language.NullString]::Value, 20)
Assert-State ($taskState.Stopped -and $null -eq $taskState.Error) 'EOF requests clean shutdown.'
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.CheckHeartbeat(2000)
Assert-State $taskState.Stopped 'No START has a finite deadline.'

foreach ($taskBadStart in @('PING', 'STOP', '', 'START ?!bad', 'START /w==', 'START ', ('START ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('C:\wrong.exe'))), ($taskStart + ' '))) {
    $taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
    Assert-Rejected { $taskState.Receive($taskBadStart, 0) } "Bad first command rejected: $taskBadStart"
}
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
foreach ($taskBadCommand in @($taskStart, 'PING 1', 'STOP 1', 'SAVE', 'ping', ('x' * 4097))) {
    Assert-Rejected { $taskState.Receive($taskBadCommand, 10) } 'Unknown, duplicate or overlong command rejected.'
}
$taskLineReader = New-Object IO.StringReader("PING`r`nSTOP`n")
try {
    Assert-State ([Tk75.Diagnostics.MonitorHostProtocol]::ReadBoundedLine($taskLineReader) -ceq 'PING') 'CRLF parsed.'
    Assert-State ([Tk75.Diagnostics.MonitorHostProtocol]::ReadBoundedLine($taskLineReader) -ceq 'STOP') 'LF parsed.'
    Assert-State ($null -eq [Tk75.Diagnostics.MonitorHostProtocol]::ReadBoundedLine($taskLineReader)) 'EOF parsed.'
} finally { $taskLineReader.Dispose() }
foreach ($taskBadLine in @('PING', (('x' * 4097) + "`n"))) {
    $taskLineReader = New-Object IO.StringReader($taskBadLine)
    try { Assert-Rejected { [void][Tk75.Diagnostics.MonitorHostProtocol]::ReadBoundedLine($taskLineReader) } 'Truncated or overlong pipe line rejected.' }
    finally { $taskLineReader.Dispose() }
}
[byte[]]$taskReport = 0..31
$taskData = [Tk75.Diagnostics.MonitorHostProtocol]::DataLine($taskReport)
Assert-State ($taskData -ceq 'DATA 000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F') 'Byte-exact fixed-size DATA golden vector.'
foreach ($taskSize in @(0,31,33)) {
    Assert-Rejected { [void][Tk75.Diagnostics.MonitorHostProtocol]::DataLine([byte[]]::new($taskSize)) } 'Wrong report size rejected.'
}
$taskEncodedError = [Tk75.Diagnostics.MonitorHostProtocol]::ErrorLine('Tastatur prüfen')
Assert-State ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($taskEncodedError.Substring(6))) -ceq 'Tastatur prüfen') 'ERROR roundtrips UTF-8.'
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive('RGBREAD 1 4', 10)
$taskRgbRead = $taskState.TakeRgbRead()
Assert-State ($taskRgbRead.RequestId -eq 1 -and $taskRgbRead.Layer -eq 4) 'Typed RGB read preserves request ID and layer.'
Assert-State ($null -eq $taskState.TakeRgbRead()) 'RGB request is taken exactly once.'
Assert-Rejected { $taskState.Receive('RGBREAD 2 0', 20) } 'Active request cannot be overtaken.'
$taskState.Receive('PING', 1000)
$taskState.CompleteRgbRead(1)
$taskState.Receive('RGBREAD 2 0', 1100)
$taskRgbRead = $taskState.TakeRgbRead()
Assert-State ($taskRgbRead.RequestId -eq 2) 'Next request is allowed after completion.'
$taskState.CompleteRgbRead(2)
foreach ($taskBadRgb in @('RGBREAD 0 0','RGBREAD 01 0','RGBREAD +1 0','RGBREAD 1 -1','RGBREAD 1 5','RGBREAD 1 00','RGBREAD 1 0 extra','RGBREAD  1 0','RGBWRITE 1 0','RGBREAD 2147483648 0')) {
    Assert-Rejected { $taskState.Receive($taskBadRgb, 1200) } 'Invalid RGB request or any write command rejected.'
}
$taskState.Receive('RGBREAD 3 0', 1300)
$taskState.Receive('STOP', 1301)
Assert-State ($null -eq $taskState.TakeRgbRead()) 'STOP prevents queued RGB work from starting.'
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive('RGBREAD 1 0', 1999)
$taskState.CheckHeartbeat(2000)
Assert-State $taskState.Stopped 'RGB traffic cannot replace heartbeat.'
$taskRgbSettings = [byte[]]::new(64); $taskRgbSettings[0] = 0x87
$taskRgbSnapshot = [Tk75.Diagnostics.Tk75RgbSnapshot]::new(3591, 2, 4, $taskRgbSettings, [byte[]]::new(384))
$taskRgbEncoded = [Convert]::ToBase64String([Tk75.Diagnostics.Tk75RgbProtocol]::EncodeSnapshot($taskRgbSnapshot))
$taskRgbWriteLine = "RGBWRITE 7 $taskRgbEncoded $taskRgbEncoded"
$taskState = New-Object Tk75.Diagnostics.MonitorHostProtocol
$taskState.Receive($taskStart, 0)
$taskState.Receive($taskRgbWriteLine, 10)
Assert-State ($null -eq $taskState.TakeRgbRead()) 'Write cannot be consumed by read route.'
$taskWrite = $taskState.TakeRgbWrite()
Assert-State ($taskWrite.RequestId -eq 7 -and $taskWrite.Expected.Layer -eq 4 -and $taskWrite.Desired.ModelId -eq 3591) 'Write route requires typed expected and desired snapshots.'
Assert-State ($null -eq $taskState.TakeRgbWrite()) 'Write request dequeues exactly once.'
Assert-Rejected { $taskState.Receive('RGBREAD 8 0', 20) } 'Read cannot overtake active write.'
$taskState.CompleteRgbRead(7)
foreach ($taskBadWrite in @("RGBWRITE 8 $taskRgbEncoded", "RGBWRITE 8 $taskRgbEncoded !", "RGBWRITE 08 $taskRgbEncoded $taskRgbEncoded", "RGBWRITE 8 $taskRgbEncoded $taskRgbEncoded extra")) {
    Assert-Rejected { $taskState.Receive($taskBadWrite, 30) } 'Incomplete or malformed guarded write rejected.'
}
$taskState.Receive($taskRgbWriteLine, 40)
$taskState.Receive('STOP', 41)
Assert-State ($null -eq $taskState.TakeRgbWrite()) 'STOP prevents queued write from starting.'
Write-Output "PASS: $script:taskAssertions assertions; pure protocol/state; no monitor executable or HID code loaded."
