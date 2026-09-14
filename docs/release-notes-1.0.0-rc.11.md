# Analog Key Mapper 1.0.0-rc.11

Selecting another key now keeps your current tab and scroll position while its settings update. Scrollbars keep their themed appearance while hovered, dragged or held, including inside tables and open drop-down lists.

## Switch keys without losing your place

Clicking a key leaves the Keys, Curve or Controller tab you are using open. The settings change to the new selection without clearing and rebuilding the lists. Switching away from a tab and returning also preserves its scroll position.

Mapping rows, setting cells and existing choices are reused; rows are added or removed only when the number of mappings changes. Hints update to their complete new text once, avoiding intermediate layout changes. Titles, mapping summaries, pressure ranges and live values still follow the selected keys.

Pending edits retain their original key scope. An unfinished gesture or capture cannot carry its changes into the next selection. When an input source disconnects, obsolete raw values are cleared from the key list.

## Shared scrollbar handling

The app now handles scrollbar mouse input through one shared component. The previous approach repainted after Windows processed an input message; during native scrollbar tracking, that repaint could not run until the mouse was released. Complete buffered drawing now prevents partially erased tracks and thumbs, and content changes are redrawn together with their frames.

The update covers the editor's scrolling panels, both table directions, lists, text fields and combo-box popups. Wheel and keyboard scrolling use the same redraw coordination. It retains 32-bit positions beyond 65,535, existing item selections, scroll events and the native controls' keyboard focus behavior.

Held arrow and page clicks repeat until released or cancelled. Losing capture, disabling a control, changing its range, recreating its handle or closing a window from a scroll callback ends or rebases the gesture safely. An open combo list retains its existing capture instead of closing when its thumb is pressed.

The public screenshots have been regenerated from the current English interface with the official application logo and synthetic example data.

## Validation and downloads

The combined update passed **16,993 local UI checks**, including **230 key-selection checks**, **2,373 scrollbar checks**, **354 redraw checks** and **778 native theme checks**. The new selection tests compare actual control, handle, row and cell identity, then verify updated values, precisely scoped edits, undo and redo. The scrollbar checks inspect existing control pixels while the mouse remains held and verify actual content movement; they also confirm that scrollbar mouse messages do not reach the competing native tracking procedure.

The local run used Windows' live content dragging option enabled. A separate isolated build exercised deferred content scrolling and passed **2,375 checks**, without changing Windows preferences or moving the pointer. See the [validation record](status.md) for the exact setup and the scope of background/tray and shutdown checks.

The release workflow runs **62 offline suites**, builds the Windows application and controller helper, tests the helper and verifies the source and binary packages. Its [release page](https://github.com/SirRanjid/analog-key-mapper/releases/tag/v1.0.0-rc.11) records the completed workflow for the downloadable files. The Windows package includes its source-manifest receipt and checksums.

This remains an unsigned release candidate. These checks use synthetic controls and inputs; they do not establish every Windows desktop theme or add physical keyboard/controller, reboot or game acceptance. See [validation status](status.md) and [build/update instructions](building.md).
