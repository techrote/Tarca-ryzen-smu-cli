# TARCA-002 — Vermeer configured power-limit readback

This note records the evidence and qualification boundary for Tarca's
configured PPT/TDC/EDC readback on Zen 3 Vermeer.

## Target identity

The first target is the physically exercised Ryzen 5 5600X platform already
recorded in
`docs/hardware/VERMEER-5600X-B450-AORUS-PRO-PPT.md`:

- family/model: `19h/21h`;
- package: AM4 (`2`);
- PM table: `0x00380905`;
- PM-table structure size: `0x5D0` / 1488 bytes;
- SMU firmware observed by the operator: 56.70.0.

Tarca admits the PM-table mapping only when all five identity fields match.
It does not select the layout by marketing name.

## ZenStates system-power query: negative control

Tarca pins ZenStates-Core commit
`999cdb6dec5ccb6dcde37a02ad1d6d878cb55cbf`.

That exact dependency contains
`Cpu.GetSystemPowerLimit()` /
`GetSystemConfiguredPowerLimit`. Vermeer's `Zen3Settings` inherits from
`Zen2Settings`, whose MP1 mailbox defines
`SMU_MSG_GetSustainedPowerAndThmLimit = 0x23`.

ZenStates-Core decodes a power value from bits 23:16 of the first response
word. TARCA-002 initially treated that value as an independent configured-PPT
query.

Physical hardware disproved that interpretation on the qualified 5600X:
with runtime PPT manually set to 92 W, PM-table `d[0]` read exactly 92 W
while `GetSystemPowerLimit()` returned 65 W. The latter therefore does not
track the active runtime PPT override on this Vermeer firmware.

Tarca retains this mismatch as a negative control and does **not** use
`GetSystemPowerLimit()` to validate or reject configured-PPT readback.

## PM-table limit indices

Historical Ryzen 5 5600X work in
`hattedsquirrel/ryzen_monitor#1` maps the `0x380904` Zen 3 table front as:

| Index | Meaning |
| ---: | --- |
| 0 | PPT limit |
| 1 | PPT live value |
| 2 | TDC limit |
| 3 | TDC live value |
| 4 | thermal limit |
| 5 | thermal live value |
| 6 | FIT limit |
| 7 | FIT live value |
| 8 | EDC limit |
| 9 | EDC live value |

The same issue documents the later `0x380905` revision. The identified
layout change is an additional eight-float per-core block around index 300;
the later per-core/L3 offsets shift by eight entries. Nothing is inserted
before the front limit/value block.

Independent 2026 work in `Kyworn/gnr-smu` uses the exact
`0x380905` / 1488-byte / SMU 56.70.0 Vermeer identity and confirms the
front-of-table region is present while deliberately declining to label the
constant limit pool without a write-back discriminator. That project
independently confirms `d[1]` as live socket/package-power telemetry.

Tarca has the missing discriminator for PPT: the operator changed PPT at
runtime and observed PM-table `d[0]` track the applied 92 W setting exactly,
while the ZenStates system-power query remained at 65 W.

Therefore the exact Tarca mapping is:

- PPT configured limit: `d[0]`;
- TDC configured limit: `d[2]`;
- EDC configured limit: `d[8]`.

## Confidence and output contract

### PPT

PPT is fail-closed and hardware-qualified at read time:

1. the exact Vermeer/AM4/`0x380905`/`0x5D0` layout must match;
2. the PM-table value at `d[0]` must be finite, plausible and effectively
   integral.

PM-table `d[0]` is physically write/readback-qualified at a 92 W runtime PPT
setting on the recorded 5600X platform. Tarca prints the result as
**PM-table hardware-qualified readback**.

The ZenStates system-power query is deliberately not part of this acceptance
path because it returned 65 W while the active PPT and `d[0]` were 92 W.

### TDC / EDC

TDC and EDC use the exact PM-table structural mapping at `d[2]` and `d[8]`.
They are intentionally labelled **PM-table structural mapping** until a
controlled physical write/read-back experiment qualifies each field.

No TDC/EDC write is needed for software CI. The highest physically exercised
PPT setting currently recorded is 92 W; no test above that value is required.

## Live telemetry is separate

HWiNFO package-power telemetry was observed roughly 2 W above the requested
PPT setpoint under load on the first Tarca test machine. That observation is
not a configured-limit readback and is not used by this implementation.

Tarca never subtracts 2 W, estimates a limit from live package power, or
silently changes a requested setpoint.

## Fail-closed conditions

Configured limit readback is refused when:

- CPU family/model/package does not match;
- PM-table version or structure size does not match;
- the PM table is unavailable or too short;
- any mapped limit is non-finite, implausible or materially non-integral.

This deliberately prefers an unsupported/error result over a plausible but
wrong configured limit.
