namespace ryzen_smu_cli.Tests;

public sealed class CpuTemperatureTelemetryTests
{
    [Theory]
    [InlineData(0x17)]
    [InlineData(0x19)]
    [InlineData(0x1A)]
    public void SupportedZenFamiliesUseFixedReadOnlyThermalRegister(uint family)
    {
        Assert.True(CpuTemperatureTelemetry.TryResolve(
            family,
            out CpuTemperatureTelemetry? telemetry));
        Assert.Equal(family, telemetry!.CpuFamily);
        Assert.Equal(0x00059800u, telemetry.SmnRegister);
        Assert.Equal("tctl-tdie", telemetry.SourceName);
    }

    [Theory]
    [InlineData(0x16)]
    [InlineData(0x18)]
    [InlineData(0x1B)]
    public void UnknownFamiliesAreNotGuessed(uint family)
    {
        Assert.False(CpuTemperatureTelemetry.TryResolve(family, out _));
    }

    [Fact]
    public void StandardRangeTemperatureIsDecodedInEighthDegrees()
    {
        CpuTemperatureTelemetry telemetry = Resolve();

        OperationResult<double> result = telemetry.Decode(
            EncodeTemperature(71.875, alternateRange: false),
            "AMD Ryzen 7 9800X3D 8-Core Processor");

        Assert.True(result.Success);
        Assert.Equal(71.875, result.Value, precision: 3);
    }

    [Fact]
    public void AlternateRangeSubtractsFortyNineDegrees()
    {
        CpuTemperatureTelemetry telemetry = Resolve();

        OperationResult<double> result = telemetry.Decode(
            EncodeTemperature(25.0, alternateRange: true),
            "AMD Ryzen 9 9950X 16-Core Processor");

        Assert.True(result.Success);
        Assert.Equal(25.0, result.Value, precision: 3);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", 70.0)]
    [InlineData("AMD Ryzen 5 1600X Six-Core Processor", 60.0)]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor", 60.0)]
    [InlineData("AMD Ryzen 7 1800X Eight-Core Processor", 60.0)]
    [InlineData("AMD Ryzen Threadripper 1950X 16-Core Processor", 53.0)]
    [InlineData("AMD Ryzen Threadripper 2990WX 32-Core Processor", 53.0)]
    public void LegacyTctlOffsetsMatchQualifiedSkuRules(
        string cpuName,
        double expectedTemperature)
    {
        OperationResult<double> result = Resolve().Decode(
            EncodeTemperature(80.0, alternateRange: false),
            cpuName);

        Assert.True(result.Success);
        Assert.Equal(expectedTemperature, result.Value, precision: 3);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void SentinelValuesAreRejected(uint rawValue)
    {
        OperationResult<double> result = Resolve().Decode(
            rawValue,
            "AMD Ryzen 7 9800X3D 8-Core Processor");

        Assert.False(result.Success);
        Assert.Contains("sentinel", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplausibleTemperatureIsRejected()
    {
        OperationResult<double> result = Resolve().Decode(
            EncodeTemperature(160.0, alternateRange: false),
            "AMD Ryzen 7 9800X3D 8-Core Processor");

        Assert.False(result.Success);
        Assert.Contains("implausible", result.Error, StringComparison.Ordinal);
    }

    private static CpuTemperatureTelemetry Resolve()
    {
        Assert.True(CpuTemperatureTelemetry.TryResolve(
            0x1A,
            out CpuTemperatureTelemetry? telemetry));
        return telemetry!;
    }

    private static uint EncodeTemperature(
        double temperatureCelsius,
        bool alternateRange)
    {
        double encodedTemperature = alternateRange
            ? temperatureCelsius + 49.0
            : temperatureCelsius;
        uint eighthDegrees = checked((uint)Math.Round(
            encodedTemperature * 8.0,
            MidpointRounding.AwayFromZero));
        return checked(eighthDegrees << 21) |
               (alternateRange ? 0x00080000u : 0u);
    }
}
