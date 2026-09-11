# Third-party notices

The root MIT license covers the original Analog Key Mapper code and documentation.
It does not replace the following third-party licenses.

| Component | Location | License |
| --- | --- | --- |
| Chromium hand cursor artwork, also embedded in `DragCursors.cs` | `src/App/Assets/Cursors/` | Chromium BSD-style license; see the complete `LICENSE` and pinned provenance in that directory |
| VIIPER-based controller helper and its upstream code | `src/ViiperOutputHost/` | GNU GPL v3 or later; see the helper's `LICENSE` and `NOTICE.md` |
| Go modules used by the helper | Pinned by the helper's `go.mod` and `go.sum` | Each module retains its upstream license; see the helper notices |

The optional helper is a separate GPL component. Its sources, build instructions
and dependency versions are included; the root MIT license does not relicense it.
The build copies applicable runtime notices into `bin/licenses/`.

The usbip-win2 driver is a separately installed external dependency. No driver
installer or manufacturer application is included. No ViGEm binary is included;
the inactive compatibility adapter remains in the C# source.

GamaKay, Xbox, PlayStation, GitHub, Chromium and ChatGPT are names of their
respective owners. This is an independent project, not an official product of
those organizations.
