using System.Text;

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
    private readonly HashSet<int> _enabledPhysicalCores;
    private bool _hasSuccessfulOffsetWrite;

    public FakeRyzenController(
        int physicalCoreSlots,
        IEnumerable<int> enabledPhysicalCores)
    {
        PhysicalCoreSlots = physicalCoreSlots;
        _enabledPhysicalCores = enabledPhysicalCores.ToHashSet();
        CcdCount = (physicalCoreSlots + 7) / 8;
        FactoryCoreDisableMasks = Enumerable
            .Repeat((byte)0, CcdCount)
            .ToArray();

        foreach (int physicalCore in _enabledPhysicalCores)
        {
            Offsets[physicalCore] = 0;
        }

        PboOffsetCandidates = Enumerable
            .Range(0, physicalCoreSlots)
            .Select(CoreAddress.FromPhysicalCoreIndex)
            .ToArray();
    }

    public CpuInformation Information { get; init; } = new(
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

    public uint CpuFamily { get; init; } = 0x1A;

    public uint CpuModel { get; init; } = 0x44;

    public uint CpuPackage { get; init; }

    public int CcdCount { get; }

    public int PhysicalCoreSlots { get; }

    public int EnabledCoreCount =>
        EnabledCoreCountOverride ?? _enabledPhysicalCores.Count;

    public int? EnabledCoreCountOverride { get; init; }

    public IReadOnlyList<byte> FactoryCoreDisableMasks { get; init; }

    public IReadOnlyList<CoreAddress> PboOffsetCandidates { get; init; }

    public IReadOnlyList<CoreAddress> PboOffsetFallbackCandidates
    {
        get;
        init;
    } = [];

    public bool HasUsableCoreTopology { get; init; } = true;

    public string? CoreTopologyUnavailableReason { get; init; }

    public bool CanReadPboOffsets { get; init; } = true;

    public bool CanWritePboOffsets { get; init; } = true;

    public bool CanReadFMax { get; init; } = true;

    public bool CanWriteFMax { get; init; } = true;

    public bool CanReadVcore { get; init; } = true;

    public string? VcoreReadUnavailableReason { get; init; }

    public bool CanReadCpuTemperature { get; init; } = true;

    public string? CpuTemperatureReadUnavailableReason { get; init; }

    public Dictionary<int, int> Offsets { get; } = [];

    public List<(CoreAddress Core, int Offset)> OffsetWrites { get; } = [];

    public int OffsetReadCount { get; private set; }

    public OperationResult SetOffsetResult { get; init; } = OperationResult.Ok();

    public OperationResult<int>? OffsetReadBackResult { get; init; }

    public OperationResult<float> GetScalarResult { get; init; } =
        OperationResult<float>.Ok(1.0f);

    public OperationResult SetScalarResult { get; init; } = OperationResult.Ok();

    public OperationResult<uint> GetFMaxResult { get; init; } =
        OperationResult<uint>.Ok(5250);

    public OperationResult SetFMaxResult { get; init; } = OperationResult.Ok();

    public OperationResult<double> GetVcoreResult { get; init; } =
        OperationResult<double>.Ok(1.225);

    public Func<OperationResult<double>>? GetVcoreHandler { get; init; }

    public OperationResult<double> GetCpuTemperatureResult { get; init; } =
        OperationResult<double>.Ok(70.0);

    public Func<OperationResult<double>>? GetCpuTemperatureHandler { get; init; }

    public Func<CoreAddress, int, OperationResult<int>>? GetPboOffsetHandler
    {
        get;
        init;
    }

    public int FMaxReadCount { get; private set; }

    public int VcoreReadCount { get; private set; }

    public int CpuTemperatureReadCount { get; private set; }

    public Dictionary<uint, OperationResult<uint>> SmuRegisterReads { get; } = [];

    public List<uint> FMaxWrites { get; } = [];

    public DowncoreOperationResult? DowncoreResult { get; init; }

    public int DowncoreWriteCount { get; private set; }

    public IReadOnlySet<int>? LastDisabledCores { get; private set; }

    public bool Disposed { get; private set; }

    public OperationResult<int> GetPboOffset(CoreAddress core)
    {
        OffsetReadCount++;
        if (_hasSuccessfulOffsetWrite && OffsetReadBackResult.HasValue)
        {
            return OffsetReadBackResult.Value;
        }

        if (GetPboOffsetHandler is not null)
        {
            return GetPboOffsetHandler(core, OffsetReadCount);
        }

        return Offsets.TryGetValue(core.PhysicalCoreIndex, out int value)
            ? OperationResult<int>.Ok(value)
            : OperationResult<int>.Fail("disabled");
    }

    public OperationResult SetPboOffset(CoreAddress core, int offset)
    {
        OffsetWrites.Add((core, offset));
        if (SetOffsetResult.Success)
        {
            Offsets[core.PhysicalCoreIndex] = offset;
            _hasSuccessfulOffsetWrite = true;
        }

        return SetOffsetResult;
    }

    public OperationResult<float> GetPboScalar() => GetScalarResult;

    public OperationResult SetPboScalar(int scalar) => SetScalarResult;

    public OperationResult<uint> GetFMax()
    {
        FMaxReadCount++;
        return GetFMaxResult;
    }

    public OperationResult SetFMax(uint megahertz)
    {
        FMaxWrites.Add(megahertz);
        return SetFMaxResult;
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

    public IReadOnlyDictionary<uint, OperationResult<uint>>
        ReadVcoreDiagnosticRegisters() => SmuRegisterReads;

    public DowncoreOperationResult SetDisabledCores(
        IReadOnlySet<int> physicalCoreIndices)
    {
        DowncoreWriteCount++;
        LastDisabledCores = physicalCoreIndices.ToHashSet();
        return DowncoreResult ??
               DowncoreOperationResult.Ok(
                   Enumerable.Repeat((byte)0, CcdCount).ToArray());
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

internal sealed class ThrowingTextWriter : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value) =>
        throw new IOException("The output pipe was closed.");
}
