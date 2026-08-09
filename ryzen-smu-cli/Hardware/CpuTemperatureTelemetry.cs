namespace ryzen_smu_cli;

internal sealed record CpuTemperatureTelemetry(
    uint CpuFamily,
    uint SmnRegister,
    string SourceName)
{
    public const string PublicSourceName = "tctl-tdie";

    private const uint ThermalControlRegister = 0x00059800;
    private const uint TemperatureRangeSelectMask = 0x00080000;
    private const double MinimumPlausibleTemperatureCelsius = -50.0;
    private const double MaximumPlausibleTemperatureCelsius = 150.0;

    public static bool TryResolve(
        uint cpuFamily,
        out CpuTemperatureTelemetry? telemetry)
    {
        if (cpuFamily is 0x17 or 0x19 or 0x1A)
        {
            telemetry = new CpuTemperatureTelemetry(
                cpuFamily,
                ThermalControlRegister,
                PublicSourceName);
            return true;
        }

        telemetry = null;
        return false;
    }

    public OperationResult<double> Decode(uint rawValue, string? cpuName)
    {
        if (rawValue is 0 or uint.MaxValue)
        {
            return OperationResult<double>.Fail(
                $"SMU {SourceName} telemetry returned a sentinel value " +
                $"(0x{rawValue:X8}).");
        }

        double temperatureCelsius = (rawValue >> 21) * 0.125;
        temperatureCelsius += ResolveTctlOffset(cpuName);
        if ((rawValue & TemperatureRangeSelectMask) != 0)
        {
            temperatureCelsius -= 49.0;
        }

        if (!double.IsFinite(temperatureCelsius) ||
            temperatureCelsius < MinimumPlausibleTemperatureCelsius ||
            temperatureCelsius > MaximumPlausibleTemperatureCelsius)
        {
            return OperationResult<double>.Fail(
                $"SMU {SourceName} telemetry returned an implausible " +
                $"temperature ({temperatureCelsius} C, raw 0x{rawValue:X8}).");
        }

        return OperationResult<double>.Ok(temperatureCelsius);
    }

    private static double ResolveTctlOffset(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName))
        {
            return 0.0;
        }

        if (cpuName.Contains("2700X", StringComparison.OrdinalIgnoreCase))
        {
            return -10.0;
        }

        if (cpuName.Contains("1600X", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("1700X", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("1800X", StringComparison.OrdinalIgnoreCase))
        {
            return -20.0;
        }

        return cpuName.Contains(
            "Threadripper 19",
            StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains(
                "Threadripper 29",
                StringComparison.OrdinalIgnoreCase)
            ? -27.0
            : 0.0;
    }
}
