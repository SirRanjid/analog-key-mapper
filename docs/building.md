# Build and verify

[Back to the project](../README.md) · [User guide](user-guide.md) · [Current status](status.md)

## What the download contains

Choose a package from [Releases](https://github.com/SirRanjid/analog-key-mapper/releases):

| Package | Contents |
| --- | --- |
| `AnalogKeyMapper-0.1.0-preview.1-windows-x64.zip` | The unsigned Windows app, keyboard monitor, diagnostic tool and Xbox output helper, plus licenses and checksum verification scripts. No compiler is required to open the editor. |
| `AnalogKeyMapper-0.1.0-preview.1-source.zip` | Complete application and controller-helper sources, vendored Go dependencies, build scripts, tests, documentation, licenses and checksum verification scripts. |

Both ZIPs contain an `AnalogKeyMapper` folder and `SHA256SUMS.txt`. Neither includes a driver installer, personal profiles or private device captures. Source test fixtures include sanitized sample pressure reports, with their provenance documented separately.

Extract the ZIP before running a script or executable. Use a writable folder: the app keeps local data in `data/` beside its executable, or `bin/data/` after a source build. Keep that folder when updating. To use the Windows package, verify it as described below and open `AnalogKeyMapper.exe`.

The Windows package is an **unsigned development preview**. Windows may block it under your security policy; the package does not change that policy.

## Source-build requirements

- Windows x64.
- .NET Framework 4.x with its 64-bit compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
- PowerShell, allowed to run the scripts under your existing Windows policy. The BAT files prefer PowerShell 7 when installed and otherwise use Windows PowerShell. Validation was performed with PowerShell 7; the local Windows PowerShell 5.1 execution policy blocked that separate test attempt.
- For the optional controller helper: Go available on `PATH`, or an explicit path to `go.exe`. The helper requires Go 1.26.2 or newer; the development build was tested with **Go 1.27.1**.

The C# application build does not require Visual Studio or a modern .NET SDK. The app runs as the current user. Driver installation is a separate administrator operation.

## Check a package

Double-click `Verify-Checksums.bat`, or run it from a terminal in the extracted folder:

```bat
Verify-Checksums.bat
```

This checks the files listed in the selected package folder's `SHA256SUMS.txt`. The manifest excludes itself. A mismatch means the checked files differ from that manifest; obtain a fresh trusted package or account for your own edits before using it.

The release also provides a separate `SHA256SUMS.txt` for the two ZIP downloads. Compare those entries with the ZIP hashes from PowerShell before extraction if you want to check the archives themselves:

```powershell
Get-FileHash .\AnalogKeyMapper-0.1.0-preview.1-windows-x64.zip -Algorithm SHA256
Get-FileHash .\AnalogKeyMapper-0.1.0-preview.1-source.zip -Algorithm SHA256
```

Checksums detect file changes. They are not a code signature or independent proof of the publisher's identity when the files and manifest come from the same download.

## Compile from source

This step is optional when using the Windows package. In the extracted source folder, run:

```bat
Build.bat
```

This compiles `bin/AnalogKeyMapper.exe`, `bin/Tk75Monitor.exe` and `bin/Tk75Diag.exe`, keeps the required cursor license, and produces `bin/SHA256SUMS.txt`. It does not launch the app or install a driver.

| BAT option | Effect |
| --- | --- |
| No options | Local app build with `-AllowUnsignedMonitor`. |
| `--with-controller` | Also build the optional Xbox output helper with Go. |
| `--strict` | Omit the local unsigned-monitor option. Live keyboard access then requires a valid signed helper. |
| `--no-pause` | Exit without waiting for a key, for terminal or scripted use. |

Examples:

```bat
Build.bat --with-controller
Build.bat --strict --no-pause
Verify-Checksums.bat bin
```

The local unsigned-monitor option accepts a helper with no signature; it still rejects an invalid existing signature. It does not bypass Windows application control. The strict build does not sign anything automatically. This preview must not be treated as a signed release.

If Windows blocks a script or executable, keep the actual error and stop that attempt. These instructions do not require disabling application control, antivirus, Secure Boot or driver-signing checks.

After a successful build, run `bin\AnalogKeyMapper.exe`. Close the app and its keyboard helper before rebuilding into the same output folder. To build a separate copy using PowerShell:

```powershell
.\Build.ps1 -OutputDirectory build\review -AllowUnsignedMonitor
```

## Optional Xbox controller output

The keyboard interface and mapping editor can be built without the virtual-controller dependencies. To enable the current experimental Xbox backend:

1. The **Windows package already includes `ViiperOutputHost.exe`**. For a source build, install a suitable [Go toolchain](https://go.dev/dl/), then run `Build.bat --with-controller`. This builds the bundled output-helper source into `bin/ViiperOutputHost.exe`. The source package includes its pinned module dependencies for an offline build; Go itself must be installed separately.
2. Obtain **usbip-win2 0.9.8.0 x64** from its [official release](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.0). Follow the project's official installation instructions and complete any requested restart. The driver is separate from both packages and requires administrator installation. Its installer may briefly restart connected USB devices.
3. Keep `ViiperOutputHost.exe` and its license/source notices with the app. Start the mapper normally, choose an Xbox slot, then use its USB connector to connect it.

To select a Go executable directly:

```powershell
.\Build-OutputHelper.ps1 -GoExecutable 'C:\path\to\go.exe'
```

The helper source is derived from VIIPER v0.7.0, commit `6b71b148a2243fab77ee1a46f4e22e00bd7d5a04`. See [its source notice](../src/ViiperOutputHost/NOTICE.md) for the origin and licenses. The helper build does not install USB/IP or create a controller.

Only **one Xbox controller may be connected at a time**. The current backend also expects exclusive use of its USB/IP session. DualSense output is not enabled, and installing ViGEm does not enable the active Xbox path. The tested dependency versions do not establish compatibility with arbitrary other driver versions or configurations. See [status and limits](status.md).

## Verify your local build

```bat
Verify-Checksums.bat bin --no-pause
```

Or use the verifier directly:

```powershell
.\scripts\Verify-Checksums.ps1
.\scripts\Verify-Checksums.ps1 -Directory bin
```

The source package's root manifest describes the distributed sources; the Windows package's root manifest describes its runtime files. The `bin` manifest describes files produced by your local build. A locally generated manifest confirms later file integrity; it does not claim that two separate compilers or machines produce identical bytes.

## Run tests

```powershell
.\tests\Test-AppUi.ps1 -KeepArtifacts
.\tests\Test-VisualKeyboard.ps1
.\tests\Test-RgbLifecycle.ps1 -KeepArtifacts
.\Test-All.ps1
```

Tests cover pure calculations, synthetic input sources, Windows calls and offscreen UI controls. They do not install a driver or create a real virtual controller. Windows desktop support is required for the UI suites. Record failed or blocked suites accurately; see [the recorded validation and its limits](status.md).
