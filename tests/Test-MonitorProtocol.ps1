#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot

# Compile only the pure encoder/parser, never the monitor executable or HID interop.
# All replies below are synthetic. These tests do not establish hardware support.
Add-Type -Path (Join-Path $workspace 'src\MonitorProtocol.cs')
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}
function Assert-Rejected([byte[]] $Reply, [string] $Message) {
    foreach ($method in @('DeviceId', 'UsbFirmwareV5Candidate')) {
        $rejected = $false
        try {
            if ($method -eq 'DeviceId') { [void][Tk75.Diagnostics.MonitorProtocol]::DeviceId($Reply) }
            else { [void][Tk75.Diagnostics.MonitorProtocol]::UsbFirmwareV5Candidate($Reply) }
        }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [IO.InvalidDataException]) { throw }
            $rejected = $true
        }
        Assert-Equal $true $rejected ($Message + ': ' + $method)
    }
}
function Hex([byte[]] $Bytes) { return [BitConverter]::ToString($Bytes).Replace('-', ' ') }

# External golden vectors were recorded from the manufacturer source before these tests.
# Compare complete packets, not a checksum recalculated by another copy of the encoder.
$golden = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'fixtures\documented-feature-packets.json') | ConvertFrom-Json
$requests = @{
    identify_and_usb_version = [Tk75.Diagnostics.MonitorProtocol]::IdentifyRequest()
    stream_enable = [Tk75.Diagnostics.MonitorProtocol]::MonitorRequest($true)
    stream_disable = [Tk75.Diagnostics.MonitorProtocol]::MonitorRequest($false)
}
foreach ($name in $requests.Keys) {
    $vector = @($golden | Where-Object { $_.name -ceq $name })
    Assert-Equal 1 $vector.Count ('Exactly one independent golden vector: ' + $name)
    Assert-Equal $vector[0].wireLength $requests[$name].Length ('Windows packet length: ' + $name)
    Assert-Equal $vector[0].hex (Hex $requests[$name]) ('Complete manufacturer packet: ' + $name)
}

# Explicit synthetic byte fixtures pin ID and V5 host candidate parsing rules.
# V5 getUSBVersion uses DataView.getUint16(8) on the payload (default big endian).
# The active V4 configurator uses another layout; none of these tests validates TK75 firmware.
# The surrounding bytes are deliberately nonzero to expose off-by-one parsing.
[byte[]] $ansi = @(0x00, 0x8f, 0x06, 0x0e, 0x00, 0x00, 0xa1, 0xb2, 0xc3, 0x03, 0x01, 0xd4) + (0..52 | ForEach-Object { 0 })
[byte[]] $iso = @(0x00, 0x8f, 0x07, 0x0e, 0x00, 0x00, 0xa1, 0xb2, 0xc3, 0x05, 0x02, 0xd4) + (0..52 | ForEach-Object { 0 })
[byte[]] $foreign = @(0x00, 0x8f, 0x12, 0x34, 0x56, 0x78, 0xa1, 0xb2, 0xc3, 0x12, 0x34, 0xd4) + (0..52 | ForEach-Object { 0 })
Assert-Equal 3590 ([Tk75.Diagnostics.MonitorProtocol]::DeviceId($ansi)) 'Synthetic ANSI model ID'
Assert-Equal 3591 ([Tk75.Diagnostics.MonitorProtocol]::DeviceId($iso)) 'Synthetic ISO model ID'
Assert-Equal 0x78563412 ([Tk75.Diagnostics.MonitorProtocol]::DeviceId($foreign)) 'All four device ID bytes are little endian'
Assert-Equal 0x0301 ([Tk75.Diagnostics.MonitorProtocol]::UsbFirmwareV5Candidate($ansi)) 'V5 host candidate rule: payload8/9 big endian'
Assert-Equal 0x0502 ([Tk75.Diagnostics.MonitorProtocol]::UsbFirmwareV5Candidate($iso)) 'V5 host candidate rule: low byte preserved'
Assert-Equal 0x1234 ([Tk75.Diagnostics.MonitorProtocol]::UsbFirmwareV5Candidate($foreign)) 'V5 host candidate rule: no offset/checksum confusion'
Assert-Equal $true ([Tk75.Diagnostics.MonitorProtocol]::IsSupportedTk75(3590)) 'ANSI model permitted'
Assert-Equal $true ([Tk75.Diagnostics.MonitorProtocol]::IsSupportedTk75(3591)) 'ISO model permitted'
foreach ($unsupported in @([uint32]0, [uint32]3589, [uint32]3592, [uint32]0x78563412, [uint32]::MaxValue)) {
    Assert-Equal $false ([Tk75.Diagnostics.MonitorProtocol]::IsSupportedTk75($unsupported)) ('Unknown model withheld: ' + $unsupported)
}

Assert-Rejected $null 'Null reply'
foreach ($length in @(0, 1, 10, 64, 66)) { Assert-Rejected ([byte[]]::new($length)) ('Wrong reply length: ' + $length) }
$malformed = [byte[]] $ansi.Clone(); $malformed[0] = 5
Assert-Rejected $malformed 'Wrong Windows report ID'
$malformed = [byte[]] $ansi.Clone(); $malformed[1] = 0x1b
Assert-Rejected $malformed 'Unrelated/stale monitor reply'
$malformed = [byte[]] $ansi.Clone(); $malformed[2] = 255; $malformed[3] = 255; $malformed[4] = 255; $malformed[5] = 255
Assert-Rejected $malformed 'Manufacturer unavailable-identity sentinel'
$unsigned = [byte[]] $ansi.Clone(); $unsigned[2] = 1; $unsigned[3] = 0; $unsigned[4] = 0; $unsigned[5] = 128
Assert-Equal ([uint32]2147483649) ([Tk75.Diagnostics.MonitorProtocol]::DeviceId($unsigned)) 'Device ID retains unsigned high bit'

Write-Output ('PASS: {0} assertions; pure protocol only; no device access or monitor executable loaded.' -f $script:assertions)
