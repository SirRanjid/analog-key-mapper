#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
# Pure packet conversion and the unconditional pre-native startup policy.
# Connect is tested only after verifying that this production policy rejects it
# before DLL/file/device access. No native import or controller call is made.
if ('Tk75.Output.XInputPacket' -as [type]) { throw 'Bitte den Test in einer neuen PowerShell-Sitzung starten, damit der aktuelle Quellcode geladen wird.' }
$compileArguments = @{Path = @((Join-Path $workspace 'src\Core\MappingModels.cs'), (Join-Path $workspace 'src\Output\ViGEmOutput.cs'))}
if ($PSVersionTable.PSVersion.Major -lt 6) { $compileArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll') }
Add-Type @compileArguments
$script:assertions = 0
function Assert-Equal($Expected, $Actual, [string]$Message) {
    $script:assertions++
    if ($Expected -ne $Actual) { throw ('FAIL: {0}; expected {1}; actual {2}' -f $Message, $Expected, $Actual) }
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $script:assertions++
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw ('FAIL: ' + $Message) }
}
function Convert-Frame($Frame) { return [Tk75.Output.XInputPacket]::FromFrame($Frame) }

$frame = [Tk75.Mapping.ControllerFrame]::new()
$packet = Convert-Frame $frame
foreach ($field in @('Buttons','LeftTrigger','RightTrigger','LeftX','LeftY','RightX','RightY')) {
    Assert-Equal 0 $packet.$field ('Neutral field ' + $field)
}
Assert-Equal 12 ([Runtime.InteropServices.Marshal]::SizeOf([type][Tk75.Output.XInputPacket])) 'Native report size'
$offsets = @{Buttons=0; LeftTrigger=2; RightTrigger=3; LeftX=4; LeftY=6; RightX=8; RightY=10}
foreach ($field in $offsets.Keys) {
    Assert-Equal $offsets[$field] ([Runtime.InteropServices.Marshal]::OffsetOf([type][Tk75.Output.XInputPacket], $field).ToInt32()) ('Native field offset ' + $field)
}

foreach ($axis in @('LeftX','LeftY','RightX','RightY')) {
    foreach ($case in @(@(-2,-32768), @(-1,-32768), @(-0.5,-16384), @(0,0), @(0.5,16384), @(1,32767), @(2,32767))) {
        $frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.$axis = [double]$case[0]
        $packet = Convert-Frame $frame
        Assert-Equal $case[1] $packet.$axis ('Axis endpoint/scaling ' + $axis + ' ' + $case[0])
        foreach ($other in @('LeftX','LeftY','RightX','RightY','LeftTrigger','RightTrigger') | Where-Object {$_ -ne $axis}) {
            Assert-Equal 0 $packet.$other ('No cross-channel effect from ' + $axis)
        }
    }
}
foreach ($trigger in @('LeftTrigger','RightTrigger')) {
    foreach ($case in @(@(-1,0), @(0,0), @(0.5,128), @(1,255), @(2,255))) {
        $frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.$trigger = [double]$case[0]
        $packet = Convert-Frame $frame
        Assert-Equal $case[1] $packet.$trigger ('Trigger bounds ' + $trigger + ' ' + $case[0])
    }
}

foreach ($field in @('LeftX','LeftY','RightX','RightY','LeftTrigger','RightTrigger')) {
    foreach ($invalid in @([double]::NaN, [double]::PositiveInfinity, [double]::NegativeInfinity)) {
        $frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.$field = $invalid
        Assert-Rejected { Convert-Frame $frame } ('Reject non-finite ' + $field)
    }
}
Assert-Rejected { [Tk75.Output.XInputPacket]::FromFrame($null) } 'Reject null frame'
$frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.LeftY = 1; $frame.Errors.Add('Synthetic invalid calibration')
Assert-Rejected { Convert-Frame $frame } 'Reject partial frame carrying a validation error'

$buttonMasks = @{DpadUp=0x0001;DpadDown=0x0002;DpadLeft=0x0004;DpadRight=0x0008;Start=0x0010;Back=0x0020;LeftThumb=0x0040;RightThumb=0x0080;LeftShoulder=0x0100;RightShoulder=0x0200;A=0x1000;B=0x2000;X=0x4000;Y=0x8000}
foreach ($button in $buttonMasks.Keys) {
    $frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.Buttons = [uint16]$buttonMasks[$button]
    Assert-Equal $buttonMasks[$button] ([uint16][Enum]::Parse([Tk75.Output.XInputButtons],$button)) ('Documented enum bit ' + $button)
    Assert-Equal $buttonMasks[$button] (Convert-Frame $frame).Buttons ('Independent button ' + $button)
}
$frame = [Tk75.Mapping.ControllerFrame]::new(); $frame.Buttons = [uint16]0xFFFF
Assert-Equal 0xF3FF (Convert-Frame $frame).Buttons 'Reserved button bits cannot escape'
Assert-Equal 0xFFFF $frame.Buttons 'Conversion does not mutate source frame'
$frame.LeftX = -0.25; $frame.LeftY = 0.25; $frame.RightX = 0.75; $frame.RightY = -0.75
$frame.LeftTrigger = 0.2; $frame.RightTrigger = 0.8
$packet = Convert-Frame $frame
Assert-Equal -8192 $packet.LeftX 'Mixed state LX'
Assert-Equal 8192 $packet.LeftY 'Mixed state LY'
Assert-Equal 24575 $packet.RightX 'Mixed state RX'
Assert-Equal -24576 $packet.RightY 'Mixed state RY'
Assert-Equal 51 $packet.LeftTrigger 'Independent LT'
Assert-Equal 204 $packet.RightTrigger 'Independent RT'
$availability = [Tk75.Output.ViGEmOutput]::AvailabilityError
Assert-Equal $false ([string]::IsNullOrEmpty($availability)) 'Known non-neutral boot blocks native backend'
Assert-Equal $true ($availability.Contains('Nullmeldungen')) 'Startup block explains actual neutral-report fault'
$backend = [Tk75.Output.ViGEmOutput]::new()
try {
    Assert-Equal $false $backend.IsConnected 'Constructor cannot activate output'
    Assert-Equal $availability $backend.Status 'Constructor exposes startup block'
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $failure = $null
        try { $backend.Connect() } catch { $failure = $_.Exception.GetBaseException() }
        Assert-Equal $true ($failure -is [NotSupportedException]) 'Connect fails at startup policy before native access'
        Assert-Equal $availability $failure.Message 'Policy error remains precise on repeated Connect'
        Assert-Equal $false $backend.IsConnected 'Rejected Connect remains disconnected'
    }
    $backend.Neutral()
    foreach ($name in @('module', 'client', 'target')) {
        $field = [Tk75.Output.ViGEmOutput].GetField($name, [Reflection.BindingFlags]'NonPublic, Instance')
        Assert-Equal ([IntPtr]::Zero) ($field.GetValue($backend)) ('No native resource acquired: ' + $name)
    }
} finally { $backend.Dispose(); $backend.Dispose() }
Assert-Equal $false $backend.IsConnected 'Repeated Dispose after policy failure stays disconnected'
Write-Host ('PASS: {0} pure XInput and startup-policy assertions. No DLL loaded, no driver or controller accessed.' -f $script:assertions)
