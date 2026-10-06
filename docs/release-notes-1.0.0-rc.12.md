# Analog Key Mapper 1.0.0-rc.12

Remap a supported Xbox or PlayStation controller through the same visual editor used for keyboard input. This release includes the controller input mode and the automatic controller connection update in both the Windows download and the source package.

## Controller inputs

Choose **Keyboard**, **Xbox** or **PlayStation** below the left-hand picture. Controller modes show a selectable controller with its buttons, sticks and triggers. Each source mode keeps its own profile; returning to keyboard input restores its mappings and pressure calibration.

- Assign any of the 24 common inputs to any supported virtual output. Select multiple controls with Ctrl+click, drag in either direction, or edit the output list.
- Swap stick X/Y axes, invert directions, move stick half-axes onto triggers, or use buttons as exact 0/1 inputs for digital stick or trigger control.
- Apply the existing response curves, deadzones, output ranges and filters. New profiles start with matching one-to-one mappings; remove the old assignment when replacing an action.
- Choose the virtual output type independently: Xbox input can drive DualSense output, and PlayStation input can drive Xbox output. Existing multiple-controller slots and undo/redo remain available.
- Select a physical device explicitly. Saved routes retain its identity, reject recognized virtual-device ancestry and invalidate input values when the device is lost. Keyboard-only calibration, lighting and input suppression controls are hidden in controller mode.

The [controller input guide](https://github.com/SirRanjid/analog-key-mapper/blob/v1.0.0-rc.12/docs/controller-input.md) includes setup instructions, three screenshots, mapping examples and the precise supported-device scope.

## Automatic controller connections

Configured virtual controllers connect once input is ready. The preference defaults to on when no preference has been saved, while an existing off setting remains respected. Lost or failed connections retry with a delay.

Mapping, curve, calibration and profile changes preserve matching controller connections. Manual disconnect keeps the selected slot off for the session; **Turn all controllers off** suspends automatic connection for the session. New connections start neutral and require held inputs to be released before rearming. See [background startup](https://github.com/SirRanjid/analog-key-mapper/blob/v1.0.0-rc.12/docs/background-startup.md) and the [connection validation record](https://github.com/SirRanjid/analog-key-mapper/blob/v1.0.0-rc.12/docs/controller-connections-2026-10-05.md).

## Downloads and updating

Use the [Windows x64 package or complete source package](https://github.com/SirRanjid/analog-key-mapper/releases/tag/v1.0.0-rc.12). The Windows ZIP includes the application and controller helper; the separate compatible USB/IP driver is still required for virtual output. Close the mapper and its helpers, preserve your existing `data/` folder, then update the application files. Verify the included checksums. [Installation and verification](https://github.com/SirRanjid/analog-key-mapper/blob/v1.0.0-rc.12/docs/building.md).

## Validation and remaining limits

The integrated controller-input source passed [**68 offline suites** in the GitHub workflow](https://github.com/SirRanjid/analog-key-mapper/actions/runs/37487109888) for commit `40520fb72051bd9f51a411743f7a18ac05113f48`, before the rc.12 version and documentation update. Targeted local runs passed 4,836 profile, 175 routing, 237 backend, 80 source-lifecycle and 355 controller UI checks, including screenshot export; the publication checkout also passed 16,920 application UI checks. The release workflow builds the Windows application and controller helper, tests the helper and verifies both packages against the source manifest. See [validation status](https://github.com/SirRanjid/analog-key-mapper/blob/v1.0.0-rc.12/docs/status.md) for recorded evidence and build boundaries.

This remains an unsigned release candidate. Physical controller input, game compatibility and full hardware acceptance remain open. Xbox input supports the implemented XUSB 1.1 path, not Bluetooth-only HID, legacy XUSB 1.0 or additional Xbox 360 controllers sharing one receiver. Sony input covers the implemented DualShock 4 and DualSense-family USB/Bluetooth report formats. Guide/PS, touchpad, gyro, vibration and adaptive-trigger effects are outside the common mapping surface.

The physical controller remains visible to Windows, so a game can see both the physical and virtual devices. Xbox outputs also share Windows' four-XInput-controller limit with physical controllers. The screenshots use synthetic profiles and disconnected devices; they demonstrate the interface rather than hardware or game acceptance.
