namespace ryzen_smu_cli.Tests;

internal sealed class FakePrivilegeChecker(
    bool isWindows,
    bool isAdministrator) : IPrivilegeChecker
{
    public bool IsWindows { get; } = isWindows;

    public bool IsAdministrator { get; } = isAdministrator;

    public static FakePrivilegeChecker Administrator() => new(true, true);
}

internal sealed class FakeRyzenController : IRyzenController
{
    private static readonly IReadOnlyDictionary<uint, OperationResult<uint>>
        EmptyRegisterReads = new Dictionary<uint, OperationResult<uint>>();

    public FakeRyzenController(
        int physicalCoreSlots,
        IEnumerable<int> enabledPhysicalCores)
    {
        PhysicalCoreSlots = physicalCoreSlots;
        int[] enabled = enabledPhysicalCores.ToArray();
        EnabledCoreCount = enabled.Length;
        CcdCount = (physicalCoreSlots + 7) / 8;
        FactoryCoreDisableMasks = Enumerable
            .Repeat((byte)0, CcdCount)
            .ToArray();

        foreach (int physicalCore in enabled)
        {
            Offsets[physicalCore] = 0;
        }

        PboOffsetCandidates = Enumerable
            .Range(0, physicalCoreSlots)
            .Select(CoreAddress.FromPhysicalCoreIndex)
            .ToArray();
    }

    public CpuInformation Information { get; } = new(
        "AMD Ryzen 7 9800X3D 8-Core Processor",
        "B40F40",
        "GraniteRidge",
        "44",
        "FPX",
        1,
        1,
        8,
        16,
        2,
        true,
        "Micro-Star International Co., Ltd.",
        "MPG X870I EDGE TI EVO WIFI (MS-7E50)",
        "1.A32",
        "0B404035",
        "98.83.0",
        0x00620105,
        0x724,
        "VDDCR CPU Telemetry (entry 49, verified)");

    public uint CpuFamily => 0x1A;

    public uint CpuModel => 0x44;

    public uint CpuPackage => 0;

    public int CcdCount { get; }

    public int PhysicalCoreSlots { get; }

    public int EnabledCoreCount { get; }

    public IReadOnlyList<byte> FactoryCoreDisableMasks { get; }

    public IReadOnlyList<CoreAddress> PboOffsetCandidates { get; init; }

    public IReadOnlyList<CoreAddress> PboOffsetFallbackCandidates
    {
        get;
        init;
    } = [];

    public bool HasUsableCoreTopology { get; init; } = true;

    public string? CoreTopologyUnavailableReason { get; init; }

    public bool CanReadPboOffsets => true;

    public bool CanWritePboOffsets => true;

    public bool CanReadFMax => true;

    public bool CanWriteFMax => true;

    public bool CanWritePptLimit => true;

    public bool CanWriteTdcLimit => true;

    public bool CanWriteEdcLimit => true;

    public bool CanReadVcore => true;

    public string? VcoreReadUnavailableReason => null;

    public bool CanReadCpuTemperature => true;

    public string? CpuTemperatureReadUnavailableReason => null;

    public Dictionary<int, int> Offsets { get; } = [];

    public List<(CoreAddress Core, int Offset)> OffsetWrites { get; } = [];

    public List<CoreAddress> OffsetReads { get; } = [];

    public int OffsetReadCount => OffsetReads.Count;

    public OperationResult<uint> GetFMaxResult { get; init; } =
        OperationResult<uint>.Ok(5250);

    public List<uint> FMaxWrites { get; } = [];

    public int FMaxReadCount { get; private set; }

    public List<uint> PptWrites { get; } = [];

    public List<uint> TdcWrites { get; } = [];

    public List<uint> EdcWrites { get; } = [];

    public OperationResult<double> GetVcoreResult { get; init; } =
        OperationResult<double>.Ok(1.225);

    public Func<OperationResult<double>>? GetVcoreHandler { get; init; }

    public int VcoreReadCount { get; private set; }

    public OperationResult<double> GetCpuTemperatureResult { get; init; } =
        OperationResult<double>.Ok(70.0);

    public Func<OperationResult<double>>? GetCpuTemperatureHandler { get; init; }

    public int CpuTemperatureReadCount { get; private set; }

    public Func<IReadOnlyList<SystemTelemetryTarget>,
        OperationResult<SystemTelemetryRawSnapshot>>?
        ReadSystemTelemetrySnapshotHandler
    { get; init; }

    public int SystemTelemetryReadCount { get; private set; }

    public OperationResult<int> GetPboOffset(CoreAddress core)
    {
        OffsetReads.Add(core);
        return Offsets.TryGetValue(core.PhysicalCoreIndex, out int value)
            ? OperationResult<int>.Ok(value)
            : OperationResult<int>.Fail("disabled");
    }

    public OperationResult SetPboOffset(CoreAddress core, int offset)
    {
        OffsetWrites.Add((core, offset));
        Offsets[core.PhysicalCoreIndex] = offset;
        return OperationResult.Ok();
    }

    public OperationResult<float> GetPboScalar() =>
        OperationResult<float>.Ok(1.0f);

    public OperationResult SetPboScalar(int scalar) => OperationResult.Ok();

    public OperationResult<uint> GetFMax()
    {
        FMaxReadCount++;
        return GetFMaxResult;
    }

    public OperationResult SetFMax(uint megahertz)
    {
        FMaxWrites.Add(megahertz);
        return OperationResult.Ok();
    }

    public OperationResult SetPptLimit(uint watts)
    {
        PptWrites.Add(watts);
        return OperationResult.Ok();
    }

    public OperationResult SetTdcLimit(uint amps)
    {
        TdcWrites.Add(amps);
        return OperationResult.Ok();
    }

    public OperationResult SetEdcLimit(uint amps)
    {
        EdcWrites.Add(amps);
        return OperationResult.Ok();
    }

    public OperationResult<double> GetVcore()
    {
        VcoreReadCount++;
        return GetVcoreHandler?.Invoke() ?? GetVcoreResult;
    }

    public OperationResult<double> GetCpuTemperature()
    {
        CpuTemperatureReadCount++;
        return GetCpuTemperatureHandler?.Invoke() ?? GetCpuTemperatureResult;
    }

    public OperationResult<SystemTelemetryRawSnapshot>
        ReadSystemTelemetrySnapshot(
            IReadOnlyList<SystemTelemetryTarget> targets)
    {
        SystemTelemetryReadCount++;
        return ReadSystemTelemetrySnapshotHandler?.Invoke(targets) ??
            OperationResult<SystemTelemetryRawSnapshot>.Fail(
                "System telemetry fake was not configured.");
    }

    public IReadOnlyDictionary<uint, OperationResult<uint>>
        ReadVcoreDiagnosticRegisters() => EmptyRegisterReads;

    public DowncoreOperationResult SetDisabledCores(
        IReadOnlySet<int> physicalCoreIndices) =>
        DowncoreOperationResult.Ok(
            Enumerable.Repeat((byte)0, CcdCount).ToArray());

    public void Dispose()
    {
    }
}
