# Analog Key Mapper

**Turn GamaKay TK75 TMR key pressure into controller input — with visual mapping, editable response curves and optional key lighting.**

**Free and open source · 1.0.0-rc.1 release candidate.** Final hardware and game acceptance is still required before stable 1.0.

**[Windows downloads](https://github.com/SirRanjid/analog-key-mapper/releases)** · [Build from source](docs/building.md#compile-from-source) · [User guide](docs/user-guide.md) · [Compatibility and test status](docs/status.md)

![Analog Key Mapper showing keyboard mappings, controller selection, lighting and USB connection controls](docs/images/controller.png)

## Make each key work your way

- **Map by dragging in either direction.** Move a key onto a controller output, or an output onto a key. The actual grabbed shape follows your pointer.
- **Use several controllers together.** Configure up to 32 Xbox 360 or DualSense slots, with separate mappings, colors and USB connection controls. Windows permits at most four XInput controllers system-wide, including physical ones; DualSense uses a separate HID path.
- **Tune the response.** Edit deadzones, activation thresholds and linear, custom or Bézier curves in a square editor.
- **Edit several keys together.** Use Ctrl+click or a selection rectangle, apply shared settings, and undo changes in one step.
- **See what is happening.** Live pressure fills the keycaps; controller previews and numbered badges show your mappings.
- **Keep different setups.** Save JSON profiles, signal presets and named controller slots. English and German are included.
- **Add optional lighting.** Choose controller colors for mapped keys, with a backup and restore workflow for normal keyboard lighting.
- **Keep the editor out of the way.** Use the tray or opt into Windows startup. Controller reconnection is a separate option that starts off. [Background startup guide](docs/background-startup.md).

## Quick start

1. [Download the Windows ZIP from Releases](https://github.com/SirRanjid/analog-key-mapper/releases) and extract it into a writable folder.
2. Double-click **`Verify-Checksums.bat`** to check the included files.
3. Open **`AnalogKeyMapper.exe`** inside the extracted `AnalogKeyMapper` folder, connect your keyboard by USB, and create a mapping.

The editor needs Windows x64 and .NET Framework 4.x; no installer or compiler is required for this download. The Xbox/DualSense helper is included, but actual virtual-controller output also needs the separate compatible USB/IP driver. Follow [the output setup instructions](docs/building.md#virtual-controller-output).

This is an **unsigned release candidate**, not stable 1.0. Windows application control may block the app or a helper. Checksums verify file integrity; they are not a code signature. If Windows blocks a file, stop that attempt and keep the error. Do not disable protections to run it.

Prefer to compile it yourself? Download the separate **source ZIP**, verify its checksums, and run **`Build.bat`**. The source package includes the controller-helper source and its vendored dependencies; Go is needed only when building that helper. See [build instructions](docs/building.md).

## Release candidate

**1.0.0-rc.1** includes these connection and lifecycle improvements:

- Controller creation runs asynchronously. Cancelled or outdated requests cannot later activate a controller after its profile, input source or mode has changed.
- Windows shutdown uses a shared cleanup time limit for controller neutralization, profile saving and lighting restoration. Unfinished lighting recovery retains its backup.
- Optional startup reconnection consumes the previous confirmed session once. A failed save or interrupted exit does not silently reuse an old controller list; the app explains when manual connection is needed.
- Compact lighting journal names support normal extracted download folders while preserving recovery from older backups.

The earlier hardware test covered **two Xbox plus two DualSense devices** and wired TK75 TMR model 3591 input. The current candidate still needs final hardware and game acceptance; its 32-slot configuration limit is not a 32-device performance claim. DualSense support covers common mapped controls. [Current compatibility and Windows startup issue](docs/status.md) · [Hardware record](docs/multi-controller-acceptance.md) · [Synthetic performance measurements](docs/performance.md).

<details>
<summary>Key settings and response-curve views</summary>

![Keyboard assignments and the Keys tab](docs/images/mapping.png)

![Square response-curve editor beside the unchanged keyboard layout](docs/images/response-curve.png)

</details>

Screenshots use example data; no hardware is connected in these images.

## Contributing and license

Bug reports, reproducible tests and focused improvements are welcome. See [Contributing](CONTRIBUTING.md).

The application is free to use. The original mapper code is available under the [MIT License](LICENSE). The VIIPER-derived output helper and other third-party components retain their own licenses; see [the helper notice](src/ViiperOutputHost/NOTICE.md) and [cursor license](src/App/Assets/Cursors/LICENSE).

Developed with **ChatGPT**, with iterative testing and user feedback.
