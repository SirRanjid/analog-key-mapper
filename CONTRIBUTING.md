# Contributing

Thank you for helping improve Analog Key Mapper. Small, focused changes and reproducible reports are especially useful while input and output compatibility are still being validated.

## Report a problem

Open an [issue](https://github.com/SirRanjid/analog-key-mapper/issues) with:

- The version or commit, Windows version and whether you used the Windows download, a local build or a strict build.
- Keyboard model, wired/wireless connection and controller backend, where relevant. Distinguish a displayed firmware value from a verified firmware version.
- Steps to reproduce, expected behavior and the exact error text.
- Whether the problem concerns the drawing, recorded input values, Windows controller output or a particular game.

For layout issues, include window size/display scaling and a screenshot without personal information. Share only the smallest relevant sanitized profile or log excerpt. Do not upload your whole `data/` folder, raw device recordings, device paths, serial numbers, account information or private application rules.

## Build and test

Follow [the build guide](docs/building.md). `Build.bat` builds locally; `Build.bat --with-controller` adds the optional Go helper. Neither installs a driver or starts a controller.

Run the relevant existing tests for your change and report the exact result. For example:

```powershell
.\tests\Test-AppUi.ps1 -KeepArtifacts
.\tests\Test-VisualKeyboard.ps1
.\tests\Test-RgbLifecycle.ps1 -KeepArtifacts
```

`Test-All.ps1` runs the available suites. Some use offscreen WinForms or native Windows APIs with synthetic sources. Report Windows policy blocks and other failures accurately; do not bypass protection settings to make a test appear successful. Do not claim hardware verification for synthetic inputs or rendered previews.

## Submit a change

Keep changes focused on one behavior. Explain the problem, resulting behavior, relevant test results and remaining limits. Include before/after screenshots for visual changes and preserve keyboard/controller geometry during context switches.

Preserve existing user profiles, calibration and lighting backups. Maintain explicit neutral-start/stop behavior and the current output availability checks. Input mappings should come from physical input; this project does not implement macros, automated sequences or game injection.

Do not commit build outputs, local `data/`, raw captures, credentials or machine-specific diagnostics. The source checksum manifest describes a packaged snapshot and will change when sources change. After reviewing your intentional edits, refresh it with `./scripts/Write-Checksums.ps1` before using the BAT build or submitting a change. Do not refresh an unexplained download mismatch to make it pass.

Maintainers can build both components and run `./scripts/Package-Preview.ps1 -Version '0.1.0-preview.2'` to create Windows/source ZIPs and ZIP checksums under `release/`. The package script requires matching source/build manifests, keeps user data out, and never publishes or signs automatically.

The original mapper code uses the [MIT License](LICENSE). Third-party source, the optional VIIPER-derived helper and cursor assets retain their own licenses. Keep their notices intact and identify any new dependency or copied material. Contributions to derived helper code must respect [its licensing](src/ViiperOutputHost/NOTICE.md).

The project was developed with ChatGPT and iterative user feedback. Human review, clear provenance and reproducible tests remain part of the contribution process.
