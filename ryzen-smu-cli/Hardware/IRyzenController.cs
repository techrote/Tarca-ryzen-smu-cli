namespace ryzen_smu_cli;

internal interface IRyzenController : IDisposable
{
    CpuInformation Information { get; }

    uint CpuFamily { get; }

    uint CpuModel { get; }

    uint CpuPackage { get; }

    int CcdCount { get; }

    int PhysicalCoreSlots { get; }

    int EnabledCoreCount { get; }

    IReadOnlyList<byte> FactoryCoreDisableMasks { get; }

    IReadOnlyList<CoreAddress> PboOffsetCandidates { get; }

    IReadOnlyList<CoreAddress> PboOffsetFallbackCandidates { get; }

    bool HasUsableCoreTopology { get; }

    string? CoreTopologyUnavailableReason { get; }

    bool CanReadPboOffsets { get; }

    bool CanWritePboOffsets { get; }

    bool CanReadFMax { get; }

    bool CanWriteFMax { get; }

    bool CanReadVcore { get; }

    string? VcoreReadUnavailableReason { get; }

    bool CanReadCpuTemperature { get; }

    string? CpuTemperatureReadUnavailableReason { get; }

    OperationResult<int> GetPboOffset(CoreAddress core);

    OperationResult SetPboOffset(CoreAddress core, int offset);

    OperationResult<float> GetPboScalar();

    OperationResult SetPboScalar(int scalar);

    OperationResult<uint> GetFMax();

    OperationResult SetFMax(uint megahertz);

    OperationResult<double> GetVcore();

    OperationResult<double> GetCpuTemperature();

    IReadOnlyDictionary<uint, OperationResult<uint>>
        ReadVcoreDiagnosticRegisters();

    DowncoreOperationResult SetDisabledCores(IReadOnlySet<int> physicalCoreIndices);
}
