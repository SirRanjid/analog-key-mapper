# Changelog

## 1.0.0-rc.11

- Keep the current tab and scroll position when selecting another key, and retain each tab's scroll position when returning to it.
- Update existing setting rows and choices for the new selection. Add or remove mapping rows only when their count changes, and update complete hint text once to avoid unnecessary layout changes.
- Preserve the original scope of pending edits and cancel gestures or captures that no longer belong to the selection. Clear obsolete raw input values when an input source disconnects.
- Use shared scrollbar input handling for editor panels, tables, lists, text fields and drop-downs. Hover, dragging and held arrow/page clicks no longer enter Windows' separate scrollbar tracking painter.
- Buffer complete scrollbar frames and coordinate content redraws, including wheel input and view rebuilds, so the thumb stays themed throughout interaction.
- Preserve full 32-bit scroll positions, keyboard focus, existing selections and scroll notifications. Handle capture loss, disabled controls, changed ranges and controls recreated or closed during a gesture.
- Refresh the documentation screenshots from the current build with the official logo.

The combined update passed 16,993 local UI checks, including 230 key-selection and 2,373 scrollbar checks. A separate isolated run also exercised deferred content scrolling without changing Windows preferences. The release workflow retains 62 offline suites plus controller-helper and package checks. See [validation status](docs/status.md) and [rc.11 release notes](docs/release-notes-1.0.0-rc.11.md) for the recorded scope and completed release workflow.

## 1.0.0-rc.10

- Check for leftover mode-switch and controller colors at startup or keyboard reconnection, independently of currently enabled lighting options and controller connections.
- Ask before cleaning recognized patterns, showing the affected keys and proposed colors. Optional future automatic cleanup starts off and can be disabled from the app or tray menu.
- Preserve unrelated colors and external background changes. Leave lighting unchanged when the same color appears on unrelated keys or the replacement colors cannot be determined; identical external patterns cannot establish ownership.
- Wait for actual keyboard-helper cleanup during normal exit and Windows shutdown, track outstanding controller removal, and prevent older cleanup work from affecting a newer mapping session.
- Use the official application logo and compact status badges in the tray, including a bordered warning triangle, and refresh the documentation screenshots.

The lighting and lifecycle changes passed 1,765 targeted local checks. The release workflow contains 62 offline suites plus controller-helper and package checks. See [rc.10 release notes](docs/release-notes-1.0.0-rc.10.md) and [validation status](docs/status.md) for the exact completed build record. Real hardware, Windows shutdown and game acceptance of this revision remain open.

## 1.0.0-rc.9

Windows build, 59 offline test suites and version-checked packaging validated. See the release notes for the validation record and remaining hardware limits.

- Add **Learn unknown inputs…** to the active key selection's context menu, with sequential capture and one reviewed, undoable apply.
- Automatically identify matching standard keyboard positions; retain existing known assignments.
- Support learned routes from readable standard HID buttons, absolute axes, hats and relative controls, alongside decoded analog keyboard channels.
- Save source/control identities per profile, invalidate disconnected inputs, and add a separate **Forget learned inputs** action.
- Keep gesture analysis in the assistant and use the saved interpretation during normal mapping.

The new native input backends still require physical hardware and game acceptance. See [rc.9 release notes](docs/release-notes-1.0.0-rc.9.md) and the [input learning guide](docs/input-learning.md).

Earlier version notes and their checked packages are available on the [releases page](https://github.com/SirRanjid/analog-key-mapper/releases).
