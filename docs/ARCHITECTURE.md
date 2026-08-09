# Architecture

## Overview

`ryzen-smu-cli` is a Windows-only .NET 8 console application. The executable
owns command parsing and safety policy; ZenStates-Core owns CPU discovery,
family-specific SMU transport, and low-level hardware access.

```text
Program
  |
  v
CliApplication -------- argument parsing and early validation
  |
  v
CommandRunner ---------- privilege checks, ordering, exit codes, output
  |           |
  |           +-------- CoreMapper
  |
  v
IRyzenController
  |
  +-------- ZenStatesRyzenController ---- ZenStates-Core / PawnIO / InpOut
  |
  +-------- AmdAcpiDowncoreController --- root\wmi:AMD_ACPI
```

Tests replace `IRyzenController` and `IPrivilegeChecker` with deterministic
test doubles. This keeps CI isolated from real firmware and drivers.

The hardware adapter exposes capability flags for optional SMU operations,
including FMax reads and writes, mapped CPU-rail Vcore telemetry, and
read-only CPU Tctl/Tdie temperature telemetry.
`CommandRunner` rejects an unsupported operation before issuing its SMU
command and treats invalid or rejected results as failures.

## Command lifecycle

1. System.CommandLine parses options and applies syntax-level validators.
2. Parser-only commands (`--help` and `--version`) return without checking
   Windows, elevation, or hardware. Hardware information such as `--info`
   continues through the normal read-only hardware path.
3. `CommandRunner` verifies Windows and administrator privileges.
4. Hardware is initialized through a factory. A missing PawnIO installation is
   reported with an installation URL and bundled-installer guidance.
5. A compact operable-core map is created only when a Curve Optimizer
   operation requires it. Candidate selectors are probed once on the healthy
   path; only successfully readable selectors are admitted, and their sampled
   offsets are cached for terse output.
6. Every input range and operation-specific assumption is validated before
   the first mutation. Physical topology is not made a prerequisite for a
   compact offset operation.
7. Operations execute in a deterministic order and stop on the first failure.
8. Curve Optimizer writes are read back from the same mapped selector. A
   success message is written only after the value matches exactly.
9. The controller is disposed on every path.

## CPU identity and topology information

CPU name, CPUID, logical-processor count, and threads per core are read from
the CPU/CPUID data initialized by ZenStates-Core rather than Windows processor
enumeration. `--info` always emits separate `Logical` and `SMT` rows so
consumers do not need to infer SMT from a localized operating-system label.

The physical-slot fuse map remains authoritative only for commands that
actually address physical topology. When it is unqualified, the `--info`
physical-core count prefers CPUID `topology.cores`; the fused-slot and
enabled-core values remain diagnostic evidence and are not presented as the
user-facing physical count. The Vcore diagnostic JSON records both the
user-facing physical/logical/SMT values and the raw reported slot/enabled
values.

## Vcore telemetry

Desktop-die Vcore is read from an exact SMU PM-table layout. Mobile and APU
silicon with no qualified PM-table rail uses a family/model-specific SMN SVI
register. Neither path substitutes per-core/current VID for the CPU supply
rail.

`VcoreTelemetryLayout` keys PM mappings by CPU family, model, package class,
table version, and structure size. `SviTfnVcoreTelemetry` records the exact
read-only register, VID bit field, optional status register, platform, and
mapping confidence. Unknown metadata is unsupported rather than an invitation
to guess an index or nearby register.

`--get-vcore` performs one mapped read. `--stream-vcore` initializes PawnIO,
ZenStates-Core, and the SMU once, then refreshes the selected PM or SVI source
in the same controller session. This persistent mode is required for sampling
intervals such as 150 ms; starting a new process for every sample would make
the timing and overhead unsuitable.

`--diagnose-vcore` selects a fixed raw-register whitelist from CPU
family/model/package. Each sample reads that whitelist under one PCI-bus lock
through the signed PawnIO `AMDFamily17` read-only SMN module. The CLI accepts
no arbitrary address argument. Only descriptors typed as `core-plane` are
decoded as VID candidates; status, unknown, and other-plane registers remain
raw evidence. For a selected SVI source, the reported voltage is derived from
the same captured raw value. The JSON also records typed source/confidence,
hardware and firmware identity, physical/logical core counts, threads per core
and SMT state, raw slot/enabled counts, topology qualification, timing,
cancellation, and success/failure counts.

Stream stdout is a headerless tab-separated protocol:

```text
VCORE	sequence	elapsed_ms	utc_timestamp	volts
```

All numeric fields use invariant culture, every record is flushed
immediately, and diagnostics use stderr. Ctrl+C cancels the stream cleanly.
If a hardware read overruns one or more requested intervals, the scheduler
skips those expired slots and waits for the next future cadence boundary; it
never emits an immediate catch-up sample.
Read failures stop the stream with exit code 6; unsupported layouts fail
before the first stdout record with exit code 3. A closed stdout pipe also
terminates through the normal failure path, disposes the controller, and
releases the PawnIO session; the PCI mutex is held only around an individual
PM-table refresh, SVI read, or diagnostic batch, never between samples.

## Combined CPU telemetry

`--get-cpu-temperature` reads the fixed `THM_CUR_TEMP` SMN register through
ZenStates-Core's signed PawnIO `AMDFamily17` read-only port. The adapter does
not call the upstream convenience method because that method can write a
mutex-timeout diagnostic directly to stdout. Instead, the hardware layer
returns the raw register value to a pure decoder that applies the range bit,
legacy product-specific Tctl offsets, sentinel rejection, and a conservative
plausibility range. All diagnostics remain owned by `CommandRunner` and are
written to stderr.

`--stream-telemetry` reuses one controller session and emits JSON Lines with
this schema-1 contract:

```text
kind, schemaVersion, sequence, elapsedMilliseconds, timestampUtc,
vcoreVolts, cpuTemperatureCelsius, temperatureSource
```

`kind` is `telemetry`, `temperatureSource` is `tctl-tdie`, and sequence starts
at zero. The default cadence is 500 ms and the accepted range is 100 through
60000 ms. Vcore is read first and temperature second; stdout is not touched
until both reads succeed, so consumers never receive a half-sample. Either
failure terminates with exit code 6, while an unsupported mapping returns code
3 before any JSON. Ctrl+C is a successful cancellation. Scheduling uses the
same missed-slot policy as the Vcore stream, keeping elapsed milliseconds
strictly increasing without immediate catch-up records.

## Core coordinates

The CLI exposes two distinct index spaces:

- **Compact operable-core index**: zero-based index over selectors that answer
  the current Curve Optimizer read probe. Used by
  `--get-offsets-terse`, `--offset`, and `--get-enabled-cores`.
- **Physical core slot**: fixed slot in the CCD topology, including disabled
  and factory-fused positions. Used by `--disable-cores`.

Candidate selectors are sorted by their SMU physical-selector coordinate
before compact keys are assigned. A failed read is never treated as a core.
The topology-derived enabled-core count is diagnostic metadata only. A fully
healthy map finishes in one pass, while every failed selector is retried even
when a possibly stale count happens to match the partial result. Each failed
selector receives up to three focused attempts before a persistently smaller
operable set is accepted instead of restoring a topology gate. This
avoids repeating healthy reads even though consumers launch a new CLI process
for frequent measurements. The enabled-CCD bitmap is an optional discovery
hint, so a surviving CCD1 retains its real selector even if the reported
active CCD count was compacted. On dual-CCD-capable Raphael/Dragon Range and
Granite Ridge/Fire Range silicon, both possible selectors are probed when that
hint is absent. When a nonzero single-CCD bitmap is stale, the opposite
selector is probed only after every primary selector fails; the healthy path
therefore remains one read per primary candidate.

ZenStates-Core converts each admitted coordinate to the family-specific SMU
mask used for writes. This is important on Zen and Zen 2, where an eight-core
CCD consists of two four-core CCXs, while later desktop families use a single
eight-core CCX. APU offset reads preserve the complete compact selector index
instead of wrapping indices above seven. The Nth terse read and compact write
key N always resolve through the same `CoreAddress` map within an invocation.
The extended APU selector mapping is structurally implemented but not yet
real-hardware-qualified. Immediate post-write read-back returns exit code 6 on
rejection, read failure, or mismatch, but does not by itself qualify physical
target identity above index 7.

Factory fuse maps, exact physical-slot counts, and topology-derived expected
core counts are not qualifications for compact offsets. They remain strict
requirements for physical downcore operations, whose index space and risk are
different.

## Downcore writes

Downcore changes use the motherboard-provided `AMD_ACPI` WMI interface rather
than an SMU mailbox. The request is a complete desired disable mask for every
CCD. Unspecified slots are enabled, and the change requires a reboot.

The application rejects:

- negative, duplicate, or out-of-range physical slot indices;
- a request that disables every physical slot;
- missing AMD ACPI commands;
- incomplete per-CCD results;
- access, initialization, and WMI failures.

## Native and managed packaging

The managed application is published as a single file. `inpoutx64.dll` remains
adjacent because ZenStates-Core loads it by name and it embeds the required
driver. PawnIO is different: its signed kernel driver must be installed
system-wide, while the signed hardware modules loaded into that driver are
embedded in ZenStates-Core. License and provenance files are external so users
can inspect them without unpacking the managed executable.

The publish target explicitly restores the renamed
`ZenStates-Core.LICENSE.txt` after project-reference conflict resolution and
then verifies all required runtime files. Release packaging additionally
checks archive membership, excludes ambiguous raw dependency documents,
compares the embedded ZenStates-Core license hash with the submodule source,
and verifies that the PawnIO bundle differs from the ordinary self-contained
package only by the pinned installer.

Release automation produces:

- a smaller framework-dependent win-x64 package;
- a self-contained win-x64 package with the .NET 8 runtime;
- a self-contained package that also carries the verified official PawnIO
  installer for interactive administrator-approved installation;
- a separate symbols package;
- SHA-256 checksums for every archive.

See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for the dependency
inventory.
