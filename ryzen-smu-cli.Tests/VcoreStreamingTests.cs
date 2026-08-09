namespace ryzen_smu_cli.Tests;

public sealed class VcoreStreamingTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(500, true)]
    [InlineData(60_000, true)]
    [InlineData(60_001, false)]
    public void TelemetryIntervalUsesIndependentUserInterfaceRange(
        int intervalMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            CpuTelemetryStreaming.IsValidInterval(intervalMilliseconds));
    }

    [Theory]
    [InlineData(0, 10, 100, 100)]
    [InlineData(100, 150, 100, 200)]
    [InlineData(100, 200, 100, 300)]
    [InlineData(100, 350, 100, 400)]
    [InlineData(0, 250, 100, 300)]
    public void NextSampleDueIsAlwaysAFutureCadenceBoundary(
        long previousDueMilliseconds,
        long elapsedMilliseconds,
        int intervalMilliseconds,
        long expectedDueMilliseconds)
    {
        long nextDueMilliseconds =
            VcoreStreaming.GetNextSampleDueMilliseconds(
                previousDueMilliseconds,
                elapsedMilliseconds,
                intervalMilliseconds);

        Assert.Equal(expectedDueMilliseconds, nextDueMilliseconds);
        Assert.True(nextDueMilliseconds > elapsedMilliseconds);
        Assert.Equal(
            0,
            (nextDueMilliseconds - previousDueMilliseconds) %
                intervalMilliseconds);
    }
}
