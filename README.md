# Analog Key Mapper

**Turn GamaKay TK75 TMR key pressure into controller input — with visual mapping, editable response curves and optional key lighting.**

**[Download Windows preview](https://github.com/SirRanjid/analog-key-mapper/releases)** · [Build from source](docs/building.md#compile-from-source) · [User guide](docs/user-guide.md) · [Compatibility and test status](docs/status.md)

![Analog Key Mapper showing keyboard mappings, controller selection, lighting and USB connection controls](docs/images/controller.png)

## Make each key work your way

- **Map by dragging in either direction.** Move a key onto a controller output, or an output onto a key. The actual grabbed shape follows your pointer.
- **Tune the response.** Edit deadzones, activation thresholds and linear, custom or Bézier curves in a square editor.
- **Edit several keys together.** Use Ctrl+click or a selection rectangle, apply shared settings, and undo changes in one step.
- **See what is happening.** Live pressure fills the keycaps; controller previews and numbered badges show your mappings.
- **Keep different setups.** Save JSON profiles, signal presets and named controller slots. English and German are included.
- **Add optional lighting.** Choose controller colors for mapped keys, with a backup and restore workflow for normal keyboard lighting.

## Quick start

1. [Download the Windows ZIP from Releases](https://github.com/SirRanjid/analog-key-mapper/releases) and extract it into a writable folder.
2. Double-click **`Verify-Checksums.bat`** to check the included files.
3. Open **`AnalogKeyMapper.exe`** inside the extracted `AnalogKeyMapper` folder, connect your keyboard by USB, and create a mapping.

The editor needs Windows x64 and .NET Framework 4.x; no installer or compiler is required for this download. The Xbox helper is included, but actual virtual-controller output also needs the separate compatible USB/IP driver. Follow [the output setup instructions](docs/building.md#optional-xbox-controller-output).

This is an **unsigned development preview**. Windows may block it under your security policy. Checksums verify file integrity; they are not a code signature. Do not disable protections to run it.

Prefer to compile it yourself? Download the separate **source ZIP**, verify its checksums, and run **`Build.bat`**. The source package includes the controller-helper source and its vendored dependencies; Go is needed only when building that helper. See [build instructions](docs/building.md).

## Development preview

Wired TK75 TMR model 3591 input and experimental **single-controller Xbox 360 output** have been observed on the documented test setup. ANSI layouts and PS5-style editing are included; real DualSense output is not enabled. Other keyboards, wireless input, every game and all failure cases are not yet verified. A live preview is not proof of game compatibility. [Read the current limits](docs/status.md).

<details>
<summary>Key settings and response-curve views</summary>

![Keyboard assignments and the Keys tab](docs/images/mapping.png)

![Square response-curve editor beside the unchanged keyboard layout](docs/images/response-curve.png)

</details>

Screenshots use example data; no hardware is connected in these images.

## Contributing and license

Bug reports, reproducible tests and focused improvements are welcome. See [Contributing](CONTRIBUTING.md).

The original mapper code is available under the [MIT License](LICENSE). The optional VIIPER-derived output helper and other third-party components retain their own licenses; see [the helper notice](src/ViiperOutputHost/NOTICE.md) and [cursor license](src/App/Assets/Cursors/LICENSE).

Developed with **ChatGPT**, with iterative testing and user feedback.
