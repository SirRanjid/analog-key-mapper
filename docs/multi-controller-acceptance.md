# Mixed-controller acceptance

**0.1.0-preview.2 · 11 September 2026**. [Current status](status.md) · [User guide](user-guide.md) · [Build instructions](building.md)

The implementation permits 32 output slots in total, freely assigned to Xbox 360 or DualSense. Windows limits XInput to four controllers, including physical controllers. DualSense devices use the Windows HID path and do not count toward that XInput limit. The recorded live configuration is **two Xbox plus two DualSense devices**. Larger configurations have not completed load validation.

## Which build was tested

The mixed-device results below apply to the preceding output-helper build, whose SHA-256 starts with `A135`. After a USB polling change, the new helper compiled and passed its Go package tests, but Windows application control blocked it at startup on the development machine. Its SHA-256 starts with `27719395`; Code Integrity recorded event 3077.

Consequently, the latest optimized executable has **not** completed live validation. No new resource result or 32-device acceptance is claimed. No protection policy was changed, and the blocked executable was not retried through another host. The earlier result remains evidence for the earlier build only.

## Device-level acceptance

The controlled Windows x64 run used the installed usbip-win2 0.9.8.0 driver and the real VIIPER-derived output backend:

1. A DualSense probe enumerated as a Windows HID device, returned neutral reports over a half-second observation and was removed successfully.
2. Two Xbox and two DualSense outputs were connected. Each received different button, trigger and stick values.
3. Fresh Windows XInput/HID readback after Submit confirmed the expected values, including after adding peers.
4. One DualSense and one Xbox device were removed separately. The surviving devices retained their own values.
5. The remaining owned devices were neutralized and removed; the starting XInput baseline was restored.

The [opt-in device acceptance test](../src/ViiperOutputHost/cmd/tk75-output-host/live_acceptance_windows_test.go) passed. Its local evidence identifier is `multi-controller-native-port-20260911.txt`; private machine captures are not included in the public package.

This verifies the sampled common controls and normal lifecycle on that setup. It is not a complete mapping matrix, game compatibility test or PS5-console test. Touchpad, motion sensors, adaptive effects and other DualSense extras are not guaranteed.

## Separate helper-process acceptance

A second run exercised the real C# isolated-output protocol against four real helper processes: two Xbox and two DualSense. It passed **163 checks**, including independent watchdog acknowledgements, a ten-second connected period, removing individual peers, parallel neutralization, normal QUIT and confirmed exit of the owned processes. Remaining helper pumps continued acknowledging commands after a peer was removed.

The local evidence identifier is `multi-controller-processes-4-20260911.txt`. This process-boundary run verifies acknowledgements and lifecycle; packet contents were checked by the separate device-level test above. It does not turn the final blocked helper into a validated build.

## How each device is identified

Each output retains a separate helper process, private listener and native USB/IP attachment identity. Attachment operations keep the exact assigned positive port, location and generated identity. Cleanup targets that owned attachment rather than detaching all devices.

For DualSense readback, the native attachment is first revalidated against its retained USB/IP identity. HID discovery then matches the exact USB host-controller ancestry and positive hub port. It does not select a device merely by its Sony vendor/product IDs or by a presumed HID serial string. This matters because usbip-win2/UDE can expose port-derived PnP identities without a usable HID serial string.

The implementation is in [native attachment ownership](../src/ViiperOutputHost/cmd/tk75-output-host/native_windows.go) and [owned HID discovery](../src/ViiperOutputHost/cmd/tk75-output-host/dualsense_hid_windows.go).

## Synthetic regression boundaries

These suites create no real virtual devices and are included in the offline GitHub workflow:

| Suite | Passed checks | Boundary |
| --- | ---: | --- |
| [MultiControllerSession](../tests/Test-MultiControllerSession.ps1) | 125 | Selection, limits, separate routes, partial failures, and neutralization before slow removal. |
| [MappingSession](../tests/Test-MappingSession.ps1) | 293 | Real worker with fake input/output: startup release, stale input, two-phase shutdown, and active/inactive polling. |
| [MultiMappingIntegration](../tests/Test-MultiMappingIntegration.ps1) | 78 | Real coordinator and mapping workers with fake outputs: mixed types, shared keys, independent frames and neutral peers while one Submit is blocked. |
| [IsolatedOutput](../tests/Test-IsolatedOutput.ps1) | 22 + 59 | Protocol checks plus real child-process/pipe/deadline handling using synthetic helpers. |

All-off first detaches the worker outputs, runs independent neutralization attempts, and only then starts the removal phase. These checks establish that a blocked output cannot postpone another output's neutralization. Successful normal lifecycle tests do not establish cleanup after every crash, forced termination, cable pull or driver failure.
