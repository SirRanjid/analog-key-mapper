#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
# These three files contain only text/packet codecs and heartbeat state.
# No ManagedMonitorSource, MonitorStreamHost, ReaderSession, active HID code,
# trust verifier, executable loader, native call or process start is included.
Add-Type -Path @(
    (Join-Path $workspace 'src\App\MonitorWire.cs'),
    (Join-Path $workspace 'src\MonitorHostProtocol.cs'),
    (Join-Path $workspace 'src\Tk75TravelReport.cs')
)
$script:wireAssertions = 0
function Assert-Wire([bool]$condition, [string]$message) {
    $script:wireAssertions++
    if (-not $condition) { throw ('FAIL MonitorWire: ' + $message) }
}
function Assert-WireRejected([scriptblock]$action, [string]$message, [type]$exceptionType = [IO.InvalidDataException]) {
    $rejected = $false
    try { & $action | Out-Null }
    catch {
        $reason = $_.Exception
        while ($null -ne $reason.InnerException) { $reason = $reason.InnerException }
        # InvalidDataException may wrap the original FormatException; match any
        # wrapper as well, rather than relying on PowerShell invocation wrapping.
        $reason = $_.Exception
        while ($null -ne $reason) {
            if ($exceptionType.IsAssignableFrom($reason.GetType())) { $rejected = $true; break }
            $reason = $reason.InnerException
        }
        if (-not $rejected) { throw }
    }
    Assert-Wire $rejected $message
}
function Read-Wire([string]$text, [int]$maximum = 4096) {
    $reader = [IO.StringReader]::new($text)
    try { [Tk75.App.MonitorWire]::ReadLine($reader, $maximum) }
    finally { $reader.Dispose() }
}

Assert-Wire ([Tk75.App.MonitorWire]::DecodeReady('READY 3590') -eq 3590) 'ANSI identity decoded.'
Assert-Wire ([Tk75.App.MonitorWire]::DecodeReady('READY 3591') -eq 3591) 'ISO identity decoded.'
foreach ($bad in @($null, '', 'READY', 'READY ', 'READY 3592', 'READY 0', 'READY 4294967295', 'READY 4294967296', 'READY -3591', 'READY +3591', 'READY 03591', 'READY 3591 ', ' READY 3591', 'ready 3591', "READY 3591`r", "READY 3591`n", 'READY ３５９１')) {
    Assert-WireRejected { [Tk75.App.MonitorWire]::DecodeReady($bad) } 'Only exact supported identity lines accepted.'
}
$lineReader = [IO.StringReader]::new("READY 3591`r`nSTOPPED`n")
try {
    Assert-Wire ([Tk75.App.MonitorWire]::ReadLine($lineReader,4096) -ceq 'READY 3591') 'One CRLF accepted.'
    Assert-Wire ([Tk75.App.MonitorWire]::ReadLine($lineReader,4096) -ceq 'STOPPED') 'LF accepted without buffering loss.'
    Assert-Wire ($null -eq [Tk75.App.MonitorWire]::ReadLine($lineReader,4096)) 'Clean EOF distinct from an empty line.'
} finally { $lineReader.Dispose() }
Assert-Wire ((Read-Wire (('x' * 4096) + "`n")).Length -eq 4096) 'Bounded LF line accepts exact maximum.'
Assert-Wire ((Read-Wire "`n") -ceq '') 'Empty framed line preserved for higher-level rejection.'
foreach ($bad in @('READY 3591', "READY 3591`r", "READY 3591`r`r`n", "READY`r 3591`n", "READY`t3591`n", "READY 3591`0`n", (('x' * 4097) + "`n"))) {
    Assert-WireRejected { Read-Wire $bad } 'Partial, embedded-control and oversized lines rejected.'
}
Assert-WireRejected { Read-Wire "x`n" 0 } 'Zero maximum rejected.' ([ArgumentOutOfRangeException])
Assert-WireRejected { Read-Wire "x`n" 4097 } 'Unbounded maximum rejected.' ([ArgumentOutOfRangeException])
Assert-WireRejected { [Tk75.App.MonitorWire]::ReadLine($null,4096) } 'Null reader rejected.' ([ArgumentNullException])

$frame = [byte[]]::new(32)
$frame[0] = 5; $frame[1] = 27; $frame[2] = 129; $frame[3] = 1; $frame[4] = 14
$encoded = [Tk75.Diagnostics.MonitorHostProtocol]::DataLine($frame)
$decoded = [Tk75.App.MonitorWire]::DecodeData($encoded)
Assert-Wire (($decoded -join ',') -ceq ($frame -join ',')) 'Child encoder and parent decoder preserve raw pressure bytes.'
$sample = [Tk75.Diagnostics.TravelSample]::new(); $decodeError = $null
Assert-Wire ([Tk75.Diagnostics.Tk75TravelReport]::TryParse($decoded,[ref]$sample,[ref]$decodeError)) 'Decoded wire bytes remain a valid pressure report.'
Assert-Wire ($sample.KeyIndex -eq 14 -and $sample.RawValue -eq 385) 'No depth scaling or key identity changes in transport.'
$random = [Random]::new(73591)
for ($case = 0; $case -lt 400; $case++) {
    $packet = [byte[]]::new(32); $random.NextBytes($packet)
    $roundtrip = [Tk75.App.MonitorWire]::DecodeData([Tk75.Diagnostics.MonitorHostProtocol]::DataLine($packet))
    Assert-Wire (($roundtrip -join ',') -ceq ($packet -join ',')) 'Every raw byte survives transport independently of report semantics.'
}
for ($position = 5; $position -lt 69; $position++) {
    $characters = $encoded.ToCharArray(); $characters[$position] = 'G'
    $bad = -join $characters
    Assert-WireRejected { [Tk75.App.MonitorWire]::DecodeData($bad) } 'Nonhex character rejected at every nibble position.'
}
foreach ($bad in @($null, '', ('DATA ' + ('00' * 31)), ('DATA ' + ('00' * 33)), ('data ' + ('00' * 32)), ($encoded + ' '), (' ' + $encoded))) {
    Assert-WireRejected { [Tk75.App.MonitorWire]::DecodeData($bad) } 'Malformed report framing rejected.'
}
$corrupt = [byte[]]$frame.Clone(); $corrupt[31] = 1
$wireCorrupt = [Tk75.App.MonitorWire]::DecodeData([Tk75.Diagnostics.MonitorHostProtocol]::DataLine($corrupt))
Assert-Wire (-not [Tk75.Diagnostics.Tk75TravelReport]::TryParse($wireCorrupt,[ref]$sample,[ref]$decodeError)) 'Transport preserves invalid report bytes so selected pressure parser can reject them.'

foreach ($message in @('Tastatur getrennt.', 'Übertragung fehlgeschlagen – neu verbinden.', '键盘', ('ä' * 1000))) {
    Assert-Wire ([Tk75.App.MonitorWire]::DecodeError([Tk75.Diagnostics.MonitorHostProtocol]::ErrorLine($message)) -ceq $message) 'UTF-8 error line roundtrip within bound.'
}
Assert-Wire ([Tk75.App.MonitorWire]::DecodeError([Tk75.Diagnostics.MonitorHostProtocol]::ErrorLine(('x' * 1500))).Length -eq 1000) 'Helper truncation matches parent bound.'
foreach ($bad in @($null, '', 'ERROR ', 'ERROR ???', 'error eA==', 'ERROR eA== ', 'ERROR eB==', 'ERROR /w==', 'ERROR wK8=', 'ERROR 7aCA', 'ERROR AA==', 'ERROR IA==', ('ERROR ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(('x' * 1001)))), ('ERROR ' + ('a' * 4097)))) {
    Assert-WireRejected { [Tk75.App.MonitorWire]::DecodeError($bad) } 'Noncanonical Base64, malformed UTF-8, NUL, blank and oversized messages rejected.'
}

# Commands and leases are deterministic state tests; these objects do not own a
# timer, thread, process, pipe or device.
$path = '\\?\hid#vid_3151&pid_5030#synthetic#only-for-pure-tests'
$start = 'START ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($path))
$state = [Tk75.Diagnostics.MonitorHostProtocol]::new()
$state.Receive($start,0); $state.Receive('PING',250); $state.CheckHeartbeat(2249)
Assert-Wire (-not $state.Stopped) 'Heartbeat valid just before deadline.'
$state.CheckHeartbeat(2250)
Assert-Wire ($state.Stopped -and $null -ne $state.Error) 'Exact expired deadline stops.'
$state.Receive('PING',2251)
Assert-Wire $state.Stopped 'Late PING cannot revive a stopped lease.'
foreach ($terminal in @('STOP',$null)) {
    $state = [Tk75.Diagnostics.MonitorHostProtocol]::new(); $state.Receive($start,0)
    if ($null -eq $terminal) { $state.Receive([System.Management.Automation.Language.NullString]::Value,100) }
    else { $state.Receive($terminal,100) }
    Assert-Wire ($state.Stopped -and $null -eq $state.Error) 'STOP and EOF request clean termination.'
}
$state = [Tk75.Diagnostics.MonitorHostProtocol]::new(); $state.CheckHeartbeat(2000)
Assert-Wire ($state.Stopped -and $null -ne $state.Error) 'A missing START has a finite lease.'
Write-Output ('PASS MonitorWire: ' + $script:wireAssertions + ' pure codec/state assertions; no executable, HID or signature operation.')
