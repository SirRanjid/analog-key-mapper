#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$typeArguments = @{ Path = @(
    (Join-Path $workspace 'src\HidNative.cs'),
    (Join-Path $workspace 'src\Tk75TravelReport.cs'),
    (Join-Path $workspace 'src\KeyMap.cs'),
    (Join-Path $workspace 'src\KeyLearner.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Core.dll') }
# CollectionInfo is used only as a plain metadata object. No native method is called.
Add-Type @typeArguments
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string] $Message) {
    $script:assertions++
    if ($Expected -cne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}
function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure) {
            if ($failure -is [IO.InvalidDataException]) { $rejected = $true; break }
            $failure = $failure.InnerException
        }
        if (-not $rejected) { throw }
    }
    Assert-Equal $true $rejected $Message
}
function Sample([int] $Index, [int] $Raw, [string] $Label = 'same label') {
    $sample = [Tk75.Diagnostics.TravelSample]::new()
    $sample.KeyIndex = $Index; $sample.RawValue = $Raw; $sample.KeyLabel = $Label
    return $sample
}
function Device {
    $device = [Tk75.Diagnostics.CollectionInfo]::new()
    $device.vendorId = 0x3151; $device.productId = 0x5030; $device.version = 0x0403
    $device.product = 'TK75 TMR'; $device.usagePage = 65535; $device.usage = 1
    $device.inputReportLength = 32; $device.featureReportLength = 0; $device.outputReportLength = 0
    $cap = [Collections.Generic.Dictionary[string,object]]::new()
    $cap.Add('reportType', 'input'); $cap.Add('kind', 'value'); $cap.Add('reportId', 5)
    $cap.Add('usagePage', 65535); $cap.Add('bitSize', 8); $cap.Add('reportCount', 31)
    $device.reportCapabilities.Add($cap)
    return $device
}

$testParent = Join-Path $PSScriptRoot 'tmp'
$testRoot = Join-Path $testParent ('key-map-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    $fingerprint = 'a' * 64
    $original = [Tk75.Diagnostics.KeyMapStore]::Create($fingerprint)
    Assert-Equal 1 $original.SchemaVersion 'New map schema'
    Assert-Equal 'Index 255' $original.GetLabel(255) 'Unknown label fallback'
    $first = [Tk75.Diagnostics.KeyMapStore]::SetLabel($original, 14, 'W')
    Assert-Equal 0 $original.Entries.Count 'SetLabel leaves original unchanged'
    $second = [Tk75.Diagnostics.KeyMapStore]::SetLabel($first, 9, 'Ä / Left Shift')
    $third = [Tk75.Diagnostics.KeyMapStore]::SetLabel($second, 14, 'Forward')
    Assert-Equal 'W' $second.GetLabel(14) 'SetLabel deep copy preserves previous entry'
    Assert-Equal 'Forward' $third.GetLabel(14) 'SetLabel replaces requested index'
    Assert-Equal 'Ä / Left Shift' $third.GetLabel(9) 'SetLabel preserves unrelated entry'
    $path = Join-Path $testRoot 'map.json'
    [Tk75.Diagnostics.KeyMapStore]::Save($path, $second)
    $loaded = [Tk75.Diagnostics.KeyMapStore]::Load($path)
    Assert-Equal $fingerprint $loaded.ProtocolFingerprint 'Fingerprint roundtrip'
    Assert-Equal 2 $loaded.Entries.Count 'Entry count roundtrip'
    Assert-Equal 'Ä / Left Shift' $loaded.GetLabel(9) 'Unicode label roundtrip'
    [Tk75.Diagnostics.KeyMapStore]::Save($path, $third)
    Assert-Equal 'Forward' ([Tk75.Diagnostics.KeyMapStore]::Load($path).GetLabel(14)) 'Atomic replacement saves update'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $testRoot -Filter '*.tmp' -Force).Count 'No temporary files remain'
    $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $foreign = [Tk75.Diagnostics.KeyMapStore]::Create(('b' * 64))
    Assert-Rejected { [Tk75.Diagnostics.KeyMapStore]::Save($path, $foreign) } 'Different protocol map cannot overwrite existing map'
    Assert-Equal $before (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash 'Foreign save leaves previous file intact'
    $invalid = [Tk75.Diagnostics.KeyMapStore]::Create($fingerprint); $invalid.SchemaVersion = 2
    Assert-Rejected { [Tk75.Diagnostics.KeyMapStore]::Save($path, $invalid) } 'Invalid save rejected before replacing file'
    Assert-Equal $before (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash 'Invalid save leaves previous file intact'

    $validJson = '{"schemaVersion":1,"protocolFingerprint":"' + $fingerprint + '","entries":[{"keyIndex":14,"label":"W"}]}'
    $badPath = Join-Path $testRoot 'invalid.json'
    $badJson = @(
        '', '{}', '[]', '{broken',
        ($validJson.Replace('"schemaVersion":1,', '')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":2')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":1.5')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":"1"')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1')),
        ($validJson.Replace('"entries":', '"extra":true,"entries":')),
        ($validJson.Replace('"entries":', '"__type":"KeyMapDocument:#Tk75.Diagnostics","entries":')),
        ($validJson.Replace('"keyIndex":14', '"keyIndex":-1')),
        ($validJson.Replace('"keyIndex":14', '"keyIndex":256')),
        ($validJson.Replace('"keyIndex":14', '"keyIndex":14.5')),
        ($validJson.Replace('"keyIndex":14', '"keyIndex":14,"extra":1')),
        ($validJson.Replace('"keyIndex":14', '"keyIndex":14,"keyIndex":14')),
        ($validJson.Replace('"label":"W"', '"label":null')),
        ($validJson.Replace('"label":"W"', '"label":true')),
        ($validJson.Replace('"label":"W"', '"label":""')),
        ($validJson.Replace('"label":"W"', '"label":"   "')),
        ($validJson.Replace('"label":"W"', '"label":"bad\nlabel"')),
        ($validJson.Replace('"label":"W"', '"label":"' + ('x' * 81) + '"')),
        ($validJson.Replace('"keyIndex":14,"label":"W"', '"keyIndex":14')),
        ($validJson.Replace('[{"keyIndex":14,"label":"W"}]', 'null')),
        ($validJson.Replace('[{"keyIndex":14,"label":"W"}]', '{}')),
        ($validJson.Replace('[{"keyIndex":14,"label":"W"}]', '[null]')),
        ($validJson.Replace('[{"keyIndex":14,"label":"W"}]', '[{"keyIndex":14,"label":"W"},{"keyIndex":14,"label":"Other"}]')),
        ($validJson.Replace($fingerprint, 'invalid'))
    )
    $badNumber = 0
    foreach ($bad in $badJson) {
        [IO.File]::WriteAllText($badPath, $bad, [Text.UTF8Encoding]::new($false))
        Assert-Rejected { [Tk75.Diagnostics.KeyMapStore]::Load($badPath) } ('Invalid JSON/map case ' + (++$badNumber))
    }
    [IO.File]::WriteAllText($badPath, $validJson.Replace('"label":"W"', '"label":"' + ('x' * 80) + '"'), [Text.UTF8Encoding]::new($false))
    Assert-Equal 80 ([Tk75.Diagnostics.KeyMapStore]::Load($badPath).GetLabel(14).Length) 'Maximum-length label accepted'

    # Logical fingerprints include semantic protocol fields and exclude physical identity.
    $device = Device
    $baseFingerprint = [Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')
    Assert-Equal 64 $baseFingerprint.Length 'Fingerprint SHA256 width'
    $device.devicePath = 'synthetic-path-A'; $device.serial = 'synthetic-serial-A'; $device.manufacturer = 'other'; $device.error = 'ignored diagnostic'
    Assert-Equal $baseFingerprint ([Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')) 'Physical identity and diagnostics excluded'
    $device.version++
    Assert-Equal $false ($baseFingerprint -eq [Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')) 'Firmware descriptor version change invalidates automatic map'
    $device = Device
    Assert-Equal $false ($baseFingerprint -eq [Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v2')) 'Decoder protocol version change invalidates map'
    $device.reportCapabilities[0]['reportCount'] = 30
    Assert-Equal $false ($baseFingerprint -eq [Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')) 'Report layout change invalidates map'
    $device = Device
    $cap2 = [Collections.Generic.Dictionary[string,object]]::new(); $cap2.Add('reportType', 'feature'); $cap2.Add('reportId', 0)
    $device.reportCapabilities.Add($cap2)
    $orderedFingerprint = [Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')
    $device.reportCapabilities.Reverse()
    Assert-Equal $orderedFingerprint ([Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')) 'Capability enumeration order does not change fingerprint'
    $previousCulture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
        Assert-Equal $orderedFingerprint ([Tk75.Diagnostics.ProtocolFingerprint]::Calculate($device, 'travel-32-05-1b-v1')) 'Fingerprint independent of current culture'
    }
    finally { [Threading.Thread]::CurrentThread.CurrentCulture = $previousCulture }

    # All learner sequences are SYNTHETIC: no physical key presses are generated.
    $learner = [Tk75.Diagnostics.KeyLearner]::new()
    Assert-Equal 'Preparing' $learner.State.ToString() 'Initial preparing state'
    Assert-Equal -1 $learner.KeyIndex 'No guessed initial key'
    $learner.Feed((Sample 9 0), 0); $learner.Feed((Sample 14 0), 1)
    Assert-Equal 'Preparing' $learner.State.ToString() 'Initial zero traffic ignored'
    $learner.Feed((Sample 14 321 'wrong display name'), 2)
    $learner.Feed((Sample 14 65535 'another display name'), 10000)
    Assert-Equal 'Pressing' $learner.State.ToString() 'Initially held key alone never completes learning'
    Assert-Equal 65535 $learner.MaxObservedRaw 'No assumed maximum or clamping'
    $learner.Feed((Sample 9 0), 10001)
    Assert-Equal 'Pressing' $learner.State.ToString() 'Unrelated zero cannot release selected key'
    $learner.Feed((Sample 14 0), 10002)
    Assert-Equal 'Armed' $learner.State.ToString() 'First release establishes baseline only'
    $learner.Feed((Sample 14 0), 20000)
    Assert-Equal 'Armed' $learner.State.ToString() 'Repeated zeros or elapsed time cannot count another cycle'
    $learner.Feed((Sample 14 1), 20001)
    Assert-Equal 'Pressing' $learner.State.ToString() 'Second positive press required'
    $learner.Feed((Sample 14 0), 20002)
    Assert-Equal 'Completed' $learner.State.ToString() 'Two independent press/release cycles confirm key'
    Assert-Equal 14 $learner.KeyIndex 'Identity comes from index, never label text'
    Assert-Equal $null $learner.Error 'Successful learner has no error'
    foreach ($stage in @('first-press', 'armed', 'second-press', 'completed')) {
        $ambiguous = [Tk75.Diagnostics.KeyLearner]::new()
        $ambiguous.Feed((Sample 9 500), 0)
        if ($stage -ne 'first-press') { $ambiguous.Feed((Sample 9 0), 1) }
        if ($stage -eq 'second-press' -or $stage -eq 'completed') { $ambiguous.Feed((Sample 9 10), 2) }
        if ($stage -eq 'completed') { $ambiguous.Feed((Sample 9 0), 3) }
        $ambiguous.Feed((Sample 14 1), 4)
        Assert-Equal 'Ambiguous' $ambiguous.State.ToString() ('Any other positive index rejects learning at ' + $stage)
        Assert-Equal $false ([string]::IsNullOrWhiteSpace($ambiguous.Error)) 'Ambiguity reason available'
        Assert-Equal 9 $ambiguous.KeyIndex 'Stronger first signal is not silently replaced by another key'
        $ambiguous.Feed((Sample 9 0), 5)
        Assert-Equal 'Ambiguous' $ambiguous.State.ToString() 'Ambiguous attempt cannot later appear successful'
    }
    $backward = [Tk75.Diagnostics.KeyLearner]::new()
    $backward.Feed((Sample 14 1), 100); $backward.Feed((Sample 14 0), 99)
    Assert-Equal 'Ambiguous' $backward.State.ToString() 'Backward times cannot create a valid cycle'
    Write-Output ('PASS: {0} assertions; strict JSON, atomic save, logical fingerprints and synthetic two-cycle learner; no hardware calls.' -f $script:assertions)
}
finally {
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    $resolvedParent = [IO.Path]::GetFullPath($testParent).TrimEnd('\') + '\'
    if ($resolvedTest.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedTest).StartsWith('key-map-')) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
