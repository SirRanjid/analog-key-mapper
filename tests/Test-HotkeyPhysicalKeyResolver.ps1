#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskApp = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\App'
if ('Tk75.App.HotkeyPhysicalKeyResolver' -as [type]) { throw 'Run in a fresh PowerShell session.' }
# The harness calls only ResolveMappedScan/FindIndex. Native imports are compiled
# but never invoked; no GUI, hook, input synthesis, HID or output is started.
$typeArguments = @{ Path = @(
    (Join-Path $taskApp 'KeyboardLayout.cs'),
    (Join-Path $taskApp 'KeyboardLayout.Tk75.cs'),
    (Join-Path $taskApp 'KeyboardSuppressionPolicy.cs'),
    (Join-Path $taskApp 'KeyboardScanCodes.cs'),
    (Join-Path $taskApp 'HotkeyPhysicalKeyResolver.cs'),
    (Join-Path $PSScriptRoot 'HotkeyPhysicalKeyResolverHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Drawing.dll', 'System.Core.dll') }
Add-Type @typeArguments
[HotkeyPhysicalKeyResolverHarness]::Run()
