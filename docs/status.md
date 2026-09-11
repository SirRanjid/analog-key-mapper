# Compatibility and validation

Development preview status: **11 September 2026**. [User guide](user-guide.md) · [Build instructions](building.md)

The release offers an unsigned Windows x64 package and a separate complete source package. Both include file checksums; the release also provides hashes for the ZIP downloads. See [package contents and verification](building.md#what-the-download-contains).

## Supported scope

| Area | Current implementation and evidence | Limit |
| --- | --- | --- |
| Platform | Native Windows x64 application using .NET Framework 4.x and WinForms. | No complete Windows-version, display-scaling or accessibility compatibility matrix. |
| Keyboard input | Wired TK75 TMR protocol; model 3591 ISO input was observed on hardware. The tested cable device used VID `3151`, PID `5030`. | Shared VID/PID alone does not prove a model. Other keyboards, wireless operation and every firmware are not validated. |
| Layouts | Manufacturer-derived model 3590 ANSI and 3591 ISO layouts, with separate QWERTY/QWERTZ legends. | An illustrated key does not establish analog reports for it. Fn, knob and special-key pressure support is not fully verified. |
| Pressure processing | Per-key defaults, optional calibration, response curves, Rapid Trigger and opposite-key handling. | The default raw range 0–385 is an estimate. It is not a measured travel distance or factory calibration. |
| Xbox output | Experimental VIIPER-derived helper with usbip-win2 0.9.8.0; neutral start/removal and real analog Windows XInput changes were recorded. | One connected Xbox controller; complete target coverage, game behavior and failure recovery are still open. |
| PS5 presentation | Controller drawing, labels and mapping configuration. | Actual DualSense output is disabled pending separate Windows device acceptance. No Xbox or DS4 fallback is substituted. |
| ViGEm | Retained adapter code. | Activation is disabled because of the documented nonneutral startup behavior. It is not the active Xbox backend. |
| Lighting | Backups, guarded writes, supported static backgrounds and restore workflow. Hardware update/restore cycles were confirmed on the tested keyboard. | Unsupported animated backgrounds are rejected. A changed onboard profile or externally changed state can require recovery. |

Profiles can hold up to 32 logical controller slots; this is not a claim of 32 simultaneously connected devices. This application does not provide a PS5-console compatibility guarantee.

## Recorded automated checks

The latest recorded targeted development runs passed:

| Suite | Assertions | What it exercises |
| --- | ---: | --- |
| [App UI](../tests/Test-AppUi.ps1) | 6,513 | Real controls with synthetic sources: tabs, fixed layout, bulk edits, keyboard/controller drag images, connection gestures and aborts. |
| [Visual keyboard](../tests/Test-VisualKeyboard.ps1) | 1,407 | Layouts, geometry, selection, accessibility and own-control rendering. |
| [RGB lifecycle](../tests/Test-RgbLifecycle.ps1) | 143 | Real app worker and ReaderSession with a simulated device: restore during writes, failed confirmations, queue coalescing, deadlines, retry limits and immutable backups. |

The UI checks include original-size key and controller pixels, transparent contours, pickup anchors, the passive layered preview window and shared drag feedback. Screenshots were inspected in English. Tests cover Xbox and PS5-style front buttons, triggers, directional controls and stick segments.

These targeted runs use no real keyboard/controller and do not perform a complete native OLE drag. They do not establish that every suite in `Test-All.ps1` passed on every machine. A blocked or failed test must be reported as such.

## Recorded hardware observations

The tested wired model 3591 returned 2,233 valid pressure reports during one independent monitor session, including changing WASD values and releases. The monitor reported its normal stop sequence and exited successfully. This confirms that tested input path, not every possible key combination.

Separate Windows XInput observations recorded:

- A neutral-only start/removal probe and a neutral start/removal through the actual Xbox runtime route.
- Real key-driven analog output, including diagonal movement and a full observed LX range of −32768 to +32767.
- A later 90-second run without an observed controller disconnect, including a continuous nonneutral section longer than 11 seconds.
- Three confirmed lighting update/restore cycles with controller operation, followed by a separate normal app-close check whose complete lighting readback matched its startup backup.

These are bounded observations on one setup. They do not establish all button/axis mappings, every game, anti-cheat acceptance, cable-pull behavior, lost-release recovery or cleanup after a forced process termination. The difficult close-during-active-write scenarios were checked with the synthetic lifecycle harness; they are not presented as a full physical controller/RGB failure test.

## Remaining boundaries

- A moving preview is calculated app output, not evidence that a game received a virtual device.
- The output helper requires exclusive session ownership. Successful ordinary disconnects do not guarantee USB/IP removal after a crash or forced termination.
- Keyboard suppression uses a Windows hook without per-device identity. Selected positions therefore affect all keyboards; Raw Input games may still receive keyboard events. Suppression starts disabled after an app restart.
- Without a device serial number, calibration may be associated with a Windows device path and may need attention after a port change.
- There is no measured whole-app CPU/memory budget or end-to-end latency guarantee. Background-work reductions are implementation changes, not published benchmark claims.
- The Windows preview and local builds are unsigned development builds. Windows decides whether a particular executable may run.

The packages exclude personal profiles, private device captures and private diagnostic logs. The source package includes sanitized pressure-report fixtures with documented provenance. New compatibility claims should include a clearly described test setup and distinguish UI tests from physical device observations.
