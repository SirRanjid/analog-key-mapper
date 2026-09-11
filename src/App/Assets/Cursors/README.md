# Chromium hand cursors

The two CUR files are unmodified upstream resources, downloaded on 2026-09-11 from the official Chromium source archive.

Pinned Chromium revision: `7e295124eb0a75dd6f0cb42e22c5eb5eae7214b3`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| hand_grab.cur | 3582 | 3df4009e28e365e1666c868aede15239c75cbb6cf710cd691997b722c3eea7f0 |
| hand_grabbing.cur | 3582 | 22e823e71c106f338d42932c13c16e05a8310b3bdec18a89cc5ca197408cf11a |
| LICENSE | 1536 | 368cca1106be99d39ecd32a38d8305585d802a475effb66380b91ffc9bcf709b |

Upstream sources:
- https://chromium.googlesource.com/chromium/src/+/7e295124eb0a75dd6f0cb42e22c5eb5eae7214b3/ui/resources/cursors/hand_grab.cur
- https://chromium.googlesource.com/chromium/src/+/7e295124eb0a75dd6f0cb42e22c5eb5eae7214b3/ui/resources/cursors/hand_grabbing.cur
- https://chromium.googlesource.com/chromium/src/+/7e295124eb0a75dd6f0cb42e22c5eb5eae7214b3/LICENSE

The cursor files are also embedded byte-for-byte as Base64 constants in `../../DragCursors.cs`, so builds do not depend on an external cursor file or new resource options. Each upstream file contains a 1-bit and 24-bit 32px image, both with hotspot (13,13). The loader selects the 24-bit DIB and its AND mask without changing any pixel, places the original hotspot words before them as required by RT_CURSOR, and calls `CreateIconFromResourceEx`. It does not render or recolor the artwork and does not use OLE/IPicture loading. The native handle is created once, retained for the process lifetime, and released with `DestroyCursor` after disposing its non-owning managed wrapper on exit. The system click cursor is separately obtained from Windows with `LoadCursorW(NULL, IDC_HAND)`; that borrowed Windows handle is never destroyed.

License: Chromium BSD-style license, retained verbatim in LICENSE and the loader source. Include the complete LICENSE with binary distributions, as required by its redistribution terms.
