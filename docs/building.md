# Install, build and verify

[Back to the project](../README.md) · [User guide](user-guide.md) · [Current status](status.md)

**1.0.0-rc.1 is a free, unsigned release candidate.** Final hardware and game acceptance is pending before stable 1.0. Use the version shown on the release asset; compiling these sources does not sign or publish a release.

## What the download contains

Choose a package from [Releases](https://github.com/SirRanjid/analog-key-mapper/releases):

| Package | Contents |
| --- | --- |
| `AnalogKeyMapper-1.0.0-rc.1-windows-x64.zip` | The unsigned Windows app, keyboard monitor, diagnostic tool and Xbox/DualSense output helper, plus licenses and checksum verification scripts. No compiler is required to open the editor. |
| `AnalogKeyMapper-1.0.0-rc.1-source.zip` | Complete application and controller-helper sources, vendored Go dependencies, build scripts, tests, documentation, licenses and checksum verification scripts. |

Both ZIPs contain an `AnalogKeyMapper` folder and `SHA256SUMS.txt`. Neither includes a driver installer, personal profiles or private device captures. Source test fixtures include sanitized sample pressure reports, with their provenance documented separately.

Extract the ZIP before running a script or executable. Use a short, writable folder: the app keeps local data in `data/` beside its executable, or `bin/data/` after a source build. Keep that folder when updating. To use the Windows package, verify it as described below and open `AnalogKeyMapper.exe`.

Back up your existing `data/` folder and close the mapper and its helpers before replacing application files. Do not copy example profiles over your saved setup. Opening the executable normally shows the editor; Windows startup and controller reconnection each require a separate opt-in. See [background startup](background-startup.md).

Windows application control may block the unsigned app or a helper. Such blocks occurred on the development machine; final candidate execution and hardware/game acceptance remain pending. Keep the actual error and stop the blocked attempt. The package does not change security policy or require protection to be disabled.

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
Get-FileHash .\AnalogKeyMapper-1.0.0-rc.1-windows-x64.zip -Algorithm SHA256
Get-FileHash .\AnalogKeyMapper-1.0.0-rc.1-source.zip -Algorithm SHA256
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
| `--with-controller` | Also build the Xbox/DualSense output helper with Go. |
| `--strict` | Omit the local unsigned-monitor option. Live keyboard access then requires a valid signed helper. |
| `--no-pause` | Exit without waiting for a key, for terminal or scripted use. |

Examples:

```bat
Build.bat --with-controller
Build.bat --strict --no-pause
Verify-Checksums.bat bin
```

The local unsigned-monitor option accepts a helper with no signature; it still rejects an invalid existing signature. It does not bypass Windows application control. The strict build does not sign anything automatically. This candidate must not be treated as a signed release.

If Windows blocks a script or executable, keep the actual error and stop that attempt. These instructions do not require disabling application control, antivirus, Secure Boot or driver-signing checks.

After a successful build, run `bin\AnalogKeyMapper.exe`. Close the app and its keyboard helper before rebuilding into the same output folder. To build a separate copy using PowerShell:

```powershell
.\Build.ps1 -OutputDirectory build\review -AllowUnsignedMonitor
```

<a id="optional-xbox-controller-output"></a>

## Virtual controller output

The keyboard interface and mapping editor can be built without the virtual-controller dependencies. Xbox 360 and DualSense output share the following setup:

1. The **Windows package already includes `ViiperOutputHost.exe`**. For a source build, install a suitable [Go toolchain](https://go.dev/dl/), then run `Build.bat --with-controller`. This builds the bundled output-helper source into `bin/ViiperOutputHost.exe`. The source package includes its pinned module dependencies for an offline build; Go itself must be installed separately.
2. Obtain **usbip-win2 0.9.8.0 x64** from its [official release](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.0). Follow the project's official installation instructions and complete any requested restart. The driver is separate from both packages and requires administrator installation. Its installer may briefly restart connected USB devices.
3. Keep `ViiperOutputHost.exe` and its license/source notices with the app. Start the mapper normally, create Xbox and/or DualSense slots, then use each slot's USB connector to connect it.

To select a Go executable directly:

```powershell
.\Build-OutputHelper.ps1 -GoExecutable 'C:\path\to\go.exe'
```

The helper source is derived from VIIPER v0.7.0, commit `6b71b148a2243fab77ee1a46f4e22e00bd7d5a04`. See [its source notice](../src/ViiperOutputHost/NOTICE.md) for the origin and licenses. The helper build does not install USB/IP or create a controller.

The application offers **32 configured output slots in total**, which can mix Xbox and DualSense. Windows provides at most **four XInput slots system-wide**, including physical controllers; DualSense devices use HID separately and do not occupy XInput slots. The recorded real-device acceptance is **two Xbox plus two DualSense outputs on an earlier helper build**. It does not establish final candidate acceptance or performance with 32 connected devices. Larger configurations remain to be load-tested. See [the acceptance record](multi-controller-acceptance.md) and [known issue](status.md#current-known-issue).

Each connected slot owns its own isolated helper and USB/IP attachment. Connecting or disconnecting one does not intentionally alter other slots or unrelated devices. Installing ViGEm does not enable this backend. The tested dependency versions do not establish compatibility with arbitrary other driver versions, games or PS5 consoles; see [status and limits](status.md).

Connection work is asynchronous and cancellable. Reconnection after startup is off by default; if enabled, it uses only the previous confirmed normal exit and consumes that record once. Windows shutdown limits the total cleanup wait rather than waiting indefinitely. These mechanisms improve handling of failures; they are not a guarantee of completed restoration after power loss or forced termination, or a promise of zero latency. See [startup and shutdown behavior](background-startup.md).

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

`RELEASE_VERSION.json` records the source version. Building checks the version embedded in the app, keyboard monitor and diagnostic executable. A complete `--with-controller` build records the verified source manifest and all four executable hashes in `BUILD-RECEIPT.json`. Packaging checks that receipt and rejects stale sources or executables. `BUILD-INFO.json` in the Windows download records the same source identity and signature status. These records do not substitute for a code signature.

Maintainers can create both archives after building and updating the source checksums:

```powershell
./scripts/Write-Checksums.ps1
./Build.bat --with-controller --no-pause
./scripts/Package-Release.ps1
```

The packager never overwrites an existing release directory and excludes personal `data/` and backups from source manifests. It does not publish, sign, install or start the application.

## Run tests

```powershell
.\tests\Test-AppUi.ps1 -KeepArtifacts
.\tests\Test-VisualKeyboard.ps1
.\tests\Test-RgbLifecycle.ps1 -KeepArtifacts
.\tests\Test-MultiControllerSession.ps1
.\tests\Test-MappingSession.ps1
.\tests\Test-MultiMappingIntegration.ps1
.\tests\Test-IsolatedOutput.ps1
.\tests\Test-ControllerReconnectStore.ps1
.\Test-All.ps1
```

These ordinary suites cover pure calculations, synthetic input sources, synthetic helper processes, Windows calls and offscreen UI controls. They do not install a driver or create a real virtual controller. Separate opt-in live acceptance tests create actual devices and are excluded from the offline GitHub workflow. Windows desktop support is required for the UI suites. Record failed or blocked suites accurately; see [the recorded validation and its limits](status.md).
