# ryzen-smu-cli

[![CI](https://github.com/Terry577/Arca-ryzen-smu-cli/actions/workflows/ci.yml/badge.svg)](https://github.com/Terry577/Arca-ryzen-smu-cli/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Terry577/Arca-ryzen-smu-cli)](https://github.com/Terry577/Arca-ryzen-smu-cli/releases/latest)
[![License: GPL-3.0](https://img.shields.io/github/license/Terry577/Arca-ryzen-smu-cli)](LICENSE)

`ryzen-smu-cli` is a Windows command-line interface for reading and changing
selected AMD Ryzen System Management Unit (SMU) settings. It uses
[ZenStates-Core](https://github.com/irusanov/ZenStates-Core) for hardware
access.

> [!WARNING]
> SMU, FMax, and Curve Optimizer changes can make a system unstable, corrupt
> work in progress, or prevent Windows from booting normally. Overclocking may
> also affect warranty coverage. Make small changes, validate them under load,
> and keep a known-good BIOS profile. Core-disable changes take effect only
> after a reboot.

## Download

Download the latest version from
[GitHub Releases](https://github.com/Terry577/Arca-ryzen-smu-cli/releases/latest).

| Package | Runtime requirement | Intended use |
| --- | --- | --- |
| `win-x64-self-contained-with-pawnio.zip` | None | Easiest clean-system setup; includes the official interactive PawnIO installer |
| `win-x64-self-contained.zip` | None | Recommended for systems that already have PawnIO |
| `win-x64-framework-dependent.zip` | .NET 8 x64 runtime | Smaller download |
| `symbols.zip` | Not executable | Debugging and crash analysis |

Extract the complete ZIP. Do not copy only `ryzen-smu-cli.exe`;
`inpoutx64.dll` and the adjacent license files are part of the application.
The `with-pawnio` package also contains `PawnIO_setup-v2.2.0.exe`. Run that
installer interactively as an administrator before the first hardware command.
Do not select the unsigned unrestricted PawnIO edition.

Verify a downloaded archive from PowerShell:

```powershell
Get-FileHash .\ryzen-smu-cli-v0.5.0-win-x64-self-contained.zip -Algorithm SHA256
Get-Content .\checksums-sha256.txt
```

## Requirements

- Windows 10 or Windows 11 x64
- A supported AMD Ryzen processor and motherboard firmware
- The signed [PawnIO](https://pawnio.eu/) system driver; PawnIO modules are
  already embedded in the application
- The [.NET 8 x64 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  only when using the framework-dependent package
- The .NET 8 SDK for development
- An elevated terminal for every hardware operation

`--help` and `--version` do not initialize hardware and work without
administrator privileges. The program is Windows-only even when it is built
from WSL. See the [installation guide](docs/INSTALLATION.md) for clean-system,
upgrade, and troubleshooting instructions.

## Run

Open an Administrator PowerShell in the extracted release directory:

```powershell
.\ryzen-smu-cli.exe --get-pbo-scalar
.\ryzen-smu-cli.exe --info
.\ryzen-smu-cli.exe --get-fmax
.\ryzen-smu-cli.exe --get-vcore
.\ryzen-smu-cli.exe --get-cpu-temperature
.\ryzen-smu-cli.exe --get-enabled-cores
```

Start with reads. Review the enabled-core mapping before setting offsets or
changing core-disable masks.

## Build from source

Clone recursively because ZenStates-Core is a Git submodule:

```powershell
git clone --recurse-submodules https://github.com/Terry577/Arca-ryzen-smu-cli.git
cd Arca-ryzen-smu-cli
.\scripts\Test.ps1
```

From WSL, call the installed Windows SDK through the same helper:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "$(wslpath -w "$PWD/scripts/Test.ps1")"
```

The framework-dependent publish directory is written to
`artifacts/win-x64`.

## Commands

```text
--info
    Print CPU identity and topology, including fixed Logical and SMT lines,
    motherboard and BIOS details, CPU firmware revision, SMU version,
    PM-table version and size, and the selected Vcore telemetry mapping.

--offset <offsets>
    Set Curve Optimizer offsets for the compact operable-core map. Use either
    a positional CSV (`-10,5,-20`) or explicit compact-core assignments
    (`0:-10,1:5,2:-20`). Do not mix the two forms. Accepted CLI values are
    -50 through 50; CPU firmware is the final authority. Every accepted write
    is read back from the same selector before success is printed.

--get-offsets-terse
    Probe the operable per-core SMU selectors and print their offsets as one
    compact, selector-ordered CSV line with no heading.

--get-physical-cores
    Print the factory-fused state of every physical core slot.

--get-enabled-cores
    Print the current compact operable-core to SMU-selector-slot mapping.

--disable-cores <indices>
    Set the complete list of physical core slots to disable. Unspecified
    slots are enabled. A reboot is required.

--enable-all-cores
    Enable every physical core slot. A reboot is required.

--set-pbo-scalar <1-10>
    Set the PBO scalar to a whole number from 1 through 10.

--get-pbo-scalar
    Print the current PBO scalar.

--set-fmax <MHz>
    Set the maximum boost-frequency limit for all cores. The value must be a
    positive whole number in 25 MHz steps, for example 5225 or 5250. CPU
    firmware is the final authority.

--get-fmax
    Print the current maximum boost-frequency limit in MHz.

--get-vcore
    Print one mapped CPU-core rail telemetry sample in volts. Desktop-die
    Zen 4/5 processors use an exact PM-table layout; mapped mobile/APU dies
    use a silicon-specific SMU SVI register. The command never substitutes
    per-core/current VID.

--stream-vcore
    Continuously print Vcore samples while keeping one PawnIO/SMU session
    open. Stop with Ctrl+C. This option cannot be combined with another
    hardware operation.

--get-cpu-temperature
    Print one read-only CPU Tctl/Tdie temperature sample in degrees Celsius.
    The supported Zen-family path reads the fixed thermal-control SMN
    register and applies the processor's documented Tctl offset when needed.

--stream-telemetry
    Continuously print one compact JSON object per line containing an atomic
    Vcore and CPU Tctl/Tdie sample. The stream keeps one PawnIO/SMU session
    open and stops cleanly with Ctrl+C. It cannot be combined with another
    hardware operation.

--stream-system-telemetry
    Continuously print schema-2 JSON Lines containing one complete Vcore,
    CPU Tctl/Tdie, AMD RAPL package-power, and per-logical-processor
    APERF/MPERF sample. The first hardware snapshot establishes the counter
    baseline and is not emitted. This option requires --targets and cannot be
    combined with another hardware operation.

--targets <group:processor,...>
    Set the complete Windows logical-processor target set for
    --stream-system-telemetry. Pairs are unique and are emitted in canonical
    group-then-processor order. Group is 0 through 65535 and processor is 0
    through 63; Windows must accept every requested processor-group affinity.

--diagnose-vcore
    Capture the selected Vcore reading plus raw Zen 4/5 SVI register
    candidates and decoded VID fields as one read-only JSON hardware report.
    The CPU family/model/package selects a fixed register whitelist; the
    command never scans arbitrary SMN addresses. This standalone command is
    intended for qualifying structural mappings and investigating currently
    unmapped hardware.

--samples <count>
    Set the --diagnose-vcore sample count from 1 through 1000. The default is
    40. This option is valid only with --diagnose-vcore.

--interval-ms <milliseconds>
    Set the sampling interval. --stream-vcore and --diagnose-vcore allow 50
    through 60000 ms and default to 150 ms. --stream-telemetry allows 100
    through 60000 ms and defaults to 500 ms. --stream-system-telemetry allows
    500 through 60000 ms and defaults to 500 ms.
```

Run `ryzen-smu-cli.exe --help` for the canonical option descriptions. Multiple
non-conflicting operations may be supplied in one invocation. The command
stops at the first failed hardware operation and never prints a success message
for a rejected write.

Core terminology matters:

- A **compact operable-core index** is the zero-based index over SMU selectors
  that successfully answer the current per-core offset probe. It is
  used by both `--get-offsets-terse` and `--offset`.
- A **physical core slot** is the fixed topology position used by
  `--disable-cores`.

Compact keys are assigned in ascending selector order. The Nth item printed by
`--get-offsets-terse` and compact key N accepted by `--offset` refer to the
same selector. Failed reads are never converted into compact cores.
Out-of-range SMU success payloads are rejected as invalid reads rather than
being admitted as cores. Use `--get-enabled-cores` to inspect the current
mapping before changing either setting.

For automation, `--get-vcore` emits exactly one invariant-culture line:

```text
Current Vcore: 1.225000 V.
```

The streaming protocol has no header. Each flushed line contains five
tab-separated fields:

```text
VCORE	0	7	2026-07-31T08:30:00.1234567+00:00	1.225000
```

The fields are the literal record type, zero-based sequence number, monotonic
milliseconds since stream start, UTC timestamp in round-trip format, and
volts with six decimal places. Stream diagnostics are written only to standard
error.

For automation, `--get-cpu-temperature` emits exactly one invariant-culture
line:

```text
Current CPU temperature: 71.875 C.
```

`--stream-telemetry` has no header. Every flushed stdout line is one complete
schema-versioned JSON object:

```json
{"kind":"telemetry","schemaVersion":1,"sequence":0,"elapsedMilliseconds":7,"timestampUtc":"2026-08-10T08:30:00.1234567+00:00","vcoreVolts":1.225,"cpuTemperatureCelsius":71.875,"temperatureSource":"tctl-tdie"}
```

Sequence numbers start at zero and remain contiguous. Elapsed milliseconds
come from a monotonic clock and increase between samples; the UTC timestamp is
informational wall-clock time in round-trip format. A record is written only
after both reads succeed. If either Vcore or temperature fails, no partial
record is emitted, the diagnostic goes to stderr, and the command exits with
code 6. An unsupported Vcore or temperature mapping fails before the first
stdout record with code 3.

`--stream-system-telemetry` is a separate schema-2 protocol; the schema-1
`--stream-telemetry` contract above is unchanged. Supply the complete live
Windows logical topology explicitly:

```powershell
.\ryzen-smu-cli.exe --stream-system-telemetry --interval-ms 500 `
  --targets 0:0,0:1,0:2,0:3
```

There is no header. The first complete raw snapshot establishes the APERF,
MPERF, and package-energy baseline and emits no line. The second complete
snapshot is sequence zero. Each later flushed line has this exact property
order:

```text
kind, schemaVersion, sequence, elapsedMilliseconds, timestampUtc,
measurementIntervalMilliseconds, acquisitionDurationMilliseconds,
vcoreVolts, cpuTemperatureCelsius, temperatureSource, packagePowerWatts,
packagePowerSource, targets
```

Each target has the exact order `group`, `processor`, `aperfDelta`,
`mperfDelta`, `measurementIntervalMilliseconds`, `effectiveMhz`. A canonical
example line is:

```json
{"kind":"system-telemetry","schemaVersion":2,"sequence":0,"elapsedMilliseconds":500,"timestampUtc":"2026-08-12T00:00:00.0000000\u002B00:00","measurementIntervalMilliseconds":500,"acquisitionDurationMilliseconds":4,"vcoreVolts":1.1875,"cpuTemperatureCelsius":71.875,"temperatureSource":"tctl-tdie","packagePowerWatts":100,"packagePowerSource":"amd-rapl-package-energy","targets":[{"group":0,"processor":0,"aperfDelta":1250000000,"mperfDelta":2000000000,"measurementIntervalMilliseconds":500,"effectiveMhz":2500},{"group":0,"processor":1,"aperfDelta":1125000000,"mperfDelta":2000000000,"measurementIntervalMilliseconds":500,"effectiveMhz":2250}]}
```

The default JSON encoder may represent the plus sign in the round-trip UTC
offset as `\u002B`; after JSON decoding the timestamp is the ordinary
`+00:00` form. Both top-level and target
`measurementIntervalMilliseconds` are actual monotonic windows rounded to
integer milliseconds, not copies of the requested cadence; a 500 ms request
may produce 499 or 501. Package and target windows can differ, but their skew
is bounded by half one requested cadence plus one millisecond of rounding.
`effectiveMhz` is `APERF delta / target wall-clock interval / 1e6`, so
it includes idle residency; `mperfDelta` is retained as independent counter
evidence and is not used to relabel the value as an active-only clock.
Package power is the 32-bit modular AMD RAPL package-energy delta divided by
its monotonic interval.

Every target is selected with Windows processor-group affinity before its
read. The complete target set, Vcore, temperature, package energy, APERF, and
MPERF must all be valid before stdout is touched. A missing target, rejected
affinity, counter reset, implausible delta, acquisition lasting more than half
the requested cadence, or measurement interval reaching three cadences stops
the stream with code 6 and emits no partial line. At 500 ms this makes 1500 ms
the stale boundary. Suspend/resume consumers must start a new process; its
first raw snapshot creates a fresh baseline, so counters captured before
sleep are never reused. Ctrl+C remains a successful cancellation.

Capture a shareable Vcore qualification report without performing a write:

```powershell
.\ryzen-smu-cli.exe --diagnose-vcore --samples 40 --interval-ms 150 `
  > .\vcore-diagnostic.json
```

The diagnostic uses the official signed PawnIO `AMDFamily17` read-only SMN
channel, including the Family 1Ah `0x000730xx` range. For each sample, every
whitelisted candidate is read under one PCI-bus lock so the values remain
close in time and one slow address cannot incur a separate lock timeout.

The JSON contains a typed `source` object (`kind`, `confidence`, platform,
register/VID metadata or PM-table metadata), motherboard, BIOS, firmware and
SMU identity, reported topology and its qualification state, requested versus
captured samples, cancellation state, producing CLI version, and
selected/register success and failure counts. Every register also has a typed
name and role. Only registers
whose role is `core-plane` receive VID-to-volts candidate decoding;
`status`, `unknown`, and other-plane registers are not presented as voltage
candidates. Status flags and explicitly named raw hardware-VID bit fields may
still be retained without converting them to Vcore.

`--diagnose-vcore` can still collect the platform whitelist when the normal
Vcore selector is unsupported; in that case the report records the selection
reason instead of guessing a voltage source.

| Diagnostic platform selector | Fixed raw-register groups |
| --- | --- |
| Family `19h`, models `74h`, `75h`, `78h`, `7Ch` | Legacy `0x0005A008` … `0x0005A014` plus extended `0x0006F034` … `0x0006F03C` |
| Family `1Ah`, models `20h`, `24h`, `60h`, `68h` | Legacy `0x0005A008` … `0x0005A014` plus extended `0x0006F034` … `0x0006F03C` |
| Family `1Ah`, model `70h`, or model `44h` mobile package | Extended `0x0006F034` … `0x0006F03C` plus Family 1Ah `0x0007300C` … `0x00073014` |
| Every other family/model/package | No speculative raw SMN candidates |

## Compatibility

Actual CPU support is determined by ZenStates-Core, the CPU's SMU command
table, motherboard firmware, and the motherboard's optional `AMD_ACPI` WMI
interface. FMax reads and writes are reported as unsupported when the
corresponding SMU command is absent.

Vcore is selected by CPUID family/model/package class and, for the
desktop-die path, exact PM-table version and structure size. Marketing names
are not used for layout selection. OEM, regional, X3D, and disabled-iGPU SKUs
based on the same silicon therefore follow the same selector, but a product
name alone never guarantees support for an unobserved firmware layout.

CPU temperature uses the read-only Tctl/Tdie thermal-control register on AMD
Families `17h`, `19h`, and `1Ah`. Sentinel and implausible values are rejected;
the decoder applies the legacy Ryzen/Threadripper Tctl reporting offsets used
by ZenStates-Core. `--stream-telemetry` is available only when both this
temperature source and the platform's Vcore mapping are available.

The schema-2 system stream additionally requires readable AMD RAPL package-
energy, APERF, and MPERF MSRs on every requested Windows processor target.
These counters are accessed read-only through PawnIO. Support is determined by
successful affinity and MSR reads at runtime; an unsupported or incomplete
target set fails closed instead of returning estimated or cross-target data.

The confidence shown by `--info` describes the telemetry mapping, not every
CPU carrying that marketing family:

- **Verified** means that the selected rail field was checked against
  synchronized real-hardware telemetry for that exact layout.
- **Structural candidate** means that independent public structures agree on
  the register or table position, but synchronized hardware captures are
  still required before calling the mapping verified.

Structural candidates are available as experimental sources. They must be
compared with synchronized motherboard telemetry on real hardware before being
treated as hardware-verified sources. Vcore coverage also does not imply that
the platform's per-core topology and Curve Optimizer selectors are qualified.

| Platform | CPUID family/model | Representative product families | Selected telemetry | Confidence |
| --- | --- | --- | --- | --- |
| Raphael | `19h/61h` desktop package | Ryzen 7000 desktop | Exact PM table, entry 47 | Verified for two layouts; remaining known layouts structural |
| Dragon Range / refresh | `19h/61h` mobile package | Ryzen 7045HX and 8000HX | Exact `0x00540208` PM table, entry 48 | Structural candidate |
| Phoenix / Phoenix 2 / Hawk Point | `19h/74h`, `75h`, `78h`, `7Ch` | Ryzen 7040 mobile and Ryzen 8000G/F/mobile | Silicon-specific SMU SVI rail | Structural candidate |
| Granite Ridge | `1Ah/44h` desktop package | Ryzen 9000 desktop | Exact PM table, entry 49 | Verified for `0x00620105`; other known layouts structural |
| Strix Point / Krackan Point | `1Ah/20h`, `24h`, `60h`, `68h` | Ryzen AI 300 families | Silicon-specific SMU SVI rail | Structural candidate |
| Strix Halo | `1Ah/70h` | Ryzen AI Max families | Silicon-specific SMU SVI rail | Structural candidate |
| Fire Range | `1Ah/44h` mobile package | Ryzen 9000HX | Exact matching desktop-style PM layout, entry 49 | Structural candidate; no Fire Range layout is hardware-verified yet |

The desktop-die path is deliberately guarded by both PM-table version and
structure size:

| Family/model/package | PM-table versions | Required sizes | Entry | Confidence |
| --- | --- | --- | ---: | --- |
| `19h/61h` | `0x00540004` | `0x08BC` | 47 | Verified |
| `19h/61h` | `0x00540104` | `0x06A8` | 47 | Verified |
| `19h/61h` | Other listed `0x00540000` … `0x00540005` revisions | Exact per-version `0x0828` … `0x08C8` sizes | 47 | Structural candidate |
| `19h/61h` | Other listed `0x00540100` … `0x00540105`, `0x00540108` revisions | Exact per-version `0x0618` … `0x06BC` sizes | 47 | Structural candidate |
| `19h/61h` | `0x00540208` | `0x08D0` | 48 | Structural candidate |
| `1Ah/44h` desktop | `0x00620105` | `0x0724` | 49 | Verified Granite Ridge layout |
| `1Ah/44h` desktop | `0x00620205` | `0x0994` | 49 | Structural Granite Ridge candidate |
| `1Ah/44h` desktop | `0x00621102` | `0x0724` | 49 | Structural Granite Ridge candidate |
| `1Ah/44h` desktop | `0x00621202` | `0x0994` | 49 | Structural Granite Ridge candidate |
| `1Ah/44h` mobile | `0x00621102` | `0x0724` | 49 | Structural Fire Range candidate |
| `1Ah/44h` mobile | `0x00621202` | `0x0994` | 49 | Structural Fire Range candidate |

An unknown family/model, an unknown PM-table version, or a known version with
a different structure size returns exit code 3 for normal Vcore reads and
reports the detected metadata on standard error. The CLI fails closed: it does
not guess an index and does not fall back to a per-core VID array. Use
`--diagnose-vcore` to collect read-only raw evidence for a new mapping. On
Granite Ridge, entry 49 is the live CPU rail; the peak/limit-style entry 18 is
intentionally not used. On Raphael, including `0x00540104`, the live rail is
entry 47 rather than entry 18.

Compact Curve Optimizer operations are qualified by the operation they
actually perform: a selector must answer the current per-core offset probe
before it receives a compact key. `--get-offsets-terse`, `--offset`, and
`--get-enabled-cores` therefore do not require an exact physical-slot count or
a complete factory fuse map. A failed selector read is never interpreted as a
core, and write success additionally requires an exact read-back from the same
selector. This allows a supported SMU offset command to remain usable when
firmware topology metadata is incomplete.

The enabled-CCD bitmap is only a discovery hint. It preserves CCD1's real SMU
selector when CCD0 is entirely disabled. When the bitmap is unavailable on
Raphael/Dragon Range or Granite Ridge/Fire Range silicon, both possible CCD
selectors are probed in deterministic order; selectors that do not answer are
discarded. A nonzero single-CCD bitmap remains the fast path, but if every
selector in that primary range fails, the opposite CCD selector is probed to
recover from stale firmware metadata. A healthy primary range is still read
only once. The compact contract deliberately describes operable offsets, not
factory-fused physical slots.

APU selectors above index 7 use the full compact index for reads instead of
wrapping to a lower selector. That extended mapping is structurally
implemented but has not yet been qualified on representative real hardware.
Any write still requires an immediate exact read-back through the same
`CoreAddress` mapping; rejection, read failure, or mismatch returns exit code
6 and is not reported as success. Until representative hardware qualifies the
extended mapping, that value check is not a claim that physical target
identity above index 7 has been proven.

Phoenix-family (`19h/74h`, `75h`, `78h`, and `7Ch`) and heterogeneous Family
1Ah mobile (`20h`, `24h`, `60h`, `68h`, and `70h`) Vcore mappings remain
structural candidates until synchronized hardware captures qualify them. That
Vcore confidence is independent of whether their SMU firmware accepts a
particular compact Curve Optimizer selector.

Physical topology operations remain separate and strict. `--disable-cores`,
`--enable-all-cores`, and `--get-physical-cores` still require a qualified
physical-slot topology and complete data appropriate to the requested
operation. Fire Range's currently mapped Vcore layouts likewise remain
structural candidates until synchronized hardware captures qualify them.

CPU identity, logical-processor count, and threads per core come from the CPU
and CPUID topology exposed by the SMU hardware layer; they are not copied from
Windows processor enumeration. When physical fuse topology is unqualified,
the user-facing physical-core count prefers the CPUID-derived core count over
the fuse-slot estimate. The raw slot and enabled-core evidence remains
available in Vcore diagnostic JSON.

Release 0.3.0 was validated with:

- AMD Ryzen 7 9800X3D;
- Windows 11 x64;
- PawnIO 2.2.0;
- all eight physical cores enabled;
- read-only PBO scalar, Curve Optimizer, fused-core, and enabled-core queries.

That validation does not guarantee support for every CPU or motherboard.
Unsupported SMU commands and missing downcore interfaces return nonzero exit
codes rather than being treated as success.

## Exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 1 | Command-line parse error or administrator privileges are missing |
| 2 | Hardware initialization failed |
| 3 | The CPU does not expose the requested SMU operation |
| 4 | Invalid request discovered after parsing |
| 5 | Core index is outside the detected topology |
| 6 | An SMU operation failed or was rejected |
| 7 | AMD ACPI downcore support is unavailable or failed |
| 8 | Enabled cores could not be mapped reliably |
| 9 | Unsupported operating system |

## Development

The solution separates command-line parsing, domain validation, hardware
adapters, and orchestration. Automated coverage is deliberately limited to a
closed allowlist of 13 critical safety/protocol scenarios; ordinary changes do
not add tests without explicit user approval. See the root `AGENTS.md` for the
fixed budget and boundaries. `scripts/HardwareSmokeTest.ps1` contains only read
operations for an elevated post-build check.

See
[CONTRIBUTING.md](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/master/CONTRIBUTING.md)
for the workflow and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for bundled dependency and
native-component details.

Additional documentation:

- [Changelog](CHANGELOG.md)
- [Installation](docs/INSTALLATION.md)
- [Architecture](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/master/docs/ARCHITECTURE.md)
- [Release process](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/master/docs/RELEASING.md)
- [Security policy](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/master/SECURITY.md)
- [v0.5.0 release notes](docs/releases/v0.5.0.md)
- [v0.4.0 release notes](docs/releases/v0.4.0.md)
- [v0.3.5 release notes](docs/releases/v0.3.5.md)
- [v0.3.4 release notes](docs/releases/v0.3.4.md)
- [v0.3.3 release notes](docs/releases/v0.3.3.md)
- [v0.3.2 release notes](docs/releases/v0.3.2.md)
- [v0.3.1 release notes](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/v0.3.1/docs/releases/v0.3.1.md)
- [v0.3.0 release notes](https://github.com/Terry577/Arca-ryzen-smu-cli/blob/v0.3.0/docs/releases/v0.3.0.md)

The project is licensed under GPL-3.0. All credit for the hardware library
belongs to the ZenStates-Core contributors. The implementation also draws on
the public [SMUDebugTool](https://github.com/irusanov/SMUDebugTool) examples.
