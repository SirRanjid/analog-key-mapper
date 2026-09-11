# Protocol fact fixtures

These files contain protocol facts for offline comparisons, not the manufacturer's executable JavaScript bundles or local keyboard recordings.

`documented-feature-packets.json` preserves the independent, previously recorded 65-byte request vectors unchanged. SHA-256: `43e902b4f1921a1eac6b1b12bf07ef5876d53a8cebbfbac925c332d17e6bcb94`. The request grammar was recorded from the GearHub V4 transport implementation at <https://gearhub.top/v4/js/9d6437da.js>; the reviewed local source bundle has SHA-256 `f8f20d3b0f44144788315a1a0fbda7ed6a0d976c116a513666afcf63d3105158`. Source URLs may change their contents; the hashes identify the reviewed snapshots.

`rgb-supported-key-indices.json` contains the independent expected matrix positions for models 3590 and 3591. Each entry records its model module URL, SHA-256 and extraction rule. These lists were extracted from the pinned manufacturer `defaultMatrix` before comparison with the mapper's `GetSupportedKeyIndices`; they were not generated from the mapper implementation. Only indices 0 through 89 belong to this RGB protocol's supported comparison range.

The two JSONL sensor fixtures have separate provenance and explicit measurement limits in `capture-notes.md`. Their relative arrival times are observations, not a sensor sample-rate claim.

`tk75-layout-ansi.json` and `tk75-layout-iso.json` preserve the previously extracted key geometry and matrix identities unchanged. Each records both its matrix and geometry source URL and SHA-256. `tk75-matrix-ansi.json` and `tk75-matrix-iso.json` contain the 512 `defaultMatrix` byte values extracted directly from those same pinned model modules, after checking the module hash against the layout record. No mapper implementation or proprietary executable bundle is included in these expected values. `Test-VisualKeyboard.ps1` checks the actual layout against the geometry facts and the independently extracted matrix bytes, including the three knob segments beyond the RGB-key range. Only W/A/S/D were physically corroborated; the remaining positions are manufacturer-source evidence.
