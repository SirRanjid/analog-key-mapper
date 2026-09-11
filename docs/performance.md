# Performance measurements

The mapper sleeps when a slot is disconnected and has no visible preview. Hidden windows skip drawing and preview calculation while keeping input safety, shortcuts and lighting maintenance active. Active output requests a 4 ms worker wait; Windows scheduling can make it longer.

## Synthetic mapping benchmark — 1.0.0-rc.1

On 11 September 2026, `Measure-MappingPerformance.ps1` exercised the real C# mapping workers with synthetic input and a fake output sink. Each case ran for four seconds after warm-up. The host reported 16 logical processors and .NET 10.0.11. This measures neither the complete application nor keyboard-to-game latency. The Windows application uses .NET Framework; the benchmark below ran inside PowerShell 7. CPU and memory include that benchmark process.

| Mapping workers | Preview hidden: CPU, core equivalents | Preview hidden: publication-to-fake-output p95 | Selected preview active: p95 |
| ---: | ---: | ---: | ---: |
| 1 | 0.008 | 15.55 ms | 16.45 ms |
| 4 | 0.004 | 15.65 ms | 16.33 ms |
| 8 | 0.004 | 16.04 ms | 15.99 ms |
| 16 | 0.031 | 15.99 ms | 16.04 ms |
| 32 | 0.043 | 16.12 ms | 16.19 ms |

One core equivalent means one fully occupied logical processor. These short samples have coarse CPU accounting and normal scheduler noise; a reported zero does not establish zero resource use. The non-monotonic small values should not be treated as meaningful differences. The configured 4 ms wait did **not** establish 4 ms latency on this setup.

The separate case with 32 disconnected workers and no preview recorded **zero input snapshot copies** during its measurement interval. The raw [JSON report](mapping-performance-1.0.0-rc.1.json) includes CPU, process memory, collection counts and latency percentiles for every case.

The benchmark excludes the keyboard, HID input, interprocess pipes, output helpers, USB/IP driver, rendering and game. Its memory figures include the PowerShell host, JIT and previous cases. It is not evidence that 32 real controllers perform acceptably or that the final Windows package has passed live acceptance. The 32-slot cap remains a configuration limit. Whole-app resource measurement and game testing remain open.

## Repeat locally

From a fresh PowerShell process in the source directory:

```powershell
./tests/Measure-MappingPerformance.ps1 -Seconds 10 -Counts @(1,4,8,16,32) -OutputPath "$PWD/build/mapping-performance.json"
```

The benchmark creates no real controller and opens no keyboard. Use the same runtime, workload and machine when comparing changes; retain the complete report and describe the excluded parts.
