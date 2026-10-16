using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ryzen_smu_cli;

internal sealed class CommandRunner
{
    private readonly Func<IRyzenController> _controllerFactory;
    private readonly IPrivilegeChecker _privilegeChecker;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly CancellationToken _cancellationToken;

    public CommandRunner(
        Func<IRyzenController> controllerFactory,
        IPrivilegeChecker privilegeChecker,
        TextWriter output,
        TextWriter error)
        : this(
            controllerFactory,
            privilegeChecker,
            output,
            error,
            CancellationToken.None)
    {
    }

    public CommandRunner(
        Func<IRyzenController> controllerFactory,
        IPrivilegeChecker privilegeChecker,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        _controllerFactory = controllerFactory;
        _privilegeChecker = privilegeChecker;
        _output = output;
        _error = error;
        _cancellationToken = cancellationToken;
    }

    public int Execute(CliRequest request)
    {
        if (!_privilegeChecker.IsWindows)
        {
            _error.WriteLine("This application can only access the SMU on Windows.");
            return (int)ExitCode.UnsupportedPlatform;
        }

        if (!_privilegeChecker.IsAdministrator)
        {
            _error.WriteLine("This application must be run as an administrator.");
            return (int)ExitCode.NotAdministrator;
        }

        IRyzenController controller;
        try
        {
            controller = _controllerFactory();
        }
        catch (PawnIoNotInstalledException)
        {
            _error.WriteLine(
                "PawnIO is required for Ryzen SMU access but is not installed.");
            _error.WriteLine(
                "Install the signed PawnIO driver from https://pawnio.eu/.");
            _error.WriteLine(
                "The self-contained-with-pawnio release package includes the official " +
                "installer. Run it interactively as an administrator, then retry this command.");
            return (int)ExitCode.InitializationFailed;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Failed to initialize Ryzen hardware access: {ex.Message}");
            return (int)ExitCode.InitializationFailed;
        }

        using (controller)
        {
            try
            {
                return Execute(request, controller);
            }
            catch (Exception ex)
            {
                _error.WriteLine($"Hardware operation failed: {ex.Message}");
                return (int)ExitCode.OperationFailed;
            }
        }
    }

    private int Execute(CliRequest request, IRyzenController controller)
    {
        CompactCoreMap? coreMap = null;

        if (NeedsCoreMap(request))
        {
            OperationResult<CompactCoreMap> mapResult =
                CoreMapper.Map(controller);
            if (!mapResult.Success)
            {
                _error.WriteLine(mapResult.Error);
                return (int)ExitCode.CoreMappingFailed;
            }

            coreMap = mapResult.Value!;
        }

        int validationResult = ValidateRequest(request, controller, coreMap);
        if (validationResult != (int)ExitCode.Success)
        {
            return validationResult;
        }

        bool rebootRequired = false;

        if (request.DiagnoseVcore)
        {
            return WriteVcoreDiagnosticReport(controller, request);
        }

        if (request.ShowInfo)
        {
            WriteCpuInformation(controller.Information);
        }

        if (request.OffsetSpecification is not null)
        {
            foreach (OffsetAssignment assignment in
                     request.OffsetSpecification.Assignments
                         .OrderBy(assignment => assignment.EnabledCoreIndex))
            {
                CoreAddress core =
                    coreMap!.GetAddress(assignment.EnabledCoreIndex);
                OperationResult result =
                    controller.SetPboOffset(core, assignment.Offset);
                if (!result.Success)
                {
                    _error.WriteLine(result.Error);
                    return (int)ExitCode.OperationFailed;
                }

                OperationResult<int> readBack = controller.GetPboOffset(core);
                if (!readBack.Success)
                {
                    _error.WriteLine(
                        $"Offset {assignment.Offset} was accepted for enabled core " +
                        $"{assignment.EnabledCoreIndex}, but read-back failed: " +
                        readBack.Error);
                    return (int)ExitCode.OperationFailed;
                }

                if (readBack.Value != assignment.Offset)
                {
                    _error.WriteLine(
                        $"Offset read-back mismatch for enabled core " +
                        $"{assignment.EnabledCoreIndex}: requested " +
                        $"{assignment.Offset}, read {readBack.Value}.");
                    return (int)ExitCode.OperationFailed;
                }

                coreMap = coreMap.WithOffset(
                    assignment.EnabledCoreIndex,
                    readBack.Value);

                _output.WriteLine(
                    $"Set enabled core {assignment.EnabledCoreIndex}, physical core " +
                    $"{core.PhysicalCoreIndex} offset to {assignment.Offset} " +
                    "(verified).");
            }
        }

        if (request.DisabledCores is not null || request.EnableAllCores)
        {
            IReadOnlySet<int> disabledCores =
                request.DisabledCores?.PhysicalCoreIndices ?? new HashSet<int>();
            DowncoreOperationResult result =
                controller.SetDisabledCores(disabledCores);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.DowncoreUnavailable;
            }

            if (result.DisableMasks.Count != controller.CcdCount)
            {
                _error.WriteLine(
                    "AMD ACPI returned an incomplete core-disable result.");
                return (int)ExitCode.DowncoreUnavailable;
            }

            for (int ccdIndex = 0; ccdIndex < result.DisableMasks.Count; ccdIndex++)
            {
                string bitmap = Convert
                    .ToString(result.DisableMasks[ccdIndex], 2)
                    .PadLeft(8, '0');
                _output.WriteLine(
                    $"New core disable bitmap for CCD{ccdIndex}: {bitmap}");
            }

            rebootRequired = true;
        }

        if (request.GetOffsetsTerse)
        {
            List<string> offsets = new(coreMap!.Count);
            foreach (CompactCoreEntry entry in
                     coreMap.Entries.OrderBy(entry => entry.CompactIndex))
            {
                offsets.Add(entry.Offset.ToString(
                    CultureInfo.InvariantCulture));
            }

            _output.WriteLine(string.Join(",", offsets));
        }

        if (request.GetPhysicalCores)
        {
            _output.WriteLine("Factory-fused status of physical core slots:");
            for (int physicalCoreIndex = 0;
                 physicalCoreIndex < controller.PhysicalCoreSlots;
                 physicalCoreIndex++)
            {
                CoreAddress core =
                    CoreAddress.FromPhysicalCoreIndex(physicalCoreIndex);
                bool disabled =
                    (controller.FactoryCoreDisableMasks[core.CcdIndex] &
                     (1 << core.CoreIndex)) != 0;
                _output.WriteLine(
                    $"Physical core {physicalCoreIndex}: " +
                    (disabled ? "Disabled" : "Enabled"));
            }
        }

        if (request.GetEnabledCores)
        {
            Dictionary<int, int> enabledCoreByPhysicalIndex = coreMap!
                .Entries.ToDictionary(
                    entry => entry.Address.PhysicalCoreIndex,
                    entry => entry.CompactIndex);

            int reportedSlots = Math.Max(
                controller.PhysicalCoreSlots,
                enabledCoreByPhysicalIndex.Count == 0
                    ? 0
                    : checked(enabledCoreByPhysicalIndex.Keys.Max() + 1));

            for (int physicalCoreIndex = 0;
                 physicalCoreIndex < reportedSlots;
                 physicalCoreIndex++)
            {
                if (enabledCoreByPhysicalIndex.TryGetValue(
                        physicalCoreIndex,
                        out int enabledCoreIndex))
                {
                    _output.WriteLine(
                        $"Physical core {physicalCoreIndex}: Enabled " +
                        $"(enabled core {enabledCoreIndex})");
                }
                else
                {
                    _output.WriteLine(
                        $"Physical core {physicalCoreIndex}: Disabled");
                }
            }
        }

        if (request.PboScalar is int scalar)
        {
            OperationResult result = controller.SetPboScalar(scalar);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Set PBO scalar to {scalar}.");
        }

        if (request.GetPboScalar)
        {
            OperationResult<float> result = controller.GetPboScalar();
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine(
                $"Current PBO scalar: " +
                result.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (request.PptWatts is uint pptWatts)
        {
            OperationResult result = controller.SetPptLimit(pptWatts);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Set PPT limit to {pptWatts} W.");
        }

        if (request.TdcAmps is uint tdcAmps)
        {
            OperationResult result = controller.SetTdcLimit(tdcAmps);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Set TDC limit to {tdcAmps} A.");
        }

        if (request.EdcAmps is uint edcAmps)
        {
            OperationResult result = controller.SetEdcLimit(edcAmps);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Set EDC limit to {edcAmps} A.");
        }

        if (request.FMax is FMaxFrequency fMax)
        {
            OperationResult result = controller.SetFMax(fMax.Megahertz);
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Set FMax to {fMax.Megahertz} MHz.");
        }

        if (request.GetFMax)
        {
            OperationResult<uint> result = controller.GetFMax();
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine($"Current FMax: {result.Value} MHz.");
        }

        if (request.GetVcore)
        {
            OperationResult<double> result = controller.GetVcore();
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine(
                $"Current Vcore: " +
                $"{result.Value.ToString("F6", CultureInfo.InvariantCulture)} V.");
        }

        if (request.GetCpuTemperature)
        {
            OperationResult<double> result = controller.GetCpuTemperature();
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            _output.WriteLine(
                $"Current CPU temperature: " +
                $"{result.Value.ToString("F3", CultureInfo.InvariantCulture)} C.");
        }

        if (request.VcoreStreamIntervalMilliseconds is int streamInterval)
        {
            return StreamVcore(controller, streamInterval);
        }

        if (request.TelemetryStreamIntervalMilliseconds is
            int telemetryStreamInterval)
        {
            return StreamTelemetry(controller, telemetryStreamInterval);
        }

        if (request.SystemTelemetryStreamIntervalMilliseconds is
                int systemTelemetryStreamInterval &&
            request.SystemTelemetryTargets is not null)
        {
            return StreamSystemTelemetry(
                controller,
                systemTelemetryStreamInterval,
                request.SystemTelemetryTargets.Targets);
        }

        if (rebootRequired)
        {
            _output.WriteLine("A reboot is required for changes to take effect.");
        }

        return (int)ExitCode.Success;
    }

    private int WriteVcoreDiagnosticReport(
        IRyzenController controller,
        CliRequest request)
    {
        int sampleCount = request.VcoreDiagnosticSampleCount;
        int intervalMilliseconds = request.VcoreDiagnosticIntervalMilliseconds;
        Stopwatch elapsed = Stopwatch.StartNew();
        List<VcoreDiagnosticSample> samples = new(sampleCount);
        IReadOnlyList<VcoreDiagnosticRegisterDescriptor> descriptors =
            VcoreDiagnostics.ResolveRegisters(
                controller.CpuFamily,
                controller.CpuModel,
                controller.CpuPackage);
        VcoreDiagnosticSource sourceDescriptor =
            ResolveVcoreDiagnosticSource(controller);

        for (int sequence = 0;
             sequence < sampleCount && !_cancellationToken.IsCancellationRequested;
             sequence++)
        {
            long sampleStartedMilliseconds = elapsed.ElapsedMilliseconds;
            IReadOnlyDictionary<uint, OperationResult<uint>> rawReadings =
                controller.ReadVcoreDiagnosticRegisters();
            OperationResult<double> selected = ReadSelectedDiagnosticVcore(
                controller,
                rawReadings);

            List<VcoreDiagnosticRegisterReading> registers =
                new(descriptors.Count);
            foreach (VcoreDiagnosticRegisterDescriptor descriptor in
                     descriptors)
            {
                registers.Add(ReadVcoreDiagnosticRegister(
                    rawReadings,
                    descriptor));
            }

            samples.Add(new VcoreDiagnosticSample(
                sequence,
                sampleStartedMilliseconds,
                elapsed.ElapsedMilliseconds,
                new VcoreDiagnosticSelectedReading(
                    selected.Success,
                    selected.Success ? selected.Value : null,
                    selected.Error),
                registers));

            if (sequence + 1 >= sampleCount)
            {
                continue;
            }

            long nextDue = checked((long)(sequence + 1) * intervalMilliseconds);
            long remaining = nextDue - elapsed.ElapsedMilliseconds;
            if (remaining > 0 &&
                _cancellationToken.WaitHandle.WaitOne(checked((int)remaining)))
            {
                break;
            }
        }

        CpuInformation information = controller.Information;
        string source = information.VcoreTelemetrySource;
        int selectedSuccessCount = samples.Count(sample => sample.Selected.Success);
        int selectedFailureCount = samples.Count - selectedSuccessCount;
        int registerSuccessCount = samples.Sum(sample =>
            sample.Registers.Count(register => register.Success));
        int registerFailureCount = samples.Sum(sample =>
            sample.Registers.Count(register => !register.Success));
        string selectionState;
        string? selectionReason;
        if (samples.Count == 0 && _cancellationToken.IsCancellationRequested)
        {
            selectionState = "cancelled";
            selectionReason = "Capture was cancelled before the first sample.";
        }
        else if (sourceDescriptor.Kind == "unsupported")
        {
            selectionState = "unsupported";
            selectionReason = controller.VcoreReadUnavailableReason;
        }
        else if (selectedSuccessCount == 0)
        {
            selectionState = "read-failed";
            selectionReason = samples
                .Select(sample => sample.Selected.Error)
                .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
        }
        else if (selectedFailureCount > 0 || registerFailureCount > 0)
        {
            selectionState = "partial";
            selectionReason = "One or more selected or raw-register reads failed.";
        }
        else
        {
            selectionState = sourceDescriptor.Confidence;
            selectionReason = null;
        }

        VcoreDiagnosticReport report = new(
            1,
            typeof(CommandRunner).Assembly.GetName().Version?.ToString(3) ??
                "unknown",
            DateTimeOffset.UtcNow,
            new VcoreDiagnosticCpu(
                information.CpuName,
                information.CpuId,
                $"0x{controller.CpuFamily:X2}",
                $"0x{controller.CpuModel:X2}",
                $"0x{controller.CpuPackage:X}",
                information.CodeName,
                information.CcdCount,
                information.PhysicalCoreCount,
                information.LogicalProcessorCount,
                information.ThreadsPerCore,
                information.SmtEnabled,
                controller.PhysicalCoreSlots,
                controller.EnabledCoreCount,
                controller.HasUsableCoreTopology,
                controller.CoreTopologyUnavailableReason,
                information.MotherboardVendor,
                information.MotherboardModel,
                information.BiosVersion,
                information.FirmwareVersion,
                information.SmuVersion,
                $"0x{information.PmTableVersion:X8}",
                $"0x{information.PmTableSize:X8}"),
            source,
            sourceDescriptor,
            selectionState,
            selectionReason,
            selectedSuccessCount,
            selectedFailureCount,
            registerSuccessCount,
            registerFailureCount,
            sampleCount,
            samples.Count,
            _cancellationToken.IsCancellationRequested,
            intervalMilliseconds,
            samples);

        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        _output.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
        return (int)ExitCode.Success;
    }

    private static VcoreDiagnosticSource ResolveVcoreDiagnosticSource(
        IRyzenController controller)
    {
        if (SviTfnVcoreTelemetry.TryResolve(
                controller.CpuFamily,
                controller.CpuModel,
                out SviTfnVcoreTelemetry? sviLayout))
        {
            return new VcoreDiagnosticSource(
                "smn-svi",
                FormatDiagnosticConfidence(sviLayout!.Confidence),
                sviLayout.PlatformName,
                $"0x{sviLayout.CorePlaneRegister:X8}",
                sviLayout.VidShift,
                sviLayout.StatusRegister is uint statusRegister
                    ? $"0x{statusRegister:X8}"
                    : null,
                null,
                null,
                null);
        }

        CpuInformation information = controller.Information;
        if (VcoreTelemetryLayout.TryResolve(
                controller.CpuFamily,
                controller.CpuModel,
                controller.CpuPackage,
                information.PmTableVersion,
                information.PmTableSize,
                out VcoreTelemetryLayout? pmLayout))
        {
            return new VcoreDiagnosticSource(
                "pm-table",
                FormatDiagnosticConfidence(pmLayout!.Confidence),
                information.CodeName,
                null,
                null,
                null,
                $"0x{pmLayout.PmTableVersion:X8}",
                $"0x{pmLayout.PmTableSize:X8}",
                pmLayout.ValueIndex);
        }

        return new VcoreDiagnosticSource(
            "unsupported",
            "unsupported",
            information.CodeName,
            null,
            null,
            null,
            $"0x{information.PmTableVersion:X8}",
            $"0x{information.PmTableSize:X8}",
            null);
    }

    private static string FormatDiagnosticConfidence(
        VcoreMappingConfidence confidence) => confidence switch
        {
            VcoreMappingConfidence.Verified => "verified",
            VcoreMappingConfidence.Structural => "structural-candidate",
            _ => "unsupported",
        };

    private static OperationResult<double> ReadSelectedDiagnosticVcore(
        IRyzenController controller,
        IReadOnlyDictionary<uint, OperationResult<uint>> rawReadings)
    {
        if (!SviTfnVcoreTelemetry.TryResolve(
                controller.CpuFamily,
                controller.CpuModel,
                out SviTfnVcoreTelemetry? layout))
        {
            return controller.CanReadVcore
                ? controller.GetVcore()
                : OperationResult<double>.Fail(
                    controller.VcoreReadUnavailableReason ??
                    "No mapped Vcore source is available for this CPU.");
        }

        if (layout!.StatusRegister is uint statusAddress)
        {
            if (!rawReadings.TryGetValue(
                    statusAddress,
                    out OperationResult<uint> statusRead) ||
                !statusRead.Success)
            {
                return OperationResult<double>.Fail(
                    statusRead.Error ??
                    $"SMN status register 0x{statusAddress:X8} was not captured.");
            }

            OperationResult status = layout.ValidateStatus(statusRead.Value);
            if (!status.Success)
            {
                return OperationResult<double>.Fail(status.Error!);
            }
        }

        if (!rawReadings.TryGetValue(
                layout.CorePlaneRegister,
                out OperationResult<uint> coreRead) ||
            !coreRead.Success)
        {
            return OperationResult<double>.Fail(
                coreRead.Error ??
                $"SMN core-plane register " +
                $"0x{layout.CorePlaneRegister:X8} was not captured.");
        }

        return layout.Decode(coreRead.Value);
    }

    private static VcoreDiagnosticRegisterReading ReadVcoreDiagnosticRegister(
        IReadOnlyDictionary<uint, OperationResult<uint>> rawReadings,
        VcoreDiagnosticRegisterDescriptor descriptor)
    {
        uint address = descriptor.Address;
        OperationResult<uint> read = rawReadings.TryGetValue(
            address,
            out OperationResult<uint> captured)
            ? captured
            : OperationResult<uint>.Fail(
                $"SMN register 0x{address:X8} was not captured.");
        if (!read.Success)
        {
            return new VcoreDiagnosticRegisterReading(
                $"0x{address:X8}",
                descriptor.Name,
                descriptor.Role,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                read.Error);
        }

        uint raw = read.Value;
        OperationResult<double>? highVid = descriptor.DecodeCoreVidCandidates
            ? SviTfnVcoreTelemetry.DecodeCandidate(raw, 16)
            : null;
        OperationResult<double>? lowVid = descriptor.DecodeCoreVidCandidates
            ? SviTfnVcoreTelemetry.DecodeCandidate(raw, 8)
            : null;
        double? highVoltage = highVid.HasValue && highVid.Value.Success
            ? highVid.Value.Value
            : null;
        double? lowVoltage = lowVid.HasValue && lowVid.Value.Success
            ? lowVid.Value.Value
            : null;

        return new VcoreDiagnosticRegisterReading(
            $"0x{address:X8}",
            descriptor.Name,
            descriptor.Role,
            true,
            $"0x{raw:X8}",
            BitConverter.GetBytes(raw),
            descriptor.DecodeCoreVidCandidates ? (raw >> 16) & 0xFF : null,
            highVoltage,
            descriptor.DecodeCoreVidCandidates ? (raw >> 8) & 0xFF : null,
            lowVoltage,
            descriptor.DecodeLegacyStatus ? (raw & 0x1) != 0 : null,
            descriptor.DecodeLegacyStatus ? (raw & 0x2) != 0 : null,
            descriptor.DecodeFamily1AHardwareVid
                ? (raw >> 6) & 0x1FF
                : null,
            !descriptor.DecodeCoreVidCandidates ||
            highVoltage.HasValue ||
            lowVoltage.HasValue
                ? null
                : highVid?.Error ?? lowVid?.Error);
    }

    private int StreamVcore(IRyzenController controller, int intervalMilliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        long nextSampleDueMilliseconds = 0;
        long sequence = 0;

        while (!_cancellationToken.IsCancellationRequested)
        {
            OperationResult<double> result = controller.GetVcore();
            if (!result.Success)
            {
                _error.WriteLine(result.Error);
                return (int)ExitCode.OperationFailed;
            }

            long sampleElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            string timestamp = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
            string voltage = result.Value.ToString(
                "F6",
                CultureInfo.InvariantCulture);
            _output.WriteLine(
                $"VCORE\t{sequence}\t{sampleElapsedMilliseconds}\t" +
                $"{timestamp}\t{voltage}");
            _output.Flush();
            sequence++;

            long schedulingElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            // Skip expired cadence points so a slow read never triggers an
            // immediate catch-up read with the same millisecond timestamp.
            nextSampleDueMilliseconds =
                VcoreStreaming.GetNextSampleDueMilliseconds(
                    nextSampleDueMilliseconds,
                    schedulingElapsedMilliseconds,
                    intervalMilliseconds);
            long remainingMilliseconds =
                nextSampleDueMilliseconds - schedulingElapsedMilliseconds;
            if (_cancellationToken.WaitHandle.WaitOne(
                    checked((int)remainingMilliseconds)))
            {
                break;
            }
        }

        return (int)ExitCode.Success;
    }

    private int StreamTelemetry(
        IRyzenController controller,
        int intervalMilliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        long nextSampleDueMilliseconds = 0;
        long sequence = 0;

        while (!_cancellationToken.IsCancellationRequested)
        {
            OperationResult<double> vcore = controller.GetVcore();
            if (!vcore.Success)
            {
                _error.WriteLine(vcore.Error);
                return (int)ExitCode.OperationFailed;
            }

            OperationResult<double> temperature =
                controller.GetCpuTemperature();
            if (!temperature.Success)
            {
                _error.WriteLine(temperature.Error);
                return (int)ExitCode.OperationFailed;
            }

            long sampleElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            CpuTelemetryStreamSample sample = new(
                CpuTelemetryStreamSample.RecordKind,
                CpuTelemetryStreamSample.CurrentSchemaVersion,
                sequence,
                sampleElapsedMilliseconds,
                DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                vcore.Value,
                temperature.Value,
                CpuTemperatureTelemetry.PublicSourceName);
            _output.WriteLine(JsonSerializer.Serialize(sample));
            _output.Flush();
            sequence++;

            long schedulingElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            nextSampleDueMilliseconds =
                VcoreStreaming.GetNextSampleDueMilliseconds(
                    nextSampleDueMilliseconds,
                    schedulingElapsedMilliseconds,
                    intervalMilliseconds);
            long remainingMilliseconds =
                nextSampleDueMilliseconds - schedulingElapsedMilliseconds;
            if (_cancellationToken.WaitHandle.WaitOne(
                    checked((int)remainingMilliseconds)))
            {
                break;
            }
        }

        return (int)ExitCode.Success;
    }

    private int StreamSystemTelemetry(
        IRyzenController controller,
        int intervalMilliseconds,
        IReadOnlyList<SystemTelemetryTarget> targets)
    {
        SystemTelemetryAccumulator accumulator = new(
            intervalMilliseconds,
            targets);
        Stopwatch elapsed = Stopwatch.StartNew();
        long nextSampleDueMilliseconds = 0;
        long sequence = 0;

        while (!_cancellationToken.IsCancellationRequested)
        {
            OperationResult<SystemTelemetryRawSnapshot> rawSnapshot =
                controller.ReadSystemTelemetrySnapshot(targets);
            if (!rawSnapshot.Success)
            {
                _error.WriteLine(rawSnapshot.Error);
                return (int)ExitCode.OperationFailed;
            }

            SystemTelemetryObservation observation =
                accumulator.Observe(rawSnapshot.Value!);
            if (!observation.Success)
            {
                _error.WriteLine(observation.Error);
                return (int)ExitCode.OperationFailed;
            }

            if (!observation.IsBaseline)
            {
                SystemTelemetryMeasurement measurement =
                    observation.Measurement!;
                SystemTelemetryTargetStreamSample[] targetSamples =
                    measurement.Targets
                        .Select(target => new SystemTelemetryTargetStreamSample(
                            target.Target.Group,
                            target.Target.Processor,
                            target.AperfDelta,
                            target.MperfDelta,
                            target.MeasurementIntervalMilliseconds,
                            target.EffectiveMegahertz))
                        .ToArray();
                SystemTelemetryStreamSample sample = new(
                    SystemTelemetryStreamSample.RecordKind,
                    SystemTelemetryStreamSample.CurrentSchemaVersion,
                    sequence,
                    elapsed.ElapsedMilliseconds,
                    DateTimeOffset.UtcNow.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    measurement.MeasurementIntervalMilliseconds,
                    measurement.AcquisitionDurationMilliseconds,
                    measurement.VcoreVolts,
                    measurement.CpuTemperatureCelsius,
                    CpuTemperatureTelemetry.PublicSourceName,
                    measurement.PackagePowerWatts,
                    SystemTelemetryStreaming.PackagePowerSource,
                    targetSamples);
                _output.WriteLine(JsonSerializer.Serialize(sample));
                _output.Flush();
                sequence++;
            }

            long schedulingElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            nextSampleDueMilliseconds =
                VcoreStreaming.GetNextSampleDueMilliseconds(
                    nextSampleDueMilliseconds,
                    schedulingElapsedMilliseconds,
                    intervalMilliseconds);
            long remainingMilliseconds =
                nextSampleDueMilliseconds - schedulingElapsedMilliseconds;
            if (_cancellationToken.WaitHandle.WaitOne(
                    checked((int)remainingMilliseconds)))
            {
                break;
            }
        }

        return (int)ExitCode.Success;
    }

    private void WriteCpuInformation(CpuInformation information)
    {
        WriteInformationLine("CPU", information.CpuName);
        WriteInformationLine(
            "CPUID",
            $"{information.CpuId} ({information.CodeName})");
        WriteInformationLine("Model", information.Model);
        WriteInformationLine("Package", information.Package);
        WriteInformationLine(
            "Config",
            $"{information.CcdCount} CCD / {information.CcxCount} CCX / " +
            $"{information.PhysicalCoreCount} physical cores");
        WriteInformationLine(
            "Logical",
            $"{information.LogicalProcessorCount} logical processors");
        WriteInformationLine(
            "SMT",
            $"{(information.SmtEnabled ? "Enabled" : "Disabled")} " +
            $"({information.ThreadsPerCore} " +
            $"{(information.ThreadsPerCore == 1 ? "thread" : "threads")} per core)");
        WriteInformationLine("MB Vendor", information.MotherboardVendor);
        WriteInformationLine("MB Model", information.MotherboardModel);
        WriteInformationLine("BIOS", information.BiosVersion);
        WriteInformationLine("Firmware", information.FirmwareVersion);
        WriteInformationLine("SMU", information.SmuVersion);
        WriteInformationLine(
            "PM Version",
            $"0x{information.PmTableVersion:X8}");
        WriteInformationLine(
            "PM Size",
            $"0x{information.PmTableSize:X8} bytes");
        WriteInformationLine(
            "Vcore Map",
            information.VcoreTelemetrySource);
    }

    private void WriteInformationLine(string label, string value)
    {
        _output.WriteLine($"{label,-11}{value}");
    }

    private int ValidateRequest(
        CliRequest request,
        IRyzenController controller,
        CompactCoreMap? coreMap)
    {
        if (NeedsQualifiedPhysicalTopology(request) &&
            !controller.HasUsableCoreTopology)
        {
            _error.WriteLine(
                controller.CoreTopologyUnavailableReason ??
                "This CPU does not expose a qualified per-core topology.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if (request.GetVcore && request.StreamVcore)
        {
            _error.WriteLine(
                "A one-shot Vcore read and a Vcore stream cannot be requested together.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamTelemetry &&
            HasNonTelemetryOperation(request))
        {
            _error.WriteLine(
                "A telemetry stream cannot be combined with another hardware operation.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamSystemTelemetry &&
            HasNonSystemTelemetryOperation(request))
        {
            _error.WriteLine(
                "A system telemetry stream cannot be combined with another " +
                "hardware operation.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.DiagnoseVcore &&
            (!VcoreDiagnostics.IsValidSampleCount(
                 request.VcoreDiagnosticSampleCount) ||
             !VcoreStreaming.IsValidInterval(
                 request.VcoreDiagnosticIntervalMilliseconds)))
        {
            _error.WriteLine(
                "The Vcore diagnostic sample count or interval is out of range.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamVcore &&
            request.VcoreStreamIntervalMilliseconds is int interval &&
            !VcoreStreaming.IsValidInterval(interval))
        {
            _error.WriteLine(
                $"Vcore stream interval must be from " +
                $"{VcoreStreaming.MinimumIntervalMilliseconds} through " +
                $"{VcoreStreaming.MaximumIntervalMilliseconds} milliseconds.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamTelemetry &&
            request.TelemetryStreamIntervalMilliseconds is int telemetryInterval &&
            !CpuTelemetryStreaming.IsValidInterval(telemetryInterval))
        {
            _error.WriteLine(
                $"Telemetry stream interval must be from " +
                $"{CpuTelemetryStreaming.MinimumIntervalMilliseconds} through " +
                $"{CpuTelemetryStreaming.MaximumIntervalMilliseconds} milliseconds.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamSystemTelemetry &&
            request.SystemTelemetryStreamIntervalMilliseconds is
                int systemTelemetryInterval &&
            !SystemTelemetryStreaming.IsValidInterval(systemTelemetryInterval))
        {
            _error.WriteLine(
                $"System telemetry stream interval must be from " +
                $"{SystemTelemetryStreaming.MinimumIntervalMilliseconds} through " +
                $"{SystemTelemetryStreaming.MaximumIntervalMilliseconds} " +
                "milliseconds.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamSystemTelemetry &&
            request.SystemTelemetryTargets is null)
        {
            _error.WriteLine(
                "A system telemetry stream requires a complete target set.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.StreamVcore && HasNonVcoreOperation(request))
        {
            _error.WriteLine(
                "A Vcore stream cannot be combined with another hardware operation.");
            return (int)ExitCode.InvalidInput;
        }

        if (request.OffsetSpecification is not null)
        {
            if (!controller.CanWritePboOffsets)
            {
                _error.WriteLine(
                    "This CPU does not expose the SMU command required to set per-core offsets.");
                return (int)ExitCode.UnsupportedOperation;
            }

            foreach (OffsetAssignment assignment in
                     request.OffsetSpecification.Assignments)
            {
                if (!coreMap!.ContainsKey(assignment.EnabledCoreIndex))
                {
                    _error.WriteLine(
                        $"Enabled core {assignment.EnabledCoreIndex} is out of range. " +
                        $"This system has {coreMap.Count} enabled cores, indexed from 0.");
                    return (int)ExitCode.CoreOutOfRange;
                }
            }
        }

        if (request.DisabledCores is not null)
        {
            if (request.DisabledCores.PhysicalCoreIndices.Count >=
                controller.PhysicalCoreSlots)
            {
                _error.WriteLine(
                    "Refusing to disable every physical core slot.");
                return (int)ExitCode.InvalidInput;
            }

            foreach (int physicalCoreIndex in request.DisabledCores.PhysicalCoreIndices)
            {
                if (physicalCoreIndex >= controller.PhysicalCoreSlots)
                {
                    _error.WriteLine(
                        $"Physical core {physicalCoreIndex} is out of range. " +
                        $"This topology has {controller.PhysicalCoreSlots} physical core " +
                        "slots, indexed from 0.");
                    return (int)ExitCode.CoreOutOfRange;
                }
            }
        }

        if (request.GetPhysicalCores &&
            controller.FactoryCoreDisableMasks.Count < controller.CcdCount)
        {
            _error.WriteLine(
                "ZenStates-Core returned an incomplete factory core-disable map.");
            return (int)ExitCode.InitializationFailed;
        }

        if (request.PptWatts is not null && !controller.CanWritePptLimit)
        {
            _error.WriteLine(
                "This CPU does not expose the SMU command required to set PPT.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if (request.TdcAmps is not null && !controller.CanWriteTdcLimit)
        {
            _error.WriteLine(
                "This CPU does not expose the SMU command required to set TDC.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if (request.EdcAmps is not null && !controller.CanWriteEdcLimit)
        {
            _error.WriteLine(
                "This CPU does not expose the SMU command required to set EDC.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if (request.FMax is not null && !controller.CanWriteFMax)
        {
            _error.WriteLine(
                "This CPU does not expose the SMU command required to set FMax.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if (request.GetFMax && !controller.CanReadFMax)
        {
            _error.WriteLine(
                "This CPU does not expose the SMU command required to read FMax.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if ((request.GetVcore ||
             request.StreamVcore ||
             request.StreamTelemetry ||
             request.StreamSystemTelemetry) &&
            !controller.CanReadVcore)
        {
            _error.WriteLine(
                controller.VcoreReadUnavailableReason ??
                "This CPU does not expose a mapped Vcore telemetry source.");
            return (int)ExitCode.UnsupportedOperation;
        }

        if ((request.GetCpuTemperature ||
             request.StreamTelemetry ||
             request.StreamSystemTelemetry) &&
            !controller.CanReadCpuTemperature)
        {
            _error.WriteLine(
                controller.CpuTemperatureReadUnavailableReason ??
                "This CPU does not expose mapped Tctl/Tdie temperature telemetry.");
            return (int)ExitCode.UnsupportedOperation;
        }

        return (int)ExitCode.Success;
    }

    private static bool HasNonVcoreOperation(CliRequest request)
    {
        return request.OffsetSpecification is not null ||
               request.DisabledCores is not null ||
               request.EnableAllCores ||
               request.GetOffsetsTerse ||
               request.GetPhysicalCores ||
               request.GetEnabledCores ||
               request.PboScalar is not null ||
               request.GetPboScalar ||
               request.PptWatts is not null ||
               request.TdcAmps is not null ||
               request.EdcAmps is not null ||
               request.FMax is not null ||
               request.GetFMax ||
               request.GetCpuTemperature ||
               request.StreamTelemetry ||
               request.StreamSystemTelemetry ||
               request.DiagnoseVcore ||
               request.ShowInfo;
    }

    private static bool HasNonTelemetryOperation(CliRequest request)
    {
        return request.OffsetSpecification is not null ||
               request.DisabledCores is not null ||
               request.EnableAllCores ||
               request.GetOffsetsTerse ||
               request.GetPhysicalCores ||
               request.GetEnabledCores ||
               request.PboScalar is not null ||
               request.GetPboScalar ||
               request.PptWatts is not null ||
               request.TdcAmps is not null ||
               request.EdcAmps is not null ||
               request.FMax is not null ||
               request.GetFMax ||
               request.GetVcore ||
               request.StreamVcore ||
               request.GetCpuTemperature ||
               request.DiagnoseVcore ||
               request.StreamSystemTelemetry ||
               request.ShowInfo;
    }

    private static bool HasNonSystemTelemetryOperation(CliRequest request)
    {
        return request.OffsetSpecification is not null ||
               request.DisabledCores is not null ||
               request.EnableAllCores ||
               request.GetOffsetsTerse ||
               request.GetPhysicalCores ||
               request.GetEnabledCores ||
               request.PboScalar is not null ||
               request.GetPboScalar ||
               request.PptWatts is not null ||
               request.TdcAmps is not null ||
               request.EdcAmps is not null ||
               request.FMax is not null ||
               request.GetFMax ||
               request.GetVcore ||
               request.StreamVcore ||
               request.GetCpuTemperature ||
               request.StreamTelemetry ||
               request.DiagnoseVcore ||
               request.ShowInfo;
    }

    private static bool NeedsCoreMap(CliRequest request)
    {
        return request.OffsetSpecification is not null ||
               request.GetOffsetsTerse ||
               request.GetEnabledCores;
    }

    private static bool NeedsQualifiedPhysicalTopology(CliRequest request)
    {
        return request.DisabledCores is not null ||
               request.EnableAllCores ||
               request.GetPhysicalCores;
    }
}
