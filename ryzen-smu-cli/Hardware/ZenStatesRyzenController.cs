using System.Diagnostics;
using OpenHardwareMonitor.Hardware;
using ZenStates.Core;

namespace ryzen_smu_cli;

internal sealed class ZenStatesRyzenController : IRyzenController
{
    private const uint MsrPowerUnit = 0xC0010299;
    private const uint MsrPackageEnergyStatus = 0xC001029B;
    private const uint MsrMperfReadOnly = 0xC00000E7;
    private const uint MsrAperfReadOnly = 0xC00000E8;

    private readonly Cpu _cpu;
    private readonly int _ccdCount;
    private readonly int _physicalCoreSlots;
    private readonly int _enabledCoreCount;
    private readonly IReadOnlyList<CoreAddress> _pboOffsetCandidates;
    private readonly IReadOnlyList<CoreAddress> _pboOffsetFallbackCandidates;
    private bool _disposed;

    public ZenStatesRyzenController()
    {
        if (!PawnIo.IsInstalled)
        {
            throw new PawnIoNotInstalledException();
        }

        _cpu = new Cpu();
        try
        {
            _ccdCount = checked((int)_cpu.info.topology.ccds);
            _physicalCoreSlots = checked((int)_cpu.info.topology.physicalCores);
            FactoryCoreDisableMasks = (_cpu.info.topology.coreDisableMap ?? [])
                .Select(mask => (byte)(mask & 0xff))
                .ToArray();
            _enabledCoreCount = ResolveEnabledCoreCount();
            _pboOffsetCandidates = ResolvePboOffsetCandidates();
            _pboOffsetFallbackCandidates = ResolvePboOffsetFallbackCandidates();
            CoreTopologyUnavailableReason = ValidateCoreTopology();
            Information = CreateCpuInformation();
        }
        catch
        {
            _cpu.Dispose();
            throw;
        }
    }

    public CpuInformation Information { get; }

    public uint CpuFamily => (uint)_cpu.info.family;

    public uint CpuModel => _cpu.info.model;

    public uint CpuPackage => (uint)_cpu.info.packageType;

    public int CcdCount => _ccdCount;

    public int PhysicalCoreSlots => _physicalCoreSlots;

    public int EnabledCoreCount => _enabledCoreCount;

    public IReadOnlyList<byte> FactoryCoreDisableMasks { get; }

    public IReadOnlyList<CoreAddress> PboOffsetCandidates =>
        _pboOffsetCandidates;

    public IReadOnlyList<CoreAddress> PboOffsetFallbackCandidates =>
        _pboOffsetFallbackCandidates;

    public bool HasUsableCoreTopology => CoreTopologyUnavailableReason is null;

    public string? CoreTopologyUnavailableReason { get; }

    public bool CanReadPboOffsets =>
        _cpu.smu.Rsmu.SMU_MSG_GetDldoPsmMargin > 0;

    public bool CanWritePboOffsets =>
        _cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin > 0 ||
        _cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin > 0;

    public bool CanReadFMax =>
        _cpu.smu.Rsmu.SMU_MSG_GetBoostLimitFrequency > 0;

    public bool CanWriteFMax =>
        _cpu.smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores > 0 ||
        _cpu.smu.Mp1Smu.SMU_MSG_SetBoostLimitFrequencyAllCores > 0;

    public bool CanWritePptLimit =>
        _cpu.smu.Rsmu.SMU_MSG_SetFastLimit > 0 ||
        _cpu.smu.Mp1Smu.SMU_MSG_SetFastLimit > 0;

    public bool CanWriteTdcLimit =>
        _cpu.smu.Rsmu.SMU_MSG_SetTDCVDDLimit > 0 ||
        _cpu.smu.Mp1Smu.SMU_MSG_SetTDCVDDLimit > 0;

    public bool CanWriteEdcLimit =>
        _cpu.smu.Rsmu.SMU_MSG_SetEDCVDDLimit > 0 ||
        _cpu.smu.Mp1Smu.SMU_MSG_SetEDCVDDLimit > 0;

    private bool TryGetPowerLimitLayout(
        out PowerLimitTelemetryLayout? layout) =>
        PowerLimitTelemetryLayout.TryResolve(
            (uint)_cpu.info.family,
            _cpu.info.model,
            (uint)_cpu.info.packageType,
            _cpu.RyzenSmu.PmTableVersion,
            PmTableStructureSize,
            out layout);

    public bool CanReadPowerLimits =>
        _cpu.RyzenSmu.IsSupported &&
        _cpu.RyzenSmu.DramBaseAddress != 0 &&
        TryGetPowerLimitLayout(out _) &&
        (_cpu.smu.Rsmu.SMU_MSG_GetSustainedPowerAndThmLimit > 0 ||
         _cpu.smu.Mp1Smu.SMU_MSG_GetSustainedPowerAndThmLimit > 0);

    public string? PowerLimitReadUnavailableReason
    {
        get
        {
            if (!_cpu.RyzenSmu.IsSupported)
            {
                return "ZenStates-Core did not initialize PM-table telemetry.";
            }

            if (_cpu.RyzenSmu.DramBaseAddress == 0)
            {
                return "ZenStates-Core did not resolve a PM-table address.";
            }

            if (!TryGetPowerLimitLayout(out _))
            {
                return $"Configured power-limit readback is not mapped for " +
                       $"CPU 0x{(uint)_cpu.info.family:X2}/0x{_cpu.info.model:X2}, " +
                       $"package {(uint)_cpu.info.packageType}, PM table " +
                       $"0x{_cpu.RyzenSmu.PmTableVersion:X8} / " +
                       $"0x{PmTableStructureSize:X} bytes.";
            }

            if (_cpu.smu.Rsmu.SMU_MSG_GetSustainedPowerAndThmLimit == 0 &&
                _cpu.smu.Mp1Smu.SMU_MSG_GetSustainedPowerAndThmLimit == 0)
            {
                return "The CPU does not expose the configured PPT query " +
                       "required to cross-check PM-table power limits.";
            }

            return null;
        }
    }

    private bool TryGetSviTfnVcoreLayout(
        out SviTfnVcoreTelemetry? layout) =>
        SviTfnVcoreTelemetry.TryResolve(
            (uint)_cpu.info.family,
            _cpu.info.model,
            out layout);

    public bool CanReadVcore =>
        TryGetSviTfnVcoreLayout(out _)
            ? true
            : _cpu.RyzenSmu.IsSupported &&
              _cpu.RyzenSmu.DramBaseAddress != 0 &&
              VcoreTelemetryLayout.TryResolve(
                  (uint)_cpu.info.family,
                  _cpu.info.model,
                  (uint)_cpu.info.packageType,
                  _cpu.RyzenSmu.PmTableVersion,
                  PmTableStructureSize,
                  out _);

    public string? VcoreReadUnavailableReason
    {
        get
        {
            uint version = _cpu.RyzenSmu.PmTableVersion;
            uint size = PmTableStructureSize;
            if (TryGetSviTfnVcoreLayout(out _))
            {
                // The embedded AMDFamily17 module exposes the required
                // read-only SMN path. A module or register failure is reported
                // by the actual read instead of misclassifying a known layout.
                return null;
            }

            if (!_cpu.RyzenSmu.IsSupported)
            {
                return $"ZenStates-Core did not initialize PM-table telemetry " +
                       $"(version 0x{version:X8}, size 0x{size:X8} bytes).";
            }

            if (_cpu.RyzenSmu.DramBaseAddress == 0)
            {
                return $"ZenStates-Core did not resolve a PM-table address " +
                       $"(version 0x{version:X8}, size 0x{size:X8} bytes).";
            }

            return VcoreTelemetryLayout.TryResolve(
                (uint)_cpu.info.family,
                _cpu.info.model,
                (uint)_cpu.info.packageType,
                version,
                size,
                out _)
                ? null
                : $"Vcore telemetry is not mapped for PM table " +
                  $"0x{version:X8} with structure size 0x{size:X8} bytes; " +
                  $"refusing to guess a PM-table index.";
        }
    }

    private bool TryGetCpuTemperatureTelemetry(
        out CpuTemperatureTelemetry? telemetry) =>
        CpuTemperatureTelemetry.TryResolve(
            (uint)_cpu.info.family,
            out telemetry);

    public bool CanReadCpuTemperature =>
        TryGetCpuTemperatureTelemetry(out _);

    public string? CpuTemperatureReadUnavailableReason =>
        TryGetCpuTemperatureTelemetry(out _)
            ? null
            : $"CPU temperature telemetry is not mapped for family " +
              $"0x{(uint)_cpu.info.family:X2}.";

    private CpuInformation CreateCpuInformation()
    {
        SystemInfo? systemInfo = _cpu.systemInfo;
        int logicalProcessorCount =
            checked((int)_cpu.info.topology.logicalCores);
        int threadsPerCore =
            checked((int)_cpu.info.topology.threadsPerCore);
        return new CpuInformation(
            string.IsNullOrWhiteSpace(_cpu.info.cpuName)
                ? "N/A"
                : _cpu.info.cpuName.Trim(),
            _cpu.info.cpuid == 0
                ? "N/A"
                : _cpu.info.cpuid.ToString("X"),
            FormatCodeName(),
            _cpu.info.model.ToString("X"),
            _cpu.info.packageType.ToString(),
            CcdCount,
            checked((int)_cpu.info.topology.ccxs),
            ResolveInformationPhysicalCoreCount(
                EnabledCoreCount,
                _cpu.info.topology.cores,
                HasUsableCoreTopology),
            logicalProcessorCount,
            threadsPerCore,
            threadsPerCore > 1,
            NormalizeInformationValue(systemInfo?.MbVendor),
            NormalizeInformationValue(systemInfo?.MbName),
            NormalizeInformationValue(systemInfo?.BiosVersion),
            _cpu.info.patchLevel == 0
                ? "N/A"
                : _cpu.info.patchLevel.ToString("X8"),
            systemInfo?.SmuVersionString ?? FormatSmuVersion(_cpu.smu.Version),
            _cpu.RyzenSmu.PmTableVersion,
            PmTableStructureSize,
            FormatVcoreTelemetrySource());
    }

    internal static int ResolveInformationPhysicalCoreCount(
        int enabledCoreCount,
        uint cpuidPhysicalCoreCount,
        bool coreTopologyQualified)
    {
        // On platforms whose per-core fuse map/selectors are deliberately
        // fail-closed, the fused-slot result is diagnostic evidence rather
        // than a trustworthy user-facing core count. ZenStates-Core derives
        // topology.cores from CPUID logical processors / threads per core, so
        // prefer that hardware-reported count without consulting Windows.
        if (!coreTopologyQualified &&
            cpuidPhysicalCoreCount is > 0 and <= 256)
        {
            return checked((int)cpuidPhysicalCoreCount);
        }

        return enabledCoreCount;
    }

    private uint PmTableStructureSize =>
        // RyzenSmu.PmTableSize is the padded read-buffer length for several
        // exact Zen 4/5 layouts. PowerTable.TableSize is the matching
        // structure definition and is the useful value for diagnostics.
        _cpu.powerTable is { TableSize: > 0 } powerTable
            ? checked((uint)powerTable.TableSize)
            : _cpu.RyzenSmu.PmTableSize;

    private string FormatCodeName()
    {
        // Fire Range reuses Family 1Ah model 44h from Granite Ridge but uses
        // the mobile package identifier, just as Dragon Range does for
        // Family 19h model 61h. ZenStates-Core currently labels both model
        // 44h packages GraniteRidge, so correct the diagnostic name here.
        return (uint)_cpu.info.family == 0x1A &&
               _cpu.info.model == 0x44 &&
               (uint)_cpu.info.packageType == 1
            ? "FireRange"
            : _cpu.info.codeName.ToString();
    }

    private string FormatVcoreTelemetrySource()
    {
        if (TryGetSviTfnVcoreLayout(
                out SviTfnVcoreTelemetry? sviLayout))
        {
            return $"SMU SVI CPU Core Rail " +
                   $"(SMN 0x{sviLayout!.CorePlaneRegister:X8}, " +
                   $"VID bits {sviLayout.VidShift + 7}:{sviLayout.VidShift}, " +
                   $"{FormatMappingConfidence(sviLayout.Confidence)})";
        }

        return VcoreTelemetryLayout.TryResolve(
            (uint)_cpu.info.family,
            _cpu.info.model,
            (uint)_cpu.info.packageType,
            _cpu.RyzenSmu.PmTableVersion,
            PmTableStructureSize,
            out VcoreTelemetryLayout? layout)
            ? $"{layout!.SourceName} (entry {layout.ValueIndex}, " +
              $"{FormatMappingConfidence(layout.Confidence)})"
            : "Unmapped";
    }

    private static string FormatMappingConfidence(
        VcoreMappingConfidence confidence) => confidence switch
        {
            VcoreMappingConfidence.Verified => "verified",
            VcoreMappingConfidence.Structural => "structural mapping",
            _ => "unknown confidence",
        };

    private static string NormalizeInformationValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

    private static string FormatSmuVersion(uint version)
    {
        if (version == 0)
        {
            return "N/A";
        }

        return (version & 0xff000000) != 0
            ? $"{(version >> 24) & 0xff}.{(version >> 16) & 0xff}." +
              $"{(version >> 8) & 0xff}.{version & 0xff}"
            : $"{(version >> 16) & 0xff}.{(version >> 8) & 0xff}.{version & 0xff}";
    }

    private int ResolveEnabledCoreCount()
    {
        if (CcdCount > 0 &&
            PhysicalCoreSlots == checked(CcdCount * 8) &&
            FactoryCoreDisableMasks.Count >= CcdCount)
        {
            int enabled = 0;
            for (int ccd = 0; ccd < CcdCount; ccd++)
            {
                enabled += 8 - System.Numerics.BitOperations.PopCount(
                    FactoryCoreDisableMasks[ccd]);
            }

            if (enabled is > 0 and <= 128)
            {
                return enabled;
            }
        }

        return checked((int)_cpu.info.topology.cores);
    }

    private IReadOnlyList<CoreAddress> ResolvePboOffsetCandidates()
    {
        int cpuidCoreCount = checked((int)_cpu.info.topology.cores);
        int[] orderedCcdSelectors = ResolvePboCcdSelectors(
            _cpu.info.topology.ccdEnableMap,
            cpuidCoreCount,
            CcdCount,
            PhysicalCoreSlots,
            CpuFamily,
            CpuModel).ToArray();
        return CreatePboOffsetCandidates(orderedCcdSelectors);
    }

    private IReadOnlyList<CoreAddress> ResolvePboOffsetFallbackCandidates()
    {
        IReadOnlyList<int> primaryCcdSelectors = ResolvePboCcdSelectors(
            _cpu.info.topology.ccdEnableMap,
            checked((int)_cpu.info.topology.cores),
            CcdCount,
            PhysicalCoreSlots,
            CpuFamily,
            CpuModel);
        IReadOnlyList<int> fallbackCcdSelectors =
            ResolvePboFallbackCcdSelectors(
                _cpu.info.topology.ccdEnableMap,
                primaryCcdSelectors,
                CpuFamily,
                CpuModel);
        return CreatePboOffsetCandidates(fallbackCcdSelectors);
    }

    private static IReadOnlyList<CoreAddress> CreatePboOffsetCandidates(
        IReadOnlyList<int> ccdSelectors)
    {
        List<CoreAddress> candidates = [];
        foreach (int ccdSelector in ccdSelectors)
        {
            for (int core = 0; core < 8; core++)
            {
                candidates.Add(
                    CoreAddress.FromPhysicalCoreIndex(
                        checked((ccdSelector * 8) + core)));
            }
        }

        return candidates;
    }

    internal static IReadOnlyList<int> ResolvePboCcdSelectors(
        uint enabledCcdMap,
        int cpuidCoreCount,
        int reportedCcdCount,
        int physicalCoreSlots,
        uint family,
        uint model)
    {
        SortedSet<int> ccdSelectors = [];
        int ccdsRequiredByCpuid = Math.Clamp(
            (Math.Max(cpuidCoreCount, 1) + 7) / 8,
            1,
            16);

        for (int ccd = 0; ccd < 16; ccd++)
        {
            if ((enabledCcdMap & (1u << ccd)) != 0)
            {
                ccdSelectors.Add(ccd);
            }
        }

        // An enabled-CCD bitmap is useful when an entire CCD is disabled: it
        // preserves the real SMU selector even though the reported active CCD
        // count is compacted. Treat it as a hint, not a qualification gate.
        // If CPUID exposes more active cores than the bitmap can represent,
        // add the missing contiguous selectors and let read-only offset probes
        // determine which ones are actually operable.
        if (ccdSelectors.Count > 0)
        {
            if (ccdSelectors.Count < ccdsRequiredByCpuid)
            {
                for (int ccd = 0; ccd < ccdsRequiredByCpuid; ccd++)
                {
                    ccdSelectors.Add(ccd);
                }
            }

            return ccdSelectors.ToArray();
        }

        int inferredCcdCount = Math.Max(
            reportedCcdCount,
            Math.Max(
                (Math.Max(physicalCoreSlots, 1) + 7) / 8,
                ccdsRequiredByCpuid));

        // Raphael/Dragon Range and Granite Ridge/Fire Range silicon can
        // contain two CCD selectors. When fuse discovery is unavailable,
        // probing both is the only way to find a surviving CCD1 after an
        // entire CCD0 has been disabled. Invalid selectors must still answer
        // the read command successfully before CoreMapper admits them.
        if (IsDualCcdCapableDesktopDie(family, model))
        {
            inferredCcdCount = Math.Max(inferredCcdCount, 2);
        }

        for (int ccd = 0; ccd < Math.Clamp(inferredCcdCount, 1, 16); ccd++)
        {
            ccdSelectors.Add(ccd);
        }

        return ccdSelectors.ToArray();
    }

    internal static IReadOnlyList<int> ResolvePboFallbackCcdSelectors(
        uint enabledCcdMap,
        IReadOnlyList<int> primaryCcdSelectors,
        uint family,
        uint model)
    {
        if (enabledCcdMap == 0 ||
            !IsDualCcdCapableDesktopDie(family, model) ||
            primaryCcdSelectors.Count != 1 ||
            primaryCcdSelectors[0] is < 0 or > 1)
        {
            return [];
        }

        // A nonzero bitmap remains the primary fast path, but firmware can
        // occasionally expose stale fuse metadata. CoreMapper probes this
        // opposite selector only when every primary selector fails, so a
        // healthy single-CCD system pays no additional hardware reads.
        return [1 - primaryCcdSelectors[0]];
    }

    private static bool IsDualCcdCapableDesktopDie(uint family, uint model) =>
        (family == 0x19 && model == 0x61) ||
        (family == 0x1A && model == 0x44);

    private string? ValidateCoreTopology()
    {
        if (CcdCount is < 1 or > 16)
        {
            return $"Unsupported CCD count reported by ZenStates-Core: " +
                   $"{CcdCount}.";
        }

        if (PhysicalCoreSlots != checked(CcdCount * 8))
        {
            return "ZenStates-Core returned a topology that cannot be represented " +
                   "by the current per-core SMU selector.";
        }

        if (EnabledCoreCount is < 1 || EnabledCoreCount > PhysicalCoreSlots)
        {
            return "ZenStates-Core returned an invalid enabled-core count.";
        }

        if (FactoryCoreDisableMasks.Count < CcdCount)
        {
            return "ZenStates-Core did not return a complete physical-core fuse map.";
        }

        string? platformReason = GetUnqualifiedCoreTopologyReason(
            (uint)_cpu.info.family,
            _cpu.info.model,
            (uint)_cpu.info.packageType);
        if (platformReason is not null)
        {
            return platformReason;
        }

        return null;
    }

    internal static string? GetUnqualifiedCoreTopologyReason(
        uint family,
        uint model,
        uint package)
    {
        if (family == 0x19 && model is 0x74 or 0x75 or 0x78 or 0x7C)
        {
            return "Per-core fuse mapping and SMU selectors for this " +
                   "Phoenix-family topology have not been hardware-qualified. " +
                   "Package-level information and Vcore telemetry remain available.";
        }

        if (family == 0x1A &&
            model is 0x20 or 0x24 or 0x60 or 0x68 or 0x70)
        {
            return "Per-core SMU selectors for this heterogeneous Family 1Ah " +
                   "mobile topology have not been hardware-qualified. Package-level " +
                   "information and Vcore telemetry remain available.";
        }

        // Fire Range is Family 1Ah model 44h in a mobile package, but it
        // retains the desktop-die per-core selector used by Granite Ridge.
        // ValidateCoreTopology has already required its complete eight-slot-
        // per-CCD fuse topology before this platform gate is evaluated.
        return null;
    }

    public OperationResult<int> GetPboOffset(CoreAddress core)
    {
        uint? value = UsesCompactApuReadSelector()
            ? _cpu.GetPsmMarginSingleCore((uint)core.PhysicalCoreIndex)
            : _cpu.GetPsmMarginSingleCore(
                (uint)core.CoreIndex,
                (uint)core.CcdIndex,
                (uint)core.CcxIndex);
        return value.HasValue
            ? OperationResult<int>.Ok(unchecked((int)value.Value))
            : OperationResult<int>.Fail(
                $"The SMU did not return an offset for physical core {core.PhysicalCoreIndex}.");
    }

    private bool UsesCompactApuReadSelector() =>
        _cpu.smu.SMU_TYPE is >= SMU.SmuType.TYPE_APU0 and <= SMU.SmuType.TYPE_APU2;

    public OperationResult SetPboOffset(CoreAddress core, int offset)
    {
        bool success = _cpu.SetPsmMarginSingleCore(
            (uint)core.CoreIndex,
            (uint)core.CcdIndex,
            (uint)core.CcxIndex,
            offset);
        return success
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected offset {offset} for physical core " +
                $"{core.PhysicalCoreIndex}.");
    }

    public OperationResult<float> GetPboScalar()
    {
        if (_cpu.smu.Rsmu.SMU_MSG_GetPBOScalar == 0)
        {
            return OperationResult<float>.Fail(
                "This CPU does not expose the SMU command required to read the PBO scalar.");
        }

        uint[] arguments = new uint[_cpu.smu.Rsmu.MAX_ARGS];
        SMU.Status status = _cpu.smu.SendRsmuCommand(
            _cpu.smu.Rsmu.SMU_MSG_GetPBOScalar,
            ref arguments);
        if (status != SMU.Status.OK)
        {
            return OperationResult<float>.Fail(
                $"The SMU failed to read the PBO scalar ({status}).");
        }

        float scalar = BitConverter.UInt32BitsToSingle(arguments[0]);
        return scalar is >= 0 and <= 10
            ? OperationResult<float>.Ok(scalar)
            : OperationResult<float>.Fail(
                $"The SMU returned an invalid PBO scalar value ({scalar}).");
    }

    public OperationResult SetPboScalar(int scalar)
    {
        if (_cpu.smu.Rsmu.SMU_MSG_SetPBOScalar == 0)
        {
            return OperationResult.Fail(
                "This CPU does not expose the SMU command required to set the PBO scalar.");
        }

        uint[] arguments = new uint[_cpu.smu.Rsmu.MAX_ARGS];
        arguments[0] = checked((uint)scalar * 100);
        SMU.Status status = _cpu.smu.SendRsmuCommand(
            _cpu.smu.Rsmu.SMU_MSG_SetPBOScalar,
            ref arguments);
        return status == SMU.Status.OK
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected PBO scalar {scalar} ({status}).");
    }

    public OperationResult<uint> GetFMax()
    {
        uint megahertz = _cpu.GetFMax();
        return megahertz > 0
            ? OperationResult<uint>.Ok(megahertz)
            : OperationResult<uint>.Fail(
                "The SMU did not return a valid maximum boost-frequency limit.");
    }

    public OperationResult SetFMax(uint megahertz)
    {
        return _cpu.SetFMax(megahertz)
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected the {megahertz} MHz maximum boost-frequency limit.");
    }

    public OperationResult SetPptLimit(uint watts)
    {
        SMU.Status status = _cpu.SetPPTLimit(watts);
        return status == SMU.Status.OK
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected PPT limit {watts} W ({status}).");
    }

    public OperationResult SetTdcLimit(uint amps)
    {
        SMU.Status status = _cpu.SetTDCVDDLimit(amps);
        return status == SMU.Status.OK
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected TDC limit {amps} A ({status}).");
    }

    public OperationResult SetEdcLimit(uint amps)
    {
        SMU.Status status = _cpu.SetEDCVDDLimit(amps);
        return status == SMU.Status.OK
            ? OperationResult.Ok()
            : OperationResult.Fail(
                $"The SMU rejected EDC limit {amps} A ({status}).");
    }

    public OperationResult<PowerLimitSnapshot> GetPowerLimits()
    {
        if (!TryGetPowerLimitLayout(
                out PowerLimitTelemetryLayout? layout))
        {
            return OperationResult<PowerLimitSnapshot>.Fail(
                PowerLimitReadUnavailableReason ??
                "Configured power-limit readback is unavailable.");
        }

        try
        {
            OperationResult<PowerLimitSnapshot> tableResult =
                layout!.Read(_cpu.RyzenSmu.GetPmTable());
            if (!tableResult.Success)
            {
                return tableResult;
            }

            Cpu.SystemPowerLimit? configured =
                _cpu.GetSystemPowerLimit();
            if (!configured.HasValue)
            {
                return OperationResult<PowerLimitSnapshot>.Fail(
                    "The SMU configured-PPT query failed; refusing to " +
                    "trust PM-table limit fields without the cross-check.");
            }

            PowerLimitSnapshot limits = tableResult.Value!;
            if (configured.Value.PowerLimit != limits.PptWatts)
            {
                return OperationResult<PowerLimitSnapshot>.Fail(
                    $"Configured PPT disagreement: SMU query returned " +
                    $"{configured.Value.PowerLimit} W but PM table " +
                    $"0x{layout.PmTableVersion:X8} returned " +
                    $"{limits.PptWatts} W.");
            }

            return OperationResult<PowerLimitSnapshot>.Ok(limits);
        }
        catch (Exception ex)
        {
            return OperationResult<PowerLimitSnapshot>.Fail(
                $"Failed to read configured power limits: {ex.Message}");
        }
    }

    public OperationResult<double> GetVcore()
    {
        if (TryGetSviTfnVcoreLayout(
                out SviTfnVcoreTelemetry? sviLayout))
        {
            return ReadSviTfnVcore(sviLayout!);
        }

        uint version = _cpu.RyzenSmu.PmTableVersion;
        if (!VcoreTelemetryLayout.TryResolve(
                (uint)_cpu.info.family,
                _cpu.info.model,
                (uint)_cpu.info.packageType,
                version,
                PmTableStructureSize,
                out VcoreTelemetryLayout? layout))
        {
            return OperationResult<double>.Fail(
                VcoreReadUnavailableReason ??
                "Vcore telemetry is unavailable on this CPU.");
        }

        VcoreTelemetryLayout selectedLayout = layout!;
        try
        {
            return selectedLayout.Read(_cpu.RyzenSmu.GetPmTable());
        }
        catch (Exception ex)
        {
            return OperationResult<double>.Fail(
                $"Failed to read {selectedLayout.SourceName} telemetry at entry " +
                $"{selectedLayout.ValueIndex} from PM table " +
                $"0x{version:X8}: {ex.Message}");
        }
    }

    public OperationResult<double> GetCpuTemperature()
    {
        if (!TryGetCpuTemperatureTelemetry(
                out CpuTemperatureTelemetry? telemetry))
        {
            return OperationResult<double>.Fail(
                CpuTemperatureReadUnavailableReason ??
                "CPU temperature telemetry is unavailable on this CPU.");
        }

        CpuTemperatureTelemetry selectedTelemetry = telemetry!;
        uint rawValue = 0;
        try
        {
            if (!_cpu.ReadDwordEx(selectedTelemetry.SmnRegister, ref rawValue))
            {
                return OperationResult<double>.Fail(
                    $"Failed to read SMU {selectedTelemetry.SourceName} temperature " +
                    $"register 0x{selectedTelemetry.SmnRegister:X8}.");
            }

            return selectedTelemetry.Decode(rawValue, _cpu.info.cpuName);
        }
        catch (Exception ex)
        {
            return OperationResult<double>.Fail(
                $"Failed to read SMU {selectedTelemetry.SourceName} temperature " +
                $"telemetry: {ex.Message}");
        }
    }

    public OperationResult<SystemTelemetryRawSnapshot>
        ReadSystemTelemetrySnapshot(
            IReadOnlyList<SystemTelemetryTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!SystemTelemetryTargetSet.IsCanonical(targets))
        {
            return OperationResult<SystemTelemetryRawSnapshot>.Fail(
                "System telemetry targets must be unique and in canonical order.");
        }

        long acquisitionStartedTimestamp = Stopwatch.GetTimestamp();
        OperationResult<double> vcore = GetVcore();
        if (!vcore.Success)
        {
            return OperationResult<SystemTelemetryRawSnapshot>.Fail(
                vcore.Error ?? "Vcore telemetry refresh failed.");
        }

        OperationResult<double> temperature = GetCpuTemperature();
        if (!temperature.Success)
        {
            return OperationResult<SystemTelemetryRawSnapshot>.Fail(
                temperature.Error ?? "CPU temperature telemetry refresh failed.");
        }

        int packageEnergyUnitExponent = 0;
        uint packageEnergy = 0;
        long packageEnergyTimestamp = 0;
        List<SystemTelemetryTargetRawReading> readings =
            new(targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            OperationResult<SystemTelemetryAffinityReading> reading =
                ReadSystemTelemetryMsrs(
                    targets[index],
                    includePackageEnergy: index == 0);
            if (!reading.Success)
            {
                return OperationResult<SystemTelemetryRawSnapshot>.Fail(
                    reading.Error ??
                    $"Failed to read target {targets[index]} telemetry.");
            }

            SystemTelemetryAffinityReading value = reading.Value!;
            if (index == 0)
            {
                packageEnergyUnitExponent =
                    value.PackageEnergyUnitExponent!.Value;
                packageEnergy = value.PackageEnergy!.Value;
                packageEnergyTimestamp =
                    value.PackageEnergyTimestamp!.Value;
            }

            readings.Add(new SystemTelemetryTargetRawReading(
                targets[index],
                value.Aperf,
                value.Mperf,
                value.TargetTimestamp));
        }

        return OperationResult<SystemTelemetryRawSnapshot>.Ok(
            new SystemTelemetryRawSnapshot(
                acquisitionStartedTimestamp,
                Stopwatch.GetTimestamp(),
                vcore.Value,
                temperature.Value,
                packageEnergyUnitExponent,
                packageEnergy,
                packageEnergyTimestamp,
                readings));
    }

    private OperationResult<SystemTelemetryAffinityReading>
        ReadSystemTelemetryMsrs(
            SystemTelemetryTarget target,
            bool includePackageEnergy)
    {
        GroupAffinity desired = GroupAffinity.Single(
            target.Group,
            target.Processor);
        GroupAffinity previous;
        try
        {
            previous = ThreadAffinity.Set(desired);
        }
        catch (Exception ex)
        {
            return OperationResult<SystemTelemetryAffinityReading>.Fail(
                $"Failed to select Windows processor target {target}: " +
                ex.Message);
        }

        if (previous == GroupAffinity.Undefined)
        {
            return OperationResult<SystemTelemetryAffinityReading>.Fail(
                $"Windows rejected processor-group affinity target {target}.");
        }

        SystemTelemetryAffinityReading? reading = null;
        string? readError = null;
        try
        {
            int? energyUnitExponent = null;
            uint? packageEnergy = null;
            long? packageTimestamp = null;
            if (includePackageEnergy)
            {
                if (!TryReadMsr(MsrPowerUnit, out ulong powerUnit))
                {
                    readError =
                        "PawnIO failed to read AMD RAPL power-unit MSR " +
                        $"0x{MsrPowerUnit:X8} on target {target}.";
                }
                else if (!TryReadMsr(
                             MsrPackageEnergyStatus,
                             out ulong packageEnergyValue))
                {
                    readError =
                        "PawnIO failed to read AMD RAPL package-energy MSR " +
                        $"0x{MsrPackageEnergyStatus:X8} on target {target}.";
                }
                else
                {
                    energyUnitExponent =
                        checked((int)((powerUnit >> 8) & 0x1F));
                    packageEnergy = unchecked((uint)packageEnergyValue);
                    packageTimestamp = Stopwatch.GetTimestamp();
                }
            }

            ulong aperf = 0;
            ulong mperf = 0;
            if (readError is null &&
                !TryReadMsr(MsrAperfReadOnly, out aperf))
            {
                readError =
                    $"PawnIO failed to read APERF MSR 0x{MsrAperfReadOnly:X8} " +
                    $"on target {target}.";
            }

            if (readError is null &&
                !TryReadMsr(MsrMperfReadOnly, out mperf))
            {
                readError =
                    $"PawnIO failed to read MPERF MSR 0x{MsrMperfReadOnly:X8} " +
                    $"on target {target}.";
            }

            if (readError is null)
            {
                reading = new SystemTelemetryAffinityReading(
                    aperf,
                    mperf,
                    Stopwatch.GetTimestamp(),
                    energyUnitExponent,
                    packageEnergy,
                    packageTimestamp);
            }
        }
        catch (Exception ex)
        {
            readError =
                $"Failed to read system telemetry on target {target}: " +
                ex.Message;
        }
        finally
        {
            try
            {
                if (ThreadAffinity.Set(previous) == GroupAffinity.Undefined)
                {
                    readError =
                        $"Failed to restore thread affinity after reading " +
                        $"target {target}.";
                }
            }
            catch (Exception ex)
            {
                readError =
                    $"Failed to restore thread affinity after reading target " +
                    $"{target}: {ex.Message}";
            }
        }

        return readError is null && reading is not null
            ? OperationResult<SystemTelemetryAffinityReading>.Ok(reading)
            : OperationResult<SystemTelemetryAffinityReading>.Fail(
                readError ?? $"System telemetry target {target} returned no data.");
    }

    private bool TryReadMsr(uint index, out ulong value)
    {
        uint eax = 0;
        uint edx = 0;
        bool success = _cpu.ReadMsr(index, ref eax, ref edx);
        value = ((ulong)edx << 32) | eax;
        return success;
    }

    private sealed record SystemTelemetryAffinityReading(
        ulong Aperf,
        ulong Mperf,
        long TargetTimestamp,
        int? PackageEnergyUnitExponent,
        uint? PackageEnergy,
        long? PackageEnergyTimestamp);

    private OperationResult<double> ReadSviTfnVcore(
        SviTfnVcoreTelemetry layout)
    {
        try
        {
            if (layout.StatusRegister is uint statusRegister)
            {
                if (!TryReadSmn(statusRegister, out uint status))
                {
                    return OperationResult<double>.Fail(
                        $"Failed to read SMU SVI status register " +
                        $"0x{statusRegister:X8}.");
                }

                OperationResult statusResult = layout.ValidateStatus(status);
                if (!statusResult.Success)
                {
                    return OperationResult<double>.Fail(statusResult.Error!);
                }
            }

            if (!TryReadSmn(
                    layout.CorePlaneRegister,
                    out uint corePlaneTelemetry))
            {
                return OperationResult<double>.Fail(
                    $"Failed to read SMU SVI CPU-core rail register " +
                    $"0x{layout.CorePlaneRegister:X8}.");
            }

            return layout.Decode(corePlaneTelemetry);
        }
        catch (Exception ex)
        {
            return OperationResult<double>.Fail(
                $"Failed to read SMU SVI CPU-core rail telemetry: " +
                ex.Message);
        }
    }

    public IReadOnlyDictionary<uint, OperationResult<uint>>
        ReadVcoreDiagnosticRegisters()
    {
        IReadOnlyList<VcoreDiagnosticRegisterDescriptor> descriptors =
            VcoreDiagnostics.ResolveRegisters(CpuFamily, CpuModel, CpuPackage);
        Dictionary<uint, OperationResult<uint>> readings =
            new(descriptors.Count);

        if (descriptors.Count == 0)
        {
            return readings;
        }

        try
        {
            // One PCI-bus lock keeps every candidate in this diagnostic
            // sample close in time and avoids paying a separate lock timeout
            // for each fixed whitelist address.
            using PciBusLock pciBusLock = new();
            foreach (VcoreDiagnosticRegisterDescriptor descriptor in descriptors)
            {
                uint value = 0;
                readings[descriptor.Address] = _cpu.ReadDwordExNoLock(
                    descriptor.Address,
                    ref value,
                    maxRetries: 1)
                    ? OperationResult<uint>.Ok(value)
                    : OperationResult<uint>.Fail(
                        $"PawnIO could not read SMN register " +
                        $"0x{descriptor.Address:X8}.");
            }
        }
        catch (Exception ex)
        {
            foreach (VcoreDiagnosticRegisterDescriptor descriptor in descriptors)
            {
                if (!readings.ContainsKey(descriptor.Address))
                {
                    readings[descriptor.Address] = OperationResult<uint>.Fail(
                        $"Failed to read SMN register " +
                        $"0x{descriptor.Address:X8}: {ex.Message}");
                }
            }
        }

        return readings;
    }

    private bool TryReadSmn(uint address, out uint value)
    {
        value = 0;
        return _cpu.ReadDwordEx(address, ref value);
    }

    public DowncoreOperationResult SetDisabledCores(
        IReadOnlySet<int> physicalCoreIndices)
    {
        return AmdAcpiDowncoreController.Apply(CcdCount, physicalCoreIndices);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cpu.Dispose();
        _disposed = true;
    }
}
