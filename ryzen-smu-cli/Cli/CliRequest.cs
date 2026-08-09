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
    public bool StreamVcore => VcoreStreamIntervalMilliseconds.HasValue;

    public bool DiagnoseVcore { get; init; }

    public bool GetCpuTemperature { get; init; }

    public int? TelemetryStreamIntervalMilliseconds { get; init; }

    public bool StreamTelemetry =>
        TelemetryStreamIntervalMilliseconds.HasValue;

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
        FMax is not null ||
        GetFMax ||
        GetVcore ||
        StreamVcore ||
        DiagnoseVcore ||
        GetCpuTemperature ||
        StreamTelemetry ||
        ShowInfo;
}
