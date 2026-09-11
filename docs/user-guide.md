# User guide

[Download and build](building.md) · [Compatibility and tests](status.md) · [Back to the project](../README.md)

## Open the Windows preview

Download the **Windows x64 ZIP** from [Releases](https://github.com/SirRanjid/analog-key-mapper/releases), extract it, and open the `AnalogKeyMapper` folder. Run `Verify-Checksums.bat`, then open `AnalogKeyMapper.exe`. The editor requires Windows x64 and .NET Framework 4.x; it does not require a compiler or installer.

This is an unsigned development preview, and Windows may block it under your security policy. Keep the actual error if that happens; do not disable protections. If you prefer to compile it yourself, use the separate source ZIP and [build instructions](building.md#compile-from-source).

## Connect the keyboard

Connect a supported TK75 TMR by USB and open `AnalogKeyMapper.exe`. A single clearly identified supported device is selected automatically. If several devices are available, choose yours at the top and use **Connect**. Read the connection status if it fails.

Press a key and look for changing fill inside its keycap. Detecting a device is separate from receiving pressure data. Known manufacturer key indices already have labels; the menu offers learning for missing or custom identification.

Choose the physical **ANSI/ISO** layout and **QWERTY/QWERTZ** legends separately if needed. English is the default UI language; German is available in the menu.

Uncalibrated keys use the default raw range **0–385**. **Adjust pressure range** opens optional per-key calibration. Follow the dialog, press slowly to the end and release fully. The default range and any millimetre estimate are not factory measurements of your individual keyboard.

## Select and map keys

- Click a key to select it. **Ctrl+click** adds or removes keys.
- Drag a rectangle from empty keyboard space to select the keys it intersects. Hold **Ctrl** or **Shift** to add that rectangle to your current selection.
- Choose **Player / controller** above the keyboard. New mappings belong to that slot.

Drag selected keys onto a controller output, or drag a controller output onto a keyboard key. When dropping onto a selected key, the current key selection can receive the mapping together. The **Controller** tab opens automatically when a key drag begins. You can also use the target selector and **Add output** in **Keys**.

The actual grabbed shape follows your pointer with about 50% opacity. Multiple keys retain their original arrangement. Available destinations are light blue; a valid current destination has a dashed amber border. An invalid drop adds nothing. Press **Escape** to cancel.

A key can have several outputs, and a drop adds only missing pairs. Repeating an existing key/output/controller pairing keeps its settings and does not duplicate it. **Undo/Redo** reverses or reapplies edits.

![Controller view with selection, lighting and connection controls](images/controller.png)

## Connect a virtual controller

The two controller selectors stay synchronized. Add or rename logical slots in **Controller** and choose their Xbox or PS5 presentation. Selecting a slot changes the editing context; it does not activate output.

Once the [optional output dependencies](building.md#optional-xbox-controller-output) are installed, use the slot's illustrated USB connector:

- Click its plug or socket to connect or disconnect.
- Slide the plug in to connect, or pull it out to disconnect.
- Use the small footer connector to operate a slot without changing your selection.
- Use **All off** to disconnect output.

Profiles support up to 32 logical slots; the current backend permits **one connected Xbox controller**. Real DualSense output is not enabled, even though its layout and labels are available for editing.

**Keyboard mode** keeps connected controller output neutral. Controller mode uses your mappings. The USB connection and input mode are separate controls. Open **Controller input & shortcuts** to set the profile's default mode and optional mode/stop shortcuts. F9 and F8 are editable defaults. Release keys held during connection before using them for output.

## Tune response and key behavior

**Keys**, **Curve** and **Controller** occupy the same right-hand area. Switching tabs keeps the keyboard's size unchanged. Focus a tab and use Left/Right or Home/End to switch it.

In **Curve**, edit deadzones, activation thresholds, minimum/maximum output, smoothing and response shape. Linear, custom segmented and Bézier curves are available. The curve editor remains square.

With several keys selected, signal settings, curves and presets apply to all their mappings in the selected controller. Unselected keys and other controllers retain their settings. Select a single key when you want to work on an individual mapping.

![Square response-curve editor](images/response-curve.png)

**Behavior** under Keys controls Rapid Trigger and opposite-key handling (SOCD). These belong to physical keys and are shared by their mappings. Mixed values are marked; editing one field preserves the others. With two selected keys, you can pair them explicitly and choose neutral, first-pressed or last-pressed resolution. Larger selections retain existing pairs and do not offer pair editing.

## Profiles and saved data

The menu offers new, duplicate and renamed profiles, JSON import/export, signal presets and application-specific profile rules. **Ctrl+S** saves the current profile.

The app stores local data under `data/` beside its executable. Keep that folder when updating or moving your setup. Exporting a profile does not include all local calibration, learned key maps or lighting backups. Controller names identify slots inside the app; they do not rename Windows devices.

## Optional keyboard lighting

Choose a controller color and enable **Key colors** in **Controller**. Mapped keys of connected controllers are colored in controller mode. Choosing a color alone does not enable lighting. A separate option in the shortcut settings marks the mode-switch key; that marker can remain visible in keyboard mode or with controllers disconnected.

The app saves the keyboard's current lighting before changing it and keeps restoration records under `data/lighting/`. Supported static backgrounds are preserved on other keys. Animated effects that cannot be preserved reliably are rejected before writing.

**Restore lighting** returns to the saved normal state and turns off both controller colors and the shortcut marker for the profile. Normal shutdown also requests restoration. Allow an in-progress lighting operation to finish; closing may take several seconds.

If recovery pauses, keep the backups and follow the displayed reason. An onboard keyboard-profile change may require returning to the previous onboard profile before restoration. Onboard keyboard profiles and the mapper's JSON profiles are different settings.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| No changing pressure values | USB connection, selected device and keyboard-helper status. Avoid another configurator changing the monitor or onboard profile during the session. |
| App/helper cannot start | Read the actual Windows or signature error. The local unsigned-helper build does not override Windows policy. See [building](building.md). |
| Xbox connection fails | Confirm `ViiperOutputHost.exe`, the compatible USB/IP setup and that no other slot is connected. Read the footer error. |
| Controller stays neutral | Check connection, mode, enabled mappings and fresh key input. Release keys held during startup. The preview alone does not prove game output. |
| Colors do not change | Check **Key colors**, controller connection/mode, the separate marker option and the lighting status. |
| Calibration changes after a USB-port move | Without a serial number, calibration may be associated with the Windows device path. The default range is used until suitable calibration is available. |
| Keyboard and controller input both reach a game | Optional **Controller input only** suppression has its own switch and starts off after an app restart. It affects the selected positions across all keyboards, and Raw Input games may still see them. |

For remaining hardware and game limitations, see [current status](status.md).
