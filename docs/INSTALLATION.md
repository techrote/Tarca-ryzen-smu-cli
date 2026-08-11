# Installation

`ryzen-smu-cli` is a Windows x64 application. Hardware commands require both
administrator privileges and the signed PawnIO system driver. The signed
RyzenSMU, AMDFamily17, and SmbusPIIX4 PawnIO modules are already embedded in
ZenStates-Core and do not need a separate installation.

## Recommended clean-system setup

1. Download
   `ryzen-smu-cli-vX.Y.Z-win-x64-self-contained-with-pawnio.zip` and
   `checksums-sha256.txt` from the same GitHub Release.
2. Verify the ZIP hash:

   ```powershell
   Get-FileHash `
     .\ryzen-smu-cli-vX.Y.Z-win-x64-self-contained-with-pawnio.zip `
     -Algorithm SHA256
   Get-Content .\checksums-sha256.txt
   ```

3. Extract the complete archive.
4. Right-click `PawnIO_setup-v2.2.0.exe`, select **Run as administrator**, and
   install the signed official edition. Do not select the unsigned unrestricted
   edition.
5. Restart Windows if the installer requests it.
6. Open an Administrator PowerShell in the extracted directory and begin with
   a read:

   ```powershell
   .\ryzen-smu-cli.exe --get-pbo-scalar
   ```

The PawnIO installer is redistributed unmodified from the official
[PawnIO.Setup 2.2.0 release](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0).
Release packaging verifies its SHA-256 and Authenticode signer before adding it
to the bundle.

## Existing PawnIO installation

If PawnIO is already installed, use either the ordinary self-contained package
or the smaller framework-dependent package. PawnIO is installed system-wide
and can be shared by applications that use official PawnIO modules.

The framework-dependent package additionally requires the .NET 8 x64 runtime.
The self-contained packages include that runtime.

## Upgrading ryzen-smu-cli

Extract the new release into a new directory. Do not copy only the executable:
`inpoutx64.dll`, notices, and license files must remain beside it. An existing
compatible PawnIO installation does not need to be reinstalled for every
ryzen-smu-cli update.

## System telemetry consumers

The schema-2 system stream requires an Administrator PowerShell and a complete
Windows `group:processor` target list. It briefly pins its worker thread to
each requested logical processor while reading APERF/MPERF, then restores the
previous affinity. AMD RAPL package energy, mapped Vcore, Tctl/Tdie, and every
target must all be readable; a failure stops the stream instead of returning a
partial topology.

Do not preserve a running stream across Windows suspend/resume. Stop and start
a new process after resume. The new process consumes one silent baseline
snapshot before sequence zero, ensuring that pre-suspend counter values are
not used. At the 500 ms default, an actual measurement interval reaching 1500
ms is also rejected as stale.

## Troubleshooting

### PawnIO is required but is not installed

Install PawnIO from the bundled installer or from
[pawnio.eu](https://pawnio.eu/), restart if requested, and run the command
again from an Administrator PowerShell.

The application detects installation using PawnIO's system registration.
Copying `PawnIO.sys` or the installer beside `ryzen-smu-cli.exe` is not
sufficient.

### PawnIO is installed but initialization still fails

- Confirm the terminal is elevated.
- Reboot after installing or upgrading PawnIO.
- Repair or reinstall the signed official PawnIO edition.
- Check whether security or anti-cheat software is preventing the driver from
  loading.
- Include the full error, CPU, motherboard, BIOS/AGESA, Windows, PawnIO, and
  ryzen-smu-cli versions in a bug report.

### Removing PawnIO

PawnIO is a shared system component. Before uninstalling it, close
ryzen-smu-cli and any other hardware-monitoring applications that use it.
Remove PawnIO through Windows **Installed apps**. Other applications that
depend on PawnIO will stop working until it is reinstalled.
