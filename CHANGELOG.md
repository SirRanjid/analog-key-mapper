# Changelog

## 1.0.0-rc.9

Implementation prepared; full release validation is pending.

- Add **Learn unknown inputs…** to the active key selection's context menu, with sequential capture and one reviewed, undoable apply.
- Automatically identify matching standard keyboard positions; retain existing known assignments.
- Support learned routes from readable standard HID buttons, absolute axes, hats and relative controls, alongside decoded analog keyboard channels.
- Save source/control identities per profile, invalidate disconnected inputs, and add a separate **Forget learned inputs** action.
- Keep gesture analysis in the assistant and use the saved interpretation during normal mapping.

The new native input backends still require physical hardware and game acceptance. See [rc.9 release notes](docs/release-notes-1.0.0-rc.9.md) and the [input learning guide](docs/input-learning.md).

Earlier version notes and their checked packages are available on the [releases page](https://github.com/SirRanjid/analog-key-mapper/releases).
