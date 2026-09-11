#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Diese lesende Signaturprüfung erfordert Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
if ('Tk75.App.SignedMonitorAccess' -as [type]) { throw 'Bitte eine frische PowerShell-Sitzung für diese Quellcodeprüfung verwenden.' }
# Only the new read-only trust verifier is compiled. The subject EXEs are opened
# as data, never executed or loaded. No certificate/policy change or fallback.
Add-Type -Path (Join-Path $workspace 'src\App\SignedMonitorAccess.cs')
$taskAssertions = 0
function Assert-Trust([bool]$condition, [string]$message) {
    $script:taskAssertions++
    if (-not $condition) { throw "Signaturtest fehlgeschlagen: $message" }
}
$taskMonitorPath = Join-Path $workspace 'bin\Tk75Monitor.exe'
$taskMicrosoftPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Assert-Trust (Test-Path -LiteralPath $taskMonitorPath) 'Unsigned original monitor fixture missing.'
Assert-Trust (Test-Path -LiteralPath $taskMicrosoftPath) 'Signed Microsoft compiler fixture missing.'

$taskUnavailable = [Tk75.App.SignedMonitorAccess]::AvailabilityMessage($taskMonitorPath)
Assert-Trust ($taskUnavailable -like '*nicht digital signiert*') 'Unsigned monitor must be rejected with a useful message.'
$taskRejected = $false
try { [Tk75.App.SignedMonitorAccess]::RequireTrusted($taskMonitorPath) }
catch { $taskRejected = $_.Exception.InnerException -is [System.Security.SecurityException] }
Assert-Trust $taskRejected 'RequireTrusted must fail closed for unsigned monitor.'
$taskRejected = $false
try { [Tk75.App.SignedMonitorAccess]::AcquireFile($taskMonitorPath, $false).Dispose() }
catch { $taskRejected = $_.Exception.InnerException -is [System.Security.SecurityException] }
Assert-Trust $taskRejected 'Explicit strict mode must reject unsigned monitor.'
$taskLease = [Tk75.App.SignedMonitorAccess]::AcquireFile($taskMonitorPath, $true)
try {
    Assert-Trust ($taskLease -is [System.IO.FileStream] -and $taskLease.CanRead -and -not $taskLease.CanWrite) 'Unsigned opt-in must retain a read-only lease.'
} finally { $taskLease.Dispose() }
Assert-Trust (-not $taskLease.CanRead) 'Unsigned lease must close on Dispose.'

$taskMicrosoftMessage = [Tk75.App.SignedMonitorAccess]::AvailabilityMessage($taskMicrosoftPath)
Assert-Trust ($null -eq $taskMicrosoftMessage) "Original Microsoft compiler signature not accepted: $taskMicrosoftMessage"
[Tk75.App.SignedMonitorAccess]::RequireTrusted($taskMicrosoftPath)
Assert-Trust $true 'RequireTrusted accepted signed Microsoft compiler without executing it.'
$taskLease = [Tk75.App.SignedMonitorAccess]::AcquireTrustedFile($taskMicrosoftPath)
try {
    Assert-Trust ($taskLease -is [System.IO.FileStream] -and $taskLease.CanRead -and -not $taskLease.CanWrite) 'Verified lease must be read-only.'
} finally { $taskLease.Dispose() }
Assert-Trust (-not $taskLease.CanRead) 'Verified lease must close on Dispose.'
$taskLease = [Tk75.App.SignedMonitorAccess]::AcquireFile($taskMicrosoftPath, $true)
try { Assert-Trust $taskLease.CanRead 'Unsigned opt-in must still accept a valid signature.' }
finally { $taskLease.Dispose() }

# Corrupt a data-only copy of a signed PE in its first nonempty section. Never
# execute or load this subject: both modes must reject its invalid digest.
$taskFixtureDirectory = Join-Path $workspace ('build\signature-access-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskFixtureDirectory | Out-Null
$taskDamagedPath = Join-Path $taskFixtureDirectory 'invalid-digest.exe'
$taskBytes = [System.IO.File]::ReadAllBytes($taskMicrosoftPath)
$taskPeOffset = [BitConverter]::ToInt32($taskBytes, 0x3c)
$taskSectionCount = [BitConverter]::ToUInt16($taskBytes, $taskPeOffset + 6)
$taskOptionalHeaderSize = [BitConverter]::ToUInt16($taskBytes, $taskPeOffset + 20)
$taskSections = $taskPeOffset + 24 + $taskOptionalHeaderSize
$taskModified = $false
for ($taskSection = 0; $taskSection -lt $taskSectionCount; $taskSection++) {
    $taskSectionOffset = $taskSections + 40 * $taskSection
    $taskRawSize = [BitConverter]::ToUInt32($taskBytes, $taskSectionOffset + 16)
    $taskRawOffset = [BitConverter]::ToUInt32($taskBytes, $taskSectionOffset + 20)
    if ($taskRawSize -ge 256 -and $taskRawOffset -ge $taskSections + 40 * $taskSectionCount -and $taskRawOffset + $taskRawSize -le $taskBytes.Length) {
        $taskBytes[$taskRawOffset + 128] = $taskBytes[$taskRawOffset + 128] -bxor 1
        $taskModified = $true
        break
    }
}
Assert-Trust $taskModified 'Signed data fixture must have a modifiable PE section.'
[System.IO.File]::WriteAllBytes($taskDamagedPath, $taskBytes)
foreach ($taskAllowUnsigned in @($false, $true)) {
    $taskRejected = $false
    try { [Tk75.App.SignedMonitorAccess]::AcquireFile($taskDamagedPath, $taskAllowUnsigned).Dispose() }
    catch { $taskRejected = $_.Exception.InnerException -is [System.Security.SecurityException] -and $_.Exception.InnerException.Message -like '*0x80096010*' }
    Assert-Trust $taskRejected "Invalid digest must be rejected (allowUnsigned=$taskAllowUnsigned)."
    $taskExclusiveRead = [System.IO.File]::Open($taskDamagedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    try { Assert-Trust $taskExclusiveRead.CanRead 'Rejected signature must release its file lease.' }
    finally { $taskExclusiveRead.Dispose() }
}

foreach ($taskBadPath in @('Tk75Monitor.exe', 'https://example.invalid/Tk75Monitor.exe', 'C:\invalid.exe:payload.exe', 'C:\invalid.txt')) {
    $taskMessage = [Tk75.App.SignedMonitorAccess]::AvailabilityMessage($taskBadPath)
    Assert-Trust ($null -ne $taskMessage) "Unsupported path was accepted: $taskBadPath"
    $taskRejected = $false
    try { [Tk75.App.SignedMonitorAccess]::AcquireFile($taskBadPath, $true).Dispose() }
    catch { $taskRejected = $_.Exception.InnerException -is [System.ArgumentException] }
    Assert-Trust $taskRejected "Unsigned opt-in must retain path validation: $taskBadPath"
}
[pscustomobject]@{ assertions=$taskAssertions; unsignedMonitor='strict rejected / explicit opt-in accepted'; signedMicrosoftCompiler='trusted'; invalidSignature='rejected in both modes'; subjectExecutablesStarted=0; certificateOrPolicyChanges=0 }
