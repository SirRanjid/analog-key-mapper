# User guide

[Download and build](building.md) · [Compatibility and tests](status.md) · [Back to the project](../README.md)

## Open the Windows application

Download the **Windows x64 ZIP** from [Releases](https://github.com/SirRanjid/analog-key-mapper/releases), extract it, and open the `AnalogKeyMapper` folder. Run `Verify-Checksums.bat`, then open `AnalogKeyMapper.exe`. The editor requires Windows x64 and .NET Framework 4.x; it does not require a compiler or installer.

The release candidates are unsigned, and Windows may block them under your security policy. Keep the actual error if that happens; do not disable protections. If you prefer to compile it yourself, use the separate source ZIP and [build instructions](building.md#compile-from-source).

## Connect the keyboard

Connect a supported TK75 TMR by USB and open `AnalogKeyMapper.exe`. A single clearly identified supported device is selected automatically. If several devices are available, choose yours at the top and use **Connect**. Read the connection status if it fails.

Press a key and look for changing fill inside its keycap. Detecting a device is separate from receiving pressure data. Known manufacturer key indices already have labels; the menu offers learning for missing or custom identification.

Choose the physical **ANSI/ISO** layout and **QWERTY/QWERTZ** legends separately if needed. English is the default UI language; German is available in the menu.

Each key can have its own pressure range, initially **0–385**. In **Keys**, drag the **Min** and **Max** handles to adjust every selected key together. Unselected keys keep their ranges. **Scale max** is shared by the physical keyboard; press Enter or leave the field to save it. The scale cannot be reduced below an existing key's maximum, so changing it never silently clips another key's range.

To measure the range, select one or more keys and choose **Calibrate** beside the slider. Fully press one selected key once, then release it. Its measured minimum and maximum are saved automatically for the selected keys. A reading above the previous limit expands **Scale max** for the keyboard. **Cancel**, changing the selection, leaving Keys, hiding or minimizing the window, or disconnecting discards an unfinished measurement. There is no second cycle or confirmation checkbox. Ranges are saved separately for each physical keyboard; existing saved ranges remain the fallback until you edit a key. The default range is not a factory measurement of your individual keyboard.

## Select and map keys

- Click a key to select it and open **Keys**. **Ctrl+click** adds or removes keys. Holding the mouse button keeps the current tab until you either release it or begin dragging.
- Drag a rectangle from empty keyboard space to select the keys it intersects. Hold **Ctrl** or **Shift** to add that rectangle to your current selection.
- Choose **Player / controller** above the keyboard. New mappings belong to that slot.

Drag selected keys onto a controller output, or drag a controller output onto a keyboard key. When dropping onto a selected key, the current key selection can receive the mapping together. The **Controller** tab opens automatically when a key drag begins. You can also use the target selector and **Add output** in **Keys**.

The actual grabbed shape follows your pointer with about 50% opacity. Multiple keys retain their original arrangement. Available destinations are light blue; a valid current destination has a dashed amber border. An invalid drop adds nothing. Press **Escape** to cancel.

A key can have several outputs, and a drop adds only missing pairs. Repeating an existing key/output/controller pairing keeps its settings and does not duplicate it. **Undo/Redo** reverses or reapplies edits.

The controller preview displays assigned keyboard keys beside their outputs. Modifier keycaps spell out **Shift**, **Ctrl/Strg** or **Alt/AltGr**, with **L** or **R** to identify the side. A combined **L/R** label shows both sides of the same modifier; an additional count indicates further assignments. Hover an output to read the complete key list.

![Controller view with selection, lighting and connection controls](images/controller.png)

## Connect a virtual controller

The two controller selectors stay synchronized. Add or rename slots in **Controller** and choose **Xbox 360** or **DualSense** for each. This selects the actual virtual-device type as well as its presentation. Selecting a slot changes the editing context; it does not activate output.

Once the [output dependencies](building.md#virtual-controller-output) are installed, use the slot's illustrated USB connector:

- Click its plug or socket to connect or disconnect.
- Slide the plug in to connect, or pull it out to disconnect.
- Use the small footer connector to operate a slot without changing your selection.
- Use **All off** to neutralize all outputs before their devices are removed.

Profiles support **32 output-capable slots in total**, freely assigned to Xbox or DualSense. Connect each slot you want to use; disconnecting one leaves the others connected. Xbox output is limited by Windows to **four XInput controllers in total, including physical controllers**. DualSense uses a separate HID path and does not consume that four-slot allowance.

The recorded mixed-device test is **two Xbox plus two DualSense controllers** on a preceding helper build. Windows blocked the optimized helper's recorded live-validation attempt; see [release-candidate limits](status.md#release-candidate-limits). The 32-slot software limit is not a completed load test of every possible configuration. See [the acceptance record](multi-controller-acceptance.md). DualSense supports the common mapped buttons, triggers and sticks; this does not promise PS5-console compatibility, touchpad or motion-sensor support.

**Keyboard mode** keeps connected controller output neutral. Controller mode uses your mappings. The USB connection and input mode are separate controls. Open **Controller input & shortcuts** to set the profile's default mode and optional mode/stop shortcuts. F9 and F8 are editable defaults. Release keys held during connection before using them for output.

## Tune response and key behavior

**Keys**, **Curve** and **Controller** occupy the same right-hand area. Switching tabs keeps the keyboard's size unchanged. Focus a tab and use Left/Right or Home/End to switch it.

Use **Keys** for pressure ranges, assignments and opposite-key handling. Use **Curve** for advanced pressure behavior and output response. Its square graph fills the space beside two vertical controls, with compact setting rows underneath. Narrow windows stack these areas so the controls remain usable.

The single shape selector at the top changes only the shape. **Presets** contains complete response presets, which also replace the other output settings. Preset curves remain editable after applying them; adjusting your selected mappings does not overwrite the saved preset definition. The numeric list has no duplicate shape selector.

![Square curve editor showing mixed settings across selected mappings](images/response-curve.png)

*When selected mappings have different curves, the editor marks them as mixed and previews the first mapping.*

With several keys selected, signal settings, curves and presets apply to all their mappings in the selected controller. Unselected keys and other controllers retain their settings. Select a single key when you want to work on an individual mapping.

The output list in **Keys** outlines its selected row even when the list has only one entry. Its **Response** column summarizes each output's curve and changed signal settings. Hover for a short reminder, or open **Curve** to inspect and edit the values. The line below the buttons separately summarizes behavior shared by all outputs of the physical key.

![Keys tab with individual pressure ranges, outlined output rows and response summaries](images/mapping.png)

The vertical controls in **Curve** configure physical-key actuation and Rapid Trigger. **Keys → Opposite keys** configures SOCD. These settings belong to physical keys and are shared by their mappings. Mixed values are marked; editing one field preserves the others. With two selected keys, you can pair them explicitly and choose neutral, first-pressed or last-pressed resolution. Larger selections retain existing pairs and do not offer pair editing.

The two vertical controls adjust **Actuation** and **Release**. Actuation sets the initial press point and, with Rapid Trigger enabled, the travel needed to press again after release. Release sets how far to lift from the deepest press before the key becomes inactive. There is no separate Repress setting. Older profiles can retain that stored field for compatibility, but rc.8 uses Actuation for the renewed press.

The scale is a percentage of each key's pressure range, not a measured distance in millimeters. Faint keycap annotations show configured values on regular keys as well as wider ones. Hover a key for its complete explanation. A key with no additional actuation setting continues to follow its pressure range.

Both controls have **Calibrate**: press one selected key as far as desired, then release it completely to accept the measured value for the selection. Actuation calibration also sets the renewed press distance used by Rapid Trigger; Release calibration sets its release movement. This changes only that behavior setting; it does not recalibrate the key's Min/Max range or the keyboard scale.

Changing tabs, hiding the window to the tray or minimizing cancels an unfinished threshold recording. Returning to the editor does not silently start recording again.

Compact keycap footers mark configured keys: `↕25` is a fixed 25% input gate, `↓18↑3` combines actuation and relative Rapid Trigger release, `●35` is a digital output threshold, and `→10–80` is an analog output range. `→…` marks differing mapped responses; `R12–330` identifies a raw pressure range when that is the key's only custom setting. Hover for exact values, units and all outputs. Unconfigured keys remain uncluttered.

![Actuation and release controls with a Calibrate button for each option](images/key-behavior.png)

In **Curve**, compact rows pair sliders with editable numbers for deadzones, output range, scale, smoothing and the other response options. The value appears once in its numeric column. A drag previews the response and becomes one undoable edit when released. The normal **Response** view includes deadzones, gain, output deadzone and output limits. The solid line shows pressing; the dashed line shows releasing when hysteresis changes the response. Shading and dotted guides explain the selected setting; they are not draggable points.

Pressure-range, threshold and curve-setting sliders use the same fine adjustment: grab a handle, then move the pointer away from the track while continuing the drag. Move sideways from a vertical slider, or up/down from a horizontal slider. Further away means smaller changes along the slider; moving away alone does not jump the value. Return toward the track for faster adjustments. **Escape** cancels the gesture.

The vertical **IN** rail beside the graph sets the usable input span between the two deadzones: released at the top, full pressure at the bottom. **OUT** sets minimum active and maximum output, with 100% at the top to match the output axis. Drag either rectangular handle, or focus a rail, choose a handle with Left/Right or Space, and adjust it with Up/Down. These are the same values shown in the list; all selected mappings update together and their dependent limits stay valid. The rails appear in Response and Edit shape. Movement and time illustrations hide them to keep their axes unambiguous; use Response to return to the ranges.

Use **Edit shape** to create and drag actual custom or Bézier points and handles. Built-in curves show fitted Bézier points following their current shape, so they can be edited too. Merely opening this view keeps the stored curve unchanged. A completed point or handle edit applies the editable curve to the selected mappings as one undoable change. Choosing **Smooth (Bézier)** directly in the shape selector also converts the selection; each mapping starts from its own existing shape. Fitting approximates analytic curves within a checked point budget. If an extreme imported shape cannot be represented accurately, the original response remains available and the app asks you to reduce its curvature before editing it as Bézier. Saved preset definitions remain unchanged.

The button switches back to **Response** to inspect the result of all output settings. The graph, its points, range rails and view button have no hover tooltips. Other setting hints appear after a short pause, stay compact and are suppressed while dragging or editing. Full instructions remain in accessible descriptions.

Input actuation has its own guide; Rapid Trigger movement is relative to the last peak or valley, so its illustration is a labeled example rather than a fixed release point. Smoothing uses a time-response illustration because it adds delay without changing the settled pressure curve. These previews update on changes and add no background animation loop.

To capture an opposite key, select the first key and choose **Keys → Opposite keys**, then click **Capture**. Click the desired opposite key on the keyboard illustration. This pairs both keys, keeps the original key's SOCD policy and preserves their actuation settings. Existing partners are unpaired; **Undo** restores the previous pairings. **Cancel** or **Escape** cancels capture. Dragging a key continues to open **Controller** for normal output mapping.

Choosing the opposite key or resolution policy in the dropdowns also applies immediately. Each choice has its own undo step; there is no need to change tabs to activate it.

![Opposite-key capture waiting for a keyboard selection, with Cancel available](images/socd-opposite-drop.png)

## Profiles and saved data

The menu offers new, duplicate and renamed profiles, JSON import/export, signal presets and application-specific profile rules. **Ctrl+S** saves the current profile.

The app stores local data under `data/` beside its executable. Keep that folder when updating or moving your setup. Exporting a profile does not include all local calibration, learned key maps or lighting backups. Controller names identify slots inside the app; they do not rename Windows devices.

## Optional keyboard lighting

Choose a controller color and enable **Key colors** in **Controller**. Mapped keys of connected controllers are colored in controller mode. Choosing a color alone does not enable lighting. A separate option in the shortcut settings marks the mode-switch key; that marker can remain visible in keyboard mode or with controllers disconnected.

When a key belongs to several connected controllers, the first controller in profile order supplies its color. Disconnecting that controller lets the next connected owner supply the color.

The app saves the keyboard's current lighting before changing it and keeps restoration records under `data/lighting/`. Supported static backgrounds are preserved on other keys. Animated effects that cannot be preserved reliably are rejected before writing.

**Restore lighting** returns to the saved normal state and turns off both controller colors and the shortcut marker for the profile. On exit, the app restores the lighting captured from the keyboard at startup, including its colors, brightness and effect. The keyboard helper also attempts restoration if the app connection is interrupted. A closing window shows progress through saving, controller cleanup, lighting restoration and helper shutdown. During Windows shutdown, the app registers a reason and waits for its cleanup work, with a 25-second total budget. Original backups remain available if the keyboard becomes unavailable or cleanup fails. Forced termination, disconnected hardware or power loss can still prevent restoration.

If recovery pauses, keep the backups and follow the displayed reason. An onboard keyboard-profile change may require returning to the previous onboard profile before restoration. Onboard keyboard profiles and the mapper's JSON profiles are different settings.

## Background startup

Use the optional **Start with Windows · in tray** setting and the separate **Reconnect controllers at startup** option. **Minimize to tray** is a saved checkbox: when enabled, the window's **X** hides the editor and keeps controllers running. Tray **Exit**, or **X** with the option disabled, closes the app and restores lighting. Reconnection starts off and only uses a confirmed previous session. See [background startup and tray controls](background-startup.md).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| No changing pressure values | USB connection, selected device and keyboard-helper status. Avoid another configurator changing the monitor or onboard profile during the session. |
| App/helper cannot start | Read the actual Windows or signature error. The local unsigned-helper build does not override Windows policy. See [building](building.md). |
| Controller connection fails | Confirm `ViiperOutputHost.exe` and the compatible USB/IP setup. For Xbox, count physical controllers toward the four XInput slots. Read the error for the affected slot. |
| Controller stays neutral | Check connection, mode, enabled mappings and fresh key input. Release keys held during startup. The preview alone does not prove game output. |
| Colors do not change | Check **Key colors**, controller connection/mode, the separate marker option and the lighting status. |
| Lighting reports a path that is too long | Exit and move the complete app folder, including `data/`, to a shorter writable path. Backup names retain the full device identity; the app checks all later journal paths before reading the lighting backup. |
| Calibration changes after a USB-port move | Without a serial number, calibration may be associated with the Windows device path. The default range is used until suitable calibration is available. |
| Keyboard and controller input both reach a game | Optional **Controller input only** suppression has its own switch and starts off after an app restart. It affects the selected positions across all keyboards, and Raw Input games may still see them. |

For remaining hardware and game limitations, see [current status](status.md).
