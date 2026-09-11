# Offline tests

Run `./Test-All.ps1` from the repository root in PowerShell 7 on Windows. The suites use synthetic backends, local temporary files and, where needed, their own test windows or fake output processes. They do not open the real keyboard or attach a controller. Respect normal Windows application-control decisions; do not change policy or use another host to run a blocked test.

The portable C#/PowerShell suites and their harness/fake-host files are included; `Test-All.ps1` discovers every `Test-*.ps1` suite. The former `Test-MonitorSigning.ps1` is intentionally excluded: it depended on unpublished signing handoff scripts and is not a runtime regression test. Signing enrollment and credentials are not included.

Fixture dependencies are under `fixtures/`, with no development `research/` or private `captures/` directory needed:

- `live-wasd-verification.jsonl`, `outside-sandbox-w.jsonl`, and `capture-notes.md`: the existing sanitized sensor-report fixtures and their limitations. Device paths/serials are removed; these still contain actual W/A/S/D pressure samples and relative arrival times, not synthetic measurements.
- `documented-feature-packets.json`: independently recorded manufacturer protocol facts used by `Test-MonitorProtocol.ps1`.
- `rgb-supported-key-indices.json`: expected RGB-capable matrix positions extracted from the two pinned manufacturer models. Source URLs, source hashes and the exact extraction rule are recorded with each model. The proprietary JavaScript bundles themselves are not distributed.
- `tk75-layout-ansi.json`, `tk75-layout-iso.json` and the corresponding `tk75-matrix-*.json`: independently recorded key geometry, USB usages and matrix bytes used by `Test-VisualKeyboard.ps1`. These fact fixtures include the manufacturer source URLs and hashes; they do not contain the executable bundles.

`Test-Tk75RgbProtocol.ps1 -ProtocolOnly` continues to run only the synthetic protocol unit; the default also checks the independent RGB-position fixture. `Test-ReportAnalysis.ps1` requires the included `src/Analyze-Reports.ps1`. `Test-IsolatedOutput.ps1` builds its own synthetic helper under `tests/tmp/isolation-tests`, not in a research tree.

The separate Go helper protocol unit is documented in `src/ViiperOutputHost/README.md`; it is not invoked by `Test-All.ps1`. Compiling or passing these tests is not a real-device or driver acceptance claim.

`Measure-MappingPerformance.ps1` is an optional synthetic benchmark, excluded from `Test-All.ps1`. It runs real mapping workers with fake input and output, without the keyboard, helper processes, driver or game. Results describe that boundary only; memory includes its PowerShell host. See [measurement scope](../docs/performance.md).
