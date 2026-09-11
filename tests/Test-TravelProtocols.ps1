#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
# Only metadata and saved report bytes are examined. No native function is invoked.
Add-Type -Path @((Join-Path $workspace 'src\HidNative.cs'), (Join-Path $workspace 'src\Tk75TravelReport.cs'), (Join-Path $workspace 'src\TravelProtocols.cs'))
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}

# Sanitized actual input-collection metadata from captures/tk75-inventory.json,
# recorded 10 September 2026. Paths, serial numbers and other physical IDs omitted.
# This embedded evidence keeps tests reproducible without the ignored captures folder.
$recordedMetadata = @'
{"vendorId":12625,"productId":20528,"product":"TK75 TMR","version":1027,"usagePage":65535,"usage":1,"inputReportLength":32,"outputReportLength":0,"featureReportLength":0,"reportCapabilities":[{"reportType":"input","kind":"value","usagePage":65535,"reportId":5,"linkCollection":0,"isRange":false,"isAbsolute":true,"usageMinimum":1,"usageMaximum":1,"bitSize":8,"reportCount":31,"logicalMinimum":0,"logicalMaximum":255,"physicalMinimum":0,"physicalMaximum":0,"unitsExponent":0,"units":0}]}
'@
function Recorded-Device {
    $record = $recordedMetadata | ConvertFrom-Json
    $device = [Tk75.Diagnostics.CollectionInfo]::new()
    foreach ($name in @('vendorId', 'productId', 'product', 'version', 'usagePage', 'usage', 'inputReportLength', 'outputReportLength', 'featureReportLength')) { $device.$name = $record.$name }
    foreach ($capability in $record.reportCapabilities) {
        $cap = [Collections.Generic.Dictionary[string,object]]::new()
        foreach ($property in $capability.PSObject.Properties) { $cap.Add($property.Name, $property.Value) }
        $device.reportCapabilities.Add($cap)
    }
    return $device
}
function Assert-Unknown($Device, [string] $Reason) { Assert-Equal $null ([Tk75.Diagnostics.TravelProtocols]::Find($Device)) $Reason }

$device = Recorded-Device
$decoder = [Tk75.Diagnostics.TravelProtocols]::Find($device)
Assert-Equal 'rongyuan-travel-05-1b-u16le-32-v1' $decoder.Id 'Actual recorded capability keys select expected candidate'
Assert-Equal $true ([Tk75.Diagnostics.TravelProtocols]::IsVendorInput($device)) 'Actual vendor input available for passive capture'
foreach ($version in @(0, 0x0300, 0x0403, 0x0406, 0x0500, 65535)) {
    $device.version = $version
    Assert-Equal $decoder.Id ([Tk75.Diagnostics.TravelProtocols]::Find($device).Id) ('Same format remains a candidate across bcdDevice ' + $version)
}
foreach ($numeric in @([byte]8, [uint16]8, [int64]8, [double]8, [decimal]8)) {
    $device = Recorded-Device; $device.reportCapabilities[0]['bitSize'] = $numeric
    Assert-Equal $decoder.Id ([Tk75.Diagnostics.TravelProtocols]::Find($device).Id) 'Exact integral metadata survives common CLR/JSON number representations'
}
foreach ($badBits in @(0, 1, 7, 9, 16, 8.4, 8.000000000000002, '8', $true, $null, [double]::NaN, [double]::PositiveInfinity, [decimal]8.1, [datetime]::MinValue)) {
    $device = Recorded-Device; $device.reportCapabilities[0]['bitSize'] = $badBits
    Assert-Unknown $device 'Wrong or nonintegral bit-size metadata is not rounded/coerced into a match'
}
foreach ($required in @('reportType', 'kind', 'reportId', 'usagePage', 'bitSize', 'reportCount')) {
    $device = Recorded-Device; [void]$device.reportCapabilities[0].Remove($required)
    Assert-Unknown $device ('Missing capability field: ' + $required)
}
foreach ($case in @(
    @{ Field = 'kind'; Value = 'button' }, @{ Field = 'reportType'; Value = 'Input' },
    @{ Field = 'reportId'; Value = 4 }, @{ Field = 'usagePage'; Value = 0xff00 },
    @{ Field = 'reportCount'; Value = 30 }
)) {
    $device = Recorded-Device; $device.reportCapabilities[0][$case.Field] = $case.Value
    Assert-Unknown $device ('Unrecognized input capability: ' + $case.Field)
}
foreach ($case in @(@{ Field = 'usagePage'; Value = 0xff00 }, @{ Field = 'usage'; Value = 2 }, @{ Field = 'inputReportLength'; Value = 64 })) {
    $device = Recorded-Device; $device.($case.Field) = $case.Value
    Assert-Unknown $device ('Unrecognized collection metadata: ' + $case.Field)
}
$device = Recorded-Device; $device.error = 'synthetic metadata error'; Assert-Unknown $device 'Failed metadata cannot identify a decoder'
$device = Recorded-Device; $device.reportCapabilities = $null; Assert-Unknown $device 'Missing capability array'
Assert-Unknown $null 'Missing device'
$device = Recorded-Device; $device.reportCapabilities.Add($device.reportCapabilities[0]); Assert-Unknown $device 'Duplicate input capability is ambiguous'
$device = Recorded-Device; $device.reportCapabilities.Add($null); Assert-Unknown $device 'Partially missing capability array cannot match'
$device = Recorded-Device
$extra = [Collections.Generic.Dictionary[string,object]]::new(); $extra.Add('reportType', 'unknown'); $device.reportCapabilities.Add($extra)
Assert-Unknown $device 'Unknown report-type metadata cannot match'
$device = Recorded-Device; $device.usagePage = 65536
Assert-Equal $false ([Tk75.Diagnostics.TravelProtocols]::IsVendorInput($device)) 'Out-of-range usage page rejected even for generic capture'
$device = Recorded-Device; $device.vendorId = 0x1234; $device.productId = 0x5678; $device.product = 'Synthetic unrelated layout'
Assert-Equal $decoder.Id ([Tk75.Diagnostics.TravelProtocols]::Find($device).Id) 'Generic format candidate does not infer manufacturer layout'

# Every REAL fixture report must still validate. The common decoder deliberately
# labels even measured WASD as Index n; only a matching external key map may name it.
$count = 0
foreach ($fixture in @('outside-sandbox-w.jsonl', 'live-wasd-verification.jsonl')) {
    foreach ($line in [IO.File]::ReadLines((Join-Path $PSScriptRoot ('fixtures\' + $fixture)))) {
        $record = $line | ConvertFrom-Json
        $parts = $record.hex.Split('-')
        [byte[]] $report = $parts | ForEach-Object { [Convert]::ToByte($_, 16) }
        $sample = [Tk75.Diagnostics.TravelSample]::new(); $errorText = $null
        Assert-Equal $true ($decoder.TryParse($report, [ref]$sample, [ref]$errorText)) 'Saved real report validated through registry decoder'
        Assert-Equal ('Index ' + [Convert]::ToInt32($parts[4], 16)) $sample.KeyLabel 'No automatic layout label from shared protocol'
        Assert-Equal ([Convert]::ToInt32(($parts[3] + $parts[2]), 16)) $sample.RawValue 'Generic decoder preserves recorded raw value'
        $count++
    }
}
Assert-Equal 4898 $count 'Both real fixtures completely replayed'

# SYNTHETIC bad input after a valid frame must never expose the previous sample.
[byte[]] $valid = @(5, 27, 0x57, 1, 14) + [byte[]]::new(27)
foreach ($fault in @('null', 'length', 'reportId', 'opcode', 'tail')) {
    $sample = [Tk75.Diagnostics.TravelSample]::new(); $errorText = $null
    Assert-Equal $true ($decoder.TryParse($valid, [ref]$sample, [ref]$errorText)) 'Valid preceding sample'
    $bad = [byte[]]$valid.Clone()
    switch ($fault) {
        'null' { $bad = $null }
        'length' { $bad = [byte[]]::new(31) }
        'reportId' { $bad[0] = 0 }
        'opcode' { $bad[1] = 0x8f }
        'tail' { $bad[31] = 1 }
    }
    Assert-Equal $false ($decoder.TryParse($bad, [ref]$sample, [ref]$errorText)) ('Unsupported report rejected: ' + $fault)
    Assert-Equal $null $sample.KeyLabel 'Failed decode clears old sample label'
    Assert-Equal 0 $sample.RawValue 'Failed decode clears old raw out-parameter; caller must honor false'
    Assert-Equal $false ([string]::IsNullOrWhiteSpace($errorText)) 'Unsupported report includes reason'
}
Write-Output ('PASS: {0} assertions; recorded metadata, strict candidate selection and {1} real saved reports; no hardware calls.' -f $script:assertions, $count)
