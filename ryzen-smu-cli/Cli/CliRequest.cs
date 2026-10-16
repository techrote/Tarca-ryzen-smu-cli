namespace ryzen_smu_cli;

internal sealed record CliRequest(
    OffsetSpecification? OffsetSpecification,
    CoreSelection? DisabledCores,
    bool EnableAllCores,
    bool GetOffsetsTerse,
    bool GetPhysicalCores,
    bool GetEnabledCores,
    int? PboScalar,
    bool GetPboScalar,
    FMaxFrequency? FMax,
    bool GetFMax,
    bool GetVcore,
    int? VcoreStreamIntervalMilliseconds,
    bool ShowInfo)
{
    public const uint MinimumPowerLimitValue = 1;
    public const uint MaximumPowerLimitValue = 500;

    public bool StreamVcore => VcoreStreamIntervalMilliseconds.HasValue;

    public uint? PptWatts { get; init; }

    public uint? TdcAmps { get; init; }

    public uint? EdcAmps { get; init; }

    public bool GetPptLimit { get; init; }

    public bool GetTdcLimit { get; init; }

    public bool GetEdcLimit { get; init; }

    public bool DiagnoseVcore { get; init; }

    public bool GetCpuTemperature { get; init; }

    public int? TelemetryStreamIntervalMilliseconds { get; init; }

    public bool StreamTelemetry =>
        TelemetryStreamIntervalMilliseconds.HasValue;

    public int? SystemTelemetryStreamIntervalMilliseconds { get; init; }

    public SystemTelemetryTargetSet? SystemTelemetryTargets { get; init; }

    public bool StreamSystemTelemetry =>
        SystemTelemetryStreamIntervalMilliseconds.HasValue;

    public int VcoreDiagnosticSampleCount { get; init; } =
        VcoreDiagnostics.DefaultSampleCount;

    public int VcoreDiagnosticIntervalMilliseconds { get; init; } =
        VcoreStreaming.DefaultIntervalMilliseconds;

    public bool HasOperation =>
        OffsetSpecification is not null ||
        DisabledCores is not null ||
        EnableAllCores ||
        GetOffsetsTerse ||
        GetPhysicalCores ||
        GetEnabledCores ||
        PboScalar is not null ||
        GetPboScalar ||
        PptWatts is not null ||
        TdcAmps is not null ||
        EdcAmps is not null ||
        GetPptLimit ||
        GetTdcLimit ||
        GetEdcLimit ||
        FMax is not null ||
        GetFMax ||
        GetVcore ||
        StreamVcore ||
        DiagnoseVcore ||
        GetCpuTemperature ||
        StreamTelemetry ||
        StreamSystemTelemetry ||
        ShowInfo;
}
