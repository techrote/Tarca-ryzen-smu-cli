# Vermeer Ryzen 5 5600X PPT hardware qualification

Date: 2026-10-16

This record captures the first physical hardware qualification of Tarca's
`--set-ppt` path. It is deliberately limited to the evidence actually
observed. It does not qualify `--set-tdc`, `--set-edc`, Vcore mapping, or
every Vermeer motherboard/firmware combination.

## System identity

| Item | Observed value |
| --- | --- |
| CPU | AMD Ryzen 5 5600X 6-Core Processor |
| CPUID | `A20F10` |
| Codename | Vermeer |
| Model | `21` |
| Package | AM4 |
| Topology | 1 CCD / 1 CCX / 6 physical cores / 12 logical processors |
| SMT | Enabled |
| Motherboard vendor | Gigabyte Technology Co., Ltd. |
| Motherboard | B450 AORUS PRO-CF |
| BIOS | F63d |
| CPU firmware | `0A201016` |
| SMU | `56.70.0` |
| PM-table version | `0x00380905` |
| PM-table size | `0x000005D0` |
| Tarca Vcore mapping | Unmapped |
| PBO scalar after PPT test | 1 |
| FMax after PPT test | 4650 MHz |

## Qualified operation

The operator exercised Tarca's independent PPT write:

```powershell
.\ryzen-smu-cli.exe --set-ppt 84
```

Tarca reported:

```text
Set PPT limit to 84 W.
```

The Vermeer SMU therefore accepted the requested PPT write through Tarca's
ZenStates-Core/PawnIO path.

The operator tested multiple PPT setpoints and observed HWiNFO package-power
telemetry settling almost exactly 2 W above the requested Tarca setpoint under
load. The highest requested PPT tested was 84 W; testing above 84 W was
intentionally stopped for thermal reasons.

The approximately +2 W observation is **live package-power telemetry**, not a
direct readback of the configured PPT limit. It may reflect measurement
definition, sampling, SMU accounting, or sensor presentation differences and
must not be encoded as a correction factor.

## Post-write observations

After the 84 W write, Tarca reported:

```text
CPU        AMD Ryzen 5 5600X 6-Core Processor
CPUID      A20F10 (Vermeer)
Model      21
Package    AM4
Config     1 CCD / 1 CCX / 6 physical cores
Logical    12 logical processors
SMT        Enabled (2 threads per core)
MB Vendor  Gigabyte Technology Co., Ltd.
MB Model   B450 AORUS PRO-CF
BIOS       F63d
Firmware   0A201016
SMU        56.70.0
PM Version 0x00380905
PM Size    0x000005D0 bytes
Vcore Map  Unmapped
Current PBO scalar: 1
Current FMax: 4650 MHz.
```

These are post-write observations. Without a same-session pre-write baseline,
they are not by themselves proof that PBO scalar or FMax were unchanged.

## Qualification boundary

Qualified:

- Tarca initializes successfully on this Vermeer platform through signed PawnIO.
- `--set-ppt` is accepted by the SMU.
- Requested PPT changes produce corresponding bounded changes in observed
  package-power behavior.
- PPT requests through 84 W were physically exercised.

Not yet qualified:

- direct configured-PPT readback;
- `--set-tdc` physical behavior;
- `--set-edc` physical behavior;
- a Vermeer Vcore mapping for PM table `0x00380905`;
- operation above 84 W on this machine;
- other Vermeer boards, BIOS/AGESA revisions, or CPUs.

## Follow-up

See TARCA-002 for configured-limit readback and Vermeer PM-table qualification.
A readback implementation must distinguish configured PPT/TDC/EDC limits from
live package-power/current telemetry and must not infer a configured limit by
subtracting the observed approximately 2 W difference.
