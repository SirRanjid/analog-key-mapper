# Output helper source

This Windows amd64 helper supplies the mapper's explicitly experimental, exclusive single-Xbox-controller route. It is a small VIIPER v0.7.0 source subset plus the adapter in `cmd/tk75-output-host`; see [NOTICE.md](NOTICE.md) and [LICENSE](LICENSE) for its separate GPL license and the pinned upstream revision.

From the repository root:

```powershell
./Build-OutputHelper.ps1
```

Go 1.26.2 or newer must already be installed and available on PATH. The local build was verified with Go 1.27.1. `-GoExecutable 'C:\path\to\go.exe'` selects an existing toolchain. The script never downloads a compiler, starts the built helper or installs a driver. The pinned dependencies are included in `vendor/`, and the build uses them by default; with an installed Go compiler, no module download or populated module cache is needed. `-Offline` also disables module downloads explicitly. If `vendor/` is deliberately removed, the build falls back to read-only module resolution from `go.mod`/`go.sum` and may download dependencies unless `-Offline` is used. Existing `GOMODCACHE`/`GOCACHE` settings are respected.

The result is `bin/ViiperOutputHost.exe` by default, alongside notices under `bin/licenses/viiper-output-helper`. Pass `-OutputDirectory` for another directory within this repository. Place the helper next to `AnalogKeyMapper.exe` to make the Xbox route available; the separately installed, compatible USB/IP driver is still required. Compilation is not proof of driver or hardware compatibility.

The mapper selects `--xbox-runtime-host`. Startup is neutral, the USB listener binds to loopback, controller ownership is exclusive, and input/ACK/cleanup deadlines remain enforced. General production and DualSense routes are still gated in the source. No driver, private profile, device capture, compiler, module cache or prebuilt executable is included here.

The independent protocol unit can be tested without running any native/controller source:

```powershell
cd src/ViiperOutputHost/cmd/tk75-output-host
go test -count=1 host.go host_test.go
```

This is the only helper unit recommended for routine source CI. The other two adapter test files are retained as source. Historical Windows application-control blocks of native test executables must not be worked around; a successful build does not claim those tests passed. Do not run the helper's live modes merely to check installation.

This subset intentionally omits the upstream CLI, tray application, installers, clients, examples, code generators, unrelated device families and upstream test suites. The 47 upstream/adapter production Go files form the helper's selected Windows build dependency closure. Three adapter test files are included separately. `vendor/` additionally contains the source closure of the three pinned external modules, produced with ordinary `go mod vendor`; `vendor/modules.txt` records their versions and `VENDOR-MANIFEST.json` records file hashes. Source and build recipe reproducibility are the aim; byte-identical outputs require the same Go version and build environment.
