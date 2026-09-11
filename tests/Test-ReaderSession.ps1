#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$typeArguments = @{ Path = @(
    (Join-Path $workspace 'src\HidNative.cs'),
    (Join-Path $workspace 'src\Tk75TravelReport.cs'),
    (Join-Path $workspace 'src\Tk75RgbProtocol.cs'),
    (Join-Path $workspace 'src\Tk75RgbExchange.cs'),
    (Join-Path $workspace 'src\TravelProtocols.cs'),
    (Join-Path $workspace 'src\KeyMap.cs'),
    (Join-Path $workspace 'src\RawLiveView.cs'),
    (Join-Path $workspace 'src\App\ReaderSession.cs'),
    (Join-Path $workspace 'src\App\ManagedMonitorSource.cs'),
    (Join-Path $workspace 'src\App\MonitorWire.cs'),
    (Join-Path $workspace 'src\App\SignedMonitorAccess.cs'),
    (Join-Path $PSScriptRoot 'ReaderSessionHarness.cs')
) }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $typeArguments.ReferencedAssemblies = @('System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Core.dll') }
# Native declarations are compiled but never invoked: every session receives a fake source.
Add-Type @typeArguments
[ReaderSessionHarness]::Run()
