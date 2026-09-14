# Analog Key Mapper 1.0.0-rc.11

Scrollbars now keep their themed appearance while hovered, dragged or held, including inside tables and open drop-down lists. Returning to a detail view also draws the scrollbar and its child controls correctly immediately.

## Shared scrollbar handling

The app now handles scrollbar mouse input through one shared component. The previous approach repainted after Windows processed an input message; during native scrollbar tracking, that repaint could not run until the mouse was released. Complete buffered drawing now prevents partially erased tracks and thumbs, and content changes are redrawn together with their frames.

The update covers the editor's scrolling panels, both table directions, lists, text fields and combo-box popups. Wheel and keyboard scrolling use the same redraw coordination. It retains 32-bit positions beyond 65,535, existing item selections, scroll events and the native controls' keyboard focus behavior.

Held arrow and page clicks repeat until released or cancelled. Losing capture, disabling a control, changing its range, recreating its handle or closing a window from a scroll callback ends or rebases the gesture safely. An open combo list retains its existing capture instead of closing when its thumb is pressed.

The public screenshots have been regenerated from the current English interface with the official application logo and synthetic example data.

## Validation and downloads

Before publication, the local application UI run passed **16,655 assertions**, including **2,318 scrollbar interaction checks**, **354 view-rebuild checks** and **778 native theme checks**. Separate background/tray and shutdown suites passed **205** and **101** assertions. The scrollbar checks inspect existing control pixels while the mouse remains held and verify actual content movement; they also confirm that the scrollbar mouse messages do not reach the competing native tracking procedure.

The release workflow runs **62 offline suites**, builds the Windows application and controller helper, tests the helper and verifies the source and binary packages. The [rc.11 release page](https://github.com/SirRanjid/analog-key-mapper/releases/tag/v1.0.0-rc.11) records the completed workflow for the downloadable files. The Windows package includes its source-manifest receipt and checksums.

This remains an unsigned release candidate. These checks use synthetic controls and inputs; they do not establish every Windows desktop theme or add physical keyboard/controller, reboot or game acceptance. See [validation status](status.md) and [build/update instructions](building.md).
