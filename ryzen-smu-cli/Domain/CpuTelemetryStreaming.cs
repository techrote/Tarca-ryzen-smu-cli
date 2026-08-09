namespace ryzen_smu_cli;

internal static class CpuTelemetryStreaming
{
    public const int DefaultIntervalMilliseconds = 500;
    public const int MinimumIntervalMilliseconds = 100;
    public const int MaximumIntervalMilliseconds = 60_000;

    public static bool IsValidInterval(int milliseconds) =>
        milliseconds is >= MinimumIntervalMilliseconds and
            <= MaximumIntervalMilliseconds;
}
