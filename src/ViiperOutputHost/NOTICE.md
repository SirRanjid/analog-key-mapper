# VIIPER-based output helper: separate license

The MIT license of the surrounding mapper does not relicense this directory's third-party code. This helper combines the mapper's adapter with selected VIIPER implementation packages and is distributed under **GPL-3.0-or-later**. The complete GPL version 3 text is in `LICENSE`.

VIIPER — Virtual Input over IP EmulatoR

Copyright (C) 2025–2026 Peter Repukat.

VIIPER is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed without any warranty, including the implied warranties of merchantability or fitness for a particular purpose. See `LICENSE` for the full terms.

Upstream: <https://github.com/Alia5/VIIPER>, version v0.7.0, commit `6b71b148a2243fab77ee1a46f4e22e00bd7d5a04`.

This is a deliberately small source subset for a Windows amd64 helper, not an upstream VIIPER release. The selected upstream production `.go` files are unchanged. Added adapter files are under `cmd/tk75-output-host`; `go.mod` was narrowed to the three external modules actually imported by this build. `SOURCE-MANIFEST.json` records each included Go source file and its SHA-256. The module path is retained so Go's internal-package visibility rules continue to work; it does not identify this project as an official upstream release.

The native USB/IP adapter references the usbip-win2 IOCTL/layout definitions from `vadimgrn/usbip-win2`, commit `83bd1f781d57ed6efdf15530c55710cf5d4482bc`. Its BSD-2-Clause notice is retained in `licenses/usbip-win2-BSD-2-Clause.txt`. No driver, driver installer or proprietary binary is included.

External Go modules are pinned by `go.mod` and `go.sum`; their required source packages and original notices are included in `vendor/`, and their BSD notices are also retained in `licenses/`. `vendor/modules.txt` lists the exact package closure and `VENDOR-MANIFEST.json` records its file hashes:

- `golang.org/x/sys` v0.45.0
- `golang.org/x/crypto` v0.52.0
- `golang.org/x/exp` v0.0.0-20260410095643-746e56fc9e2f

The Go runtime's BSD notice is included there as well. Preserve these notices and provide the corresponding helper source, including the build recipe, when distributing a compiled helper. A separate process does not remove the helper's GPL obligations.
