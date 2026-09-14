# Compatibility and validation

Release-candidate validation, updated **14 September 2026**. [Downloads and release versions](https://github.com/SirRanjid/analog-key-mapper/releases) · [User guide](user-guide.md) · [Build instructions](building.md)

This page describes implemented features and recorded validation. Release assets identify their own version; the observations below apply only to the builds and test setups stated here. A release candidate is not a stable 1.0 acceptance claim.

The release offers an unsigned Windows x64 package and a separate complete source package. Both include file checksums; the release also provides hashes for the ZIP downloads. See [package contents and verification](building.md#what-the-download-contains).

## Release-candidate limits

The application and helper are unsigned. Windows application control has blocked earlier development executables at normal process startup (Code Integrity event 3077). See [background startup and validation](background-startup.md). Automated tests and published checksums do not establish that Windows will allow a downloaded application or helper to start.

The optimized output-helper build compiled and passed its Go package tests, but Windows blocked its recorded live-validation attempt. It therefore has no new live acceptance or resource measurement. No protection settings were changed and no alternate-host retry was used.

The successful two-Xbox/two-DualSense observations below apply to the preceding helper build. They must not be read as a successful live test of the final optimized executable. A 32-device live test has not been completed. See [the detailed acceptance record](multi-controller-acceptance.md).

## Supported scope

| Area | Current implementation and evidence | Limit |
| --- | --- | --- |
| Platform | Native Windows x64 application using .NET Framework 4.x and WinForms. | No complete Windows-version, display-scaling or accessibility compatibility matrix. |
| Keyboard input | Wired TK75 TMR protocol; model 3591 ISO input was observed on hardware. The tested cable device used VID `3151`, PID `5030`. | Shared VID/PID alone does not prove a model. Other keyboards, wireless operation and every firmware are not validated. |
| Learned inputs | Guided assignment of unresolved logical keys from standard keyboard on/off events and supported HID buttons, axes, hats and relative controls. Profiles retain source/control identity. | Native backends are implemented and synthetically tested; physical devices and games still require acceptance. Unknown vendor protocols and arbitrary wrapping counters are not decoded by learning. |
| Layouts | Manufacturer-derived model 3590 ANSI and 3591 ISO layouts, with separate QWERTY/QWERTZ legends. | An illustrated key does not establish analog reports for it. Fn, knob and special-key pressure support is not fully verified. |
| Pressure processing | Per-key min/max ranges, calibration for selected keys, a keyboard-wide scale, response curves, Rapid Trigger and opposite-key handling. | The default raw range 0–385 is an estimate. It is not a measured travel distance or factory calibration. |
| Xbox output | VIIPER-derived helper with usbip-win2 0.9.8.0; distinct Windows XInput packets were verified with two Xbox and two DualSense devices on a preceding build. | Windows exposes at most four XInput controllers, including physical ones. The final optimized helper is not live-validated. |
| DualSense output | Separate Windows HID devices, common mapped buttons, triggers and sticks; neutral enumeration/readback, distinct packets and peer-preserving removal passed on the preceding mixed-device build. | Does not consume XInput slots. No PS5-console, touchpad, gyro or other extended-feature guarantee; game compatibility remains unverified. |
| ViGEm | Retained adapter code. | Activation is disabled because of the documented nonneutral startup behavior. It is not the active Xbox backend. |
| Lighting | Backups, guarded writes, supported static backgrounds and restore workflow. rc.10 adds confirmation before cleanup of recognized leftover key colors and optional future automatic cleanup. Earlier update/restore cycles were confirmed on the tested keyboard. | New startup cleanup is synthetically tested. Identical external color patterns cannot prove ownership; ambiguous replacement colors remain unchanged. Unsupported animated backgrounds and changed onboard profiles can prevent recovery. |

Profiles offer **32 output-capable slots in total**, each freely assigned to Xbox or DualSense, subject to the Xbox/XInput limit. This is the software's supported configuration limit; the recorded mixed-device acceptance used **two Xbox plus two DualSense**, not 32 connected devices. Larger configurations remain to be load-tested.

## Recorded automated checks

The rc.11 scrollbar update passed **16,655 local application UI assertions**, including **2,318 interaction checks**, **354 rebuild checks** and **778 native theme checks**. The tests inspect existing pixels during hover and held dragging, confirm real panel/table/list/text/popup scrolling, and cover 32-bit positions, arrow/page repetition, focus, capture loss, changed ranges and handle recreation. Background/tray checks passed **205 assertions** and shutdown checks passed **101**. These are synthetic controls and inputs, without real hardware or an OS shutdown.

The rc.11 workflow retains **62 offline suites** and verifies Windows builds, controller-helper tests and source/binary packages. The [rc.11 release page](https://github.com/SirRanjid/analog-key-mapper/releases/tag/v1.0.0-rc.11) records the completed workflow for the downloadable revision. See the [rc.11 release notes](release-notes-1.0.0-rc.11.md) for the scope and remaining acceptance limits.

The rc.10 lighting and lifecycle changes passed **1,765 targeted local checks**: marker recognition 92, recovery decisions 815, lighting lifecycle 517, background UI 205, shutdown 101, saved permission 14 and the confirmation dialog 21. They cover disabled lighting options and controllers, preserved external backgrounds, matching colors on unrelated keys, ambiguous replacements, interrupted writes, damaged recovery data, onboard profile changes, one-time and saved approval, and exit while a question is open. Both dialog languages are checked. These tests use simulated keyboard access and do not restart Windows or connect a physical device.

The completed [rc.10 Windows validation](https://github.com/SirRanjid/analog-key-mapper/actions/runs/34830788024) passed **62 offline suites**, Windows builds, controller-helper tests and verified packaging. The [rc.10 release notes](release-notes-1.0.0-rc.10.md) describe its lighting and lifecycle changes. Real hardware, Windows shutdown and game acceptance remain open.

The [recorded rc.9 validation](https://github.com/SirRanjid/analog-key-mapper/actions/runs/34781600962) passed **59 offline suites**, the Windows application/helper build, helper tests and checked packaging. Input learning adds 40,895 pure capture assertions, 145 profile checks, 1,048 routing checks, 121 HID parsing checks and 777 keyboard control checks. Native UI fixtures cover the actual modal assistant, Apply/Cancel ownership, selected-key scope, generic profile pressure isolation, source disconnection and reconnect races. Screenshots use synthetic devices. Physical hardware, game compatibility and native input timing remain unverified for these new sources.

The completed [rc.8 editor validation](https://github.com/SirRanjid/analog-key-mapper/actions/runs/34778891037) passed all **54 offline suites**, including **13,806 application UI assertions**, **778 native theme checks** and **1,253 real-control precision-drag checks**. The controller-helper tests and version-checked packaging also passed. These checks use synthetic inputs and do not establish hardware or game acceptance.

The rc.8 revision uses stable geometry for the larger square graph and compact settings, preserves exact slider pickup values during fine adjustments, and converts built-in shapes to editable Bezier curves without replacing preset definitions. Repress uses the actuation amount while older profile fields remain readable. Pure checks cover fitting accuracy and refused extreme conversions, relative precision and scrollbar layout boundaries. Native scrollbar checks include handle recreation, hover, focus, visibility and delayed repaint on the Windows 2022 runner; they do not establish every Windows desktop theme.

The rc.7 revision moves advanced physical-key controls beside the compact response graph, exposes one shape selector, visualizes output processing and separates editable shape points from response guides. Numeric rows are updated in place to prevent reentrant cell transitions. Theme checks read control pixels immediately during native scrollbar updates; pure response checks compare the preview with the signal processor. The [release page](https://github.com/SirRanjid/analog-key-mapper/releases) identifies each exact build and its [automated validation](https://github.com/SirRanjid/analog-key-mapper/actions); the recorded results below remain tied to their stated versions.

The preceding rc.6 revision introduced per-key pressure ranges, selection-wide calibration, per-option threshold capture, keycap annotations and visible shutdown phases. Known RGB states need fewer verification requests; initial backup discovery still reads the complete original picture twice. rc.7 changes the editor and its previews, without increasing the background input polling rate.

The rc.4 tests add shared range persistence, automatic calibration after one press and release, editable min/max handles, opposite-key capture by click, saved close-to-tray behavior, transient restore retries and helper cleanup with fresh device identification. The release notes link the complete workflow run for the downloadable build. These checks use synthetic sources; physical shutdown and game acceptance for rc.4 remain open.

Earlier targeted runs recorded during rc.3 development passed:

| Suite | Assertions | What it exercises |
| --- | ---: | --- |
| [App UI](../tests/Test-AppUi.ps1) | 6,967 | Real controls with synthetic sources: tabs, fixed layout, bulk edits, readable modifier badges, outlined output summaries, SOCD drag pairing, keyboard/controller drag images, connection gestures and aborts. |
| [Background UI](../tests/Test-BackgroundUi.ps1) | 113 | Windowless startup, tray behavior and cancellation of startup reconnection after edits. |
| [Pending USB connection](../tests/Test-ControllerPendingUi.ps1) | 28 | Docked pending graphics, cancellation gestures, accessibility and independent footer slots. |
| [Reconnect persistence](../tests/Test-ControllerReconnectStore.ps1) | 52 | One-use confirmed records, stale files, failed writes and interrupted sessions. |
| [Visual keyboard](../tests/Test-VisualKeyboard.ps1) | 1,407 | Layouts, geometry, selection, accessibility and own-control rendering. |
| [RGB lifecycle](../tests/Test-RgbLifecycle.ps1) | 220 | Real app worker and ReaderSession with a simulated device: restore during writes, failed confirmations, deadlines, compact journal paths and recovery from unchanged older backups. |
| [Shutdown](../tests/Test-Shutdown.ps1) | 77 | Windows shutdown budget, pending connections/restoration, cancelled logoff and clean-exit confirmation. |
| [Multiple controller sessions](../tests/Test-MultiControllerSession.ps1) | 167 | Per-slot selection, pending capacity, cancellation, retired cleanup and independent neutral/removal phases with synthetic endpoints. |
| [Mapping sessions](../tests/Test-MappingSession.ps1) | 403 | Real mapping workers with fake inputs/outputs, asynchronous creation, cancellation at publication, startup release gates and inactive/active preview cadence. |
| [Multiple mapping workers](../tests/Test-MultiMappingIntegration.ps1) | 78 | Real coordinator and workers, independent Xbox/DualSense routing, shared keys, and neutralizing peers while one Submit is blocked. |
| [Isolated output](../tests/Test-IsolatedOutput.ps1) | 22 protocol + 59 process | Memory-stream protocol cases and actual isolated-process boundaries using only synthetic helper executables. |

The UI checks include original-size key and controller pixels, transparent contours, pickup anchors, the passive layered preview window and shared drag feedback. Screenshots were inspected in English. Tests cover Xbox and PS5-style front buttons, triggers, directional controls and stick segments.

The recorded UI revision includes 136 modifier-label checks, 66 output-summary checks and 147 SOCD-drag checks. Output summaries and selection outlines were also inspected in German and at minimum window size. A separate deterministic layout check covered 200 mixtures of all 24 controller targets in both styles, including 2,396 broad modifier labels, without creating a window or touching hardware. The background UI and visual-keyboard suites above also passed on this UI revision.

The earlier rc.8 offline command discovered 54 suites. The initial finalization run exposed an RGB journal path-length failure; compact filenames fixed the underlying issue, with successful follow-up checks for ordinary download paths and legacy recovery. Packaging checks separately reject stale versions, changed executables, source/receipt mismatches and unlisted source files. The [GitHub workflow](https://github.com/SirRanjid/analog-key-mapper/actions/workflows/build.yml) builds both components, runs all offline suites and the Go protocol tests, then creates verified packages.

These targeted runs use no real keyboard/controller and do not perform a complete native OLE drag. They do not establish that every suite in `Test-All.ps1` passed on every machine. A blocked or failed test must be reported as such.

## Recorded hardware observations

The tested wired model 3591 returned 2,233 valid pressure reports during one independent monitor session, including changing WASD values and releases. The monitor reported its normal stop sequence and exited successfully. This confirms that tested input path, not every possible key combination.

Separate Windows XInput observations recorded:

- A neutral-only start/removal probe and a neutral start/removal through the actual Xbox runtime route.
- Real key-driven analog output, including diagonal movement and a full observed LX range of −32768 to +32767.
- A later 90-second run without an observed controller disconnect, including a continuous nonneutral section longer than 11 seconds.
- Three confirmed lighting update/restore cycles with controller operation, followed by a separate normal app-close check whose complete lighting readback matched its startup backup.

The subsequent mixed-device acceptance on the preceding helper build verified two Xbox plus two DualSense devices together, fresh distinct packet readback, and removal of either type while surviving outputs remained unchanged. A separate four-process run passed **163 checks**, covering independent command acknowledgements, removal of individual devices, neutralization, normal QUIT and exit of the owned helper processes. This was actual virtual-device output; it was not a game test. Details and build boundaries are in [the acceptance record](multi-controller-acceptance.md).

These are bounded observations on one setup. They do not establish all button/axis mappings, every game, anti-cheat acceptance, cable-pull behavior, lost-release recovery or cleanup after a forced process termination. The difficult close-during-active-write scenarios were checked with the synthetic lifecycle harness; they are not presented as a full physical controller/RGB failure test.

## Remaining boundaries

- A moving preview is calculated app output, not evidence that a game received a virtual device.
- Each connected slot retains its own helper and exact USB/IP attachment identity. Successful ordinary disconnects do not guarantee removal after a crash, power loss or forced termination.
- Keyboard suppression uses a Windows hook without per-device identity. Selected positions therefore affect all keyboards; Raw Input games may still receive keyboard events. Suppression starts disabled after an app restart.
- Without a device serial number, calibration may be associated with a Windows device path and may need attention after a port change.
- New lighting journals require the complete `data` path to fit within 92 characters; unusually deep installation paths are rejected before the initial lighting read. Normal extracted download paths were tested. Existing backup names are retained and may need a shorter folder for recovery.
- There is no measured whole-app CPU/memory budget or end-to-end latency guarantee. Background-work reductions are implementation changes, not published benchmark claims.
- A [synthetic mapping benchmark](performance.md) covers 1, 4, 8, 16 and 32 workers. It excludes real input, helpers, the driver and games; it does not validate 32 live devices. The configured 4 ms wait produced roughly 16 ms p95 publication-to-fake-output latency on that setup.
- The Windows candidate and local builds are unsigned. Windows decides whether a particular executable may run.

The packages exclude personal profiles, private device captures and private diagnostic logs. The source package includes sanitized pressure-report fixtures with documented provenance. New compatibility claims should include a clearly described test setup and distinguish UI tests from physical device observations.
