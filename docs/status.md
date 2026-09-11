# Compatibility and validation

Development preview **0.1.0-preview.2**, status: **11 September 2026**. [User guide](user-guide.md) · [Build instructions](building.md)

The release offers an unsigned Windows x64 package and a separate complete source package. Both include file checksums; the release also provides hashes for the ZIP downloads. See [package contents and verification](building.md#what-the-download-contains).

## Current known issue

Windows application control blocked the latest optimized output-helper executable on the development machine at process startup (Code Integrity event 3077). The helper compiled and its Go package tests passed; it did not run for a new live acceptance or resource measurement. No protection settings were changed and no alternate-host retry was used.

The successful two-Xbox/two-DualSense observations below apply to the preceding helper build. They must not be read as a successful live test of the final optimized executable. A 32-device live test has not been completed. See [the detailed acceptance record](multi-controller-acceptance.md).

## Supported scope

| Area | Current implementation and evidence | Limit |
| --- | --- | --- |
| Platform | Native Windows x64 application using .NET Framework 4.x and WinForms. | No complete Windows-version, display-scaling or accessibility compatibility matrix. |
| Keyboard input | Wired TK75 TMR protocol; model 3591 ISO input was observed on hardware. The tested cable device used VID `3151`, PID `5030`. | Shared VID/PID alone does not prove a model. Other keyboards, wireless operation and every firmware are not validated. |
| Layouts | Manufacturer-derived model 3590 ANSI and 3591 ISO layouts, with separate QWERTY/QWERTZ legends. | An illustrated key does not establish analog reports for it. Fn, knob and special-key pressure support is not fully verified. |
| Pressure processing | Per-key defaults, optional calibration, response curves, Rapid Trigger and opposite-key handling. | The default raw range 0–385 is an estimate. It is not a measured travel distance or factory calibration. |
| Xbox output | VIIPER-derived helper with usbip-win2 0.9.8.0; distinct Windows XInput packets were verified with two Xbox and two DualSense devices on a preceding build. | Windows exposes at most four XInput controllers, including physical ones. The final optimized helper is not live-validated. |
| DualSense output | Separate Windows HID devices, common mapped buttons, triggers and sticks; neutral enumeration/readback, distinct packets and peer-preserving removal passed on the preceding mixed-device build. | Does not consume XInput slots. No PS5-console, touchpad, gyro or other extended-feature guarantee; game compatibility remains unverified. |
| ViGEm | Retained adapter code. | Activation is disabled because of the documented nonneutral startup behavior. It is not the active Xbox backend. |
| Lighting | Backups, guarded writes, supported static backgrounds and restore workflow. Hardware update/restore cycles were confirmed on the tested keyboard. | Unsupported animated backgrounds are rejected. A changed onboard profile or externally changed state can require recovery. |

Profiles offer **32 output-capable slots in total**, each freely assigned to Xbox or DualSense, subject to the Xbox/XInput limit. This is the software's supported configuration limit; the recorded mixed-device acceptance used **two Xbox plus two DualSense**, not 32 connected devices. Larger configurations remain to be load-tested.

## Recorded automated checks

The latest recorded targeted development runs passed:

| Suite | Assertions | What it exercises |
| --- | ---: | --- |
| [App UI](../tests/Test-AppUi.ps1) | 6,513 | Real controls with synthetic sources: tabs, fixed layout, bulk edits, keyboard/controller drag images, connection gestures and aborts. |
| [Visual keyboard](../tests/Test-VisualKeyboard.ps1) | 1,407 | Layouts, geometry, selection, accessibility and own-control rendering. |
| [RGB lifecycle](../tests/Test-RgbLifecycle.ps1) | 143 | Real app worker and ReaderSession with a simulated device: restore during writes, failed confirmations, queue coalescing, deadlines, retry limits and immutable backups. |
| [Multiple controller sessions](../tests/Test-MultiControllerSession.ps1) | 125 | Per-slot selection, capacity, failures and independent neutral/removal phases with synthetic endpoints. |
| [Mapping sessions](../tests/Test-MappingSession.ps1) | 293 | Real mapping workers with fake inputs/outputs, startup release gates, two-phase cleanup and inactive/active preview cadence. |
| [Multiple mapping workers](../tests/Test-MultiMappingIntegration.ps1) | 78 | Real coordinator and workers, independent Xbox/DualSense routing, shared keys, and neutralizing peers while one Submit is blocked. |
| [Isolated output](../tests/Test-IsolatedOutput.ps1) | 22 protocol + 59 process | Memory-stream protocol cases and actual isolated-process boundaries using only synthetic helper executables. |

The UI checks include original-size key and controller pixels, transparent contours, pickup anchors, the passive layered preview window and shared drag feedback. Screenshots were inspected in English. Tests cover Xbox and PS5-style front buttons, triggers, directional controls and stick segments.

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
- There is no measured whole-app CPU/memory budget or end-to-end latency guarantee. Background-work reductions are implementation changes, not published benchmark claims.
- The Windows preview and local builds are unsigned development builds. Windows decides whether a particular executable may run.

The packages exclude personal profiles, private device captures and private diagnostic logs. The source package includes sanitized pressure-report fixtures with documented provenance. New compatibility claims should include a clearly described test setup and distinguish UI tests from physical device observations.
