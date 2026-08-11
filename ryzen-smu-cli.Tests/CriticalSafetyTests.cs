using System.Globalization;
using System.Text.Json;

namespace ryzen_smu_cli.Tests;

public sealed class CriticalSafetyTests
{
    [Fact]
    public void InvalidArgumentsAreRejectedBeforeHardwareInitialization()
    {
        string[][] invalidArgumentSets =
        [
            ["--set-pbo-scalar", "0"],
            ["--set-fmax", "5226"],
            ["--offset", "0:-10,-20"],
            ["--disable-cores", "1,1"],
            ["--stream-vcore", "--interval-ms", "49"],
            ["--stream-vcore", "--get-vcore"],
            ["--stream-telemetry", "--interval-ms", "99"],
            ["--stream-telemetry", "--get-cpu-temperature"],
            ["--diagnose-vcore", "--samples", "0"],
        ];

        foreach (string[] arguments in invalidArgumentSets)
        {
            int factoryCalls = 0;

            int exitCode = CliApplication.Run(
                arguments,
                () =>
                {
                    factoryCalls++;
                    return new FakeRyzenController(
                        8,
                        Enumerable.Range(0, 8));
                },
                FakePrivilegeChecker.Administrator(),
                new StringWriter(),
                new StringWriter());

            Assert.NotEqual((int)ExitCode.Success, exitCode);
            Assert.Equal(0, factoryCalls);
        }
    }

    [Fact]
    public void CurveOptimizerWriteIsReadBackFromTheSameSelector()
    {
        FakeRyzenController controller = new(
            physicalCoreSlots: 24,
            enabledPhysicalCores: Enumerable.Range(0, 24));
        StringWriter output = new();
        Assert.True(OffsetSpecification.TryParse(
            "16:-25",
            out OffsetSpecification? offsets,
            out string? parseError), parseError);

        int exitCode = CreateRunner(controller, output).Execute(
            EmptyRequest() with { OffsetSpecification = offsets });

        Assert.Equal((int)ExitCode.Success, exitCode);
        (CoreAddress writtenSelector, int writtenOffset) =
            Assert.Single(controller.OffsetWrites);
        Assert.Equal(CoreAddress.FromPhysicalCoreIndex(16), writtenSelector);
        Assert.Equal(-25, writtenOffset);
        Assert.Equal(25, controller.OffsetReadCount);
        Assert.Equal(writtenSelector, controller.OffsetReads[^1]);
        Assert.Contains("(verified)", output.ToString());
    }

    [Fact]
    public void CompactMappingSurvivesAWholeDisabledCcd()
    {
        CoreAddress[] survivingCcd = Enumerable
            .Range(8, 8)
            .Select(CoreAddress.FromPhysicalCoreIndex)
            .Reverse()
            .ToArray();
        FakeRyzenController controller = new(
            physicalCoreSlots: 8,
            enabledPhysicalCores: Enumerable.Range(8, 8))
        {
            PboOffsetCandidates = survivingCcd,
            HasUsableCoreTopology = false,
            CoreTopologyUnavailableReason =
                "The complete fuse map is unavailable.",
        };

        OperationResult<CompactCoreMap> mapResult = CoreMapper.Map(controller);

        Assert.True(mapResult.Success, mapResult.Error);
        Assert.Equal(Enumerable.Range(0, 8),
            mapResult.Value!.Entries.Select(entry => entry.CompactIndex));
        Assert.Equal(Enumerable.Range(8, 8),
            mapResult.Value.Entries.Select(
                entry => entry.Address.PhysicalCoreIndex));

        Assert.True(OffsetSpecification.TryParse(
            "0:-15",
            out OffsetSpecification? offsets,
            out string? parseError), parseError);
        int exitCode = CreateRunner(controller).Execute(
            EmptyRequest() with { OffsetSpecification = offsets });

        Assert.Equal((int)ExitCode.Success, exitCode);
        (CoreAddress selector, int offset) =
            Assert.Single(controller.OffsetWrites);
        Assert.Equal(8, selector.PhysicalCoreIndex);
        Assert.Equal(-15, offset);
    }

    [Fact]
    public void TerseOffsetsRemainOneSelectorOrderedCsvLine()
    {
        FakeRyzenController controller = new(
            4,
            Enumerable.Range(0, 4));
        controller.Offsets[0] = -10;
        controller.Offsets[1] = 0;
        controller.Offsets[2] = 10;
        controller.Offsets[3] = -20;
        StringWriter output = new();

        int exitCode = CreateRunner(controller, output).Execute(
            EmptyRequest() with { GetOffsetsTerse = true });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(
            $"-10,0,10,-20{Environment.NewLine}",
            output.ToString());
        Assert.Equal(4, controller.OffsetReadCount);
    }

    [Fact]
    public void CombinedOffsetCommandIsOrderedVerifiedAndReusesReadBack()
    {
        FakeRyzenController controller = new(
            3,
            Enumerable.Range(0, 3));
        controller.Offsets[0] = -1;
        controller.Offsets[1] = -2;
        controller.Offsets[2] = -3;
        StringWriter output = new();
        Assert.True(OffsetSpecification.TryParse(
            "2:-12,0:-10,1:-11",
            out OffsetSpecification? offsets,
            out string? parseError), parseError);

        int exitCode = CreateRunner(controller, output).Execute(
            EmptyRequest() with
            {
                OffsetSpecification = offsets,
                GetOffsetsTerse = true,
            });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal([0, 1, 2], controller.OffsetWrites
            .Select(write => write.Core.PhysicalCoreIndex));
        Assert.Equal([-10, -11, -12], controller.OffsetWrites
            .Select(write => write.Offset));
        Assert.Equal(6, controller.OffsetReadCount);
        string[] lines = output
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.All(lines[..3], line => Assert.Contains("(verified)", line));
        Assert.Equal("-10,-11,-12", lines[3]);
    }

    [Fact]
    public void FMaxValidationAndControllerWireRemainStable()
    {
        foreach ((string text, uint expected) in new[]
                 {
                     ("5225", 5225u),
                     ("5250", 5250u),
                 })
        {
            Assert.True(FMaxFrequency.TryParse(
                text,
                out FMaxFrequency frequency,
                out string? error), error);
            Assert.Equal(expected, frequency.Megahertz);
        }

        Assert.False(FMaxFrequency.TryParse(
            "5226",
            out _,
            out string? invalidError));
        Assert.False(string.IsNullOrWhiteSpace(invalidError));

        FakeRyzenController controller = new(
            8,
            Enumerable.Range(0, 8));
        StringWriter output = new();
        int exitCode = CliApplication.Run(
            ["--set-fmax", "5225", "--get-fmax"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal([5225u], controller.FMaxWrites);
        Assert.Equal(1, controller.FMaxReadCount);
        Assert.Contains("Set FMax to 5225 MHz.", output.ToString());
        Assert.Contains("Current FMax: 5250 MHz.", output.ToString());
    }

    [Fact]
    public void VcoreReadUsesInvariantSixDecimalOutput()
    {
        FakeRyzenController controller = new(
            8,
            Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Ok(1.225),
        };
        StringWriter output = new();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            int exitCode = CreateRunner(controller, output).Execute(
                EmptyRequest() with { GetVcore = true });

            Assert.Equal((int)ExitCode.Success, exitCode);
            Assert.Equal(
                $"Current Vcore: 1.225000 V.{Environment.NewLine}",
                output.ToString());
            Assert.Equal(1, controller.VcoreReadCount);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void VcoreStreamRemainsAStableFiveColumnProtocol()
    {
        using CancellationTokenSource cancellationSource = new();
        FakeRyzenController controller = new(
            8,
            Enumerable.Range(0, 8))
        {
            HasUsableCoreTopology = false,
            GetVcoreHandler = () =>
            {
                cancellationSource.Cancel();
                return OperationResult<double>.Ok(1.1875);
            },
        };
        StringWriter output = new();

        int exitCode = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token).Execute(
                EmptyRequest() with
                {
                    VcoreStreamIntervalMilliseconds = 150,
                });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        string[] fields = output
            .ToString()
            .TrimEnd('\r', '\n')
            .Split('\t');
        Assert.Equal(5, fields.Length);
        Assert.Equal("VCORE", fields[0]);
        Assert.Equal("0", fields[1]);
        Assert.True(long.TryParse(
            fields[2],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long elapsedMilliseconds));
        Assert.True(elapsedMilliseconds >= 0);
        Assert.True(DateTimeOffset.TryParseExact(
            fields[3],
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _));
        Assert.Equal("1.187500", fields[4]);
    }

    [Fact]
    public void TelemetryStreamRemainsAnEightFieldJsonLine()
    {
        using CancellationTokenSource cancellationSource = new();
        FakeRyzenController controller = new(
            8,
            Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Ok(1.1875),
            GetCpuTemperatureHandler = () =>
            {
                cancellationSource.Cancel();
                return OperationResult<double>.Ok(71.875);
            },
        };
        StringWriter output = new();

        int exitCode = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token).Execute(
                EmptyRequest() with
                {
                    TelemetryStreamIntervalMilliseconds = 500,
                });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(1, controller.CpuTemperatureReadCount);
        Assert.Single(output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(8, root.EnumerateObject().Count());
        Assert.Equal("telemetry", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, root.GetProperty("sequence").GetInt64());
        Assert.True(root.GetProperty("elapsedMilliseconds").GetInt64() >= 0);
        string timestamp = root.GetProperty("timestampUtc").GetString()!;
        Assert.EndsWith("+00:00", timestamp, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTimeOffset parsedTimestamp));
        Assert.Equal(TimeSpan.Zero, parsedTimestamp.Offset);
        Assert.Equal(
            1.1875,
            root.GetProperty("vcoreVolts").GetDouble(),
            precision: 6);
        Assert.Equal(
            71.875,
            root.GetProperty("cpuTemperatureCelsius").GetDouble(),
            precision: 3);
        Assert.Equal(
            "tctl-tdie",
            root.GetProperty("temperatureSource").GetString());
    }

    [Fact]
    public void TelemetryReadFailuresNeverEmitPartialSamples()
    {
        FakeRyzenController vcoreFailureController = new(
            8,
            Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Fail(
                "Vcore telemetry refresh failed"),
        };
        StringWriter vcoreOutput = new();
        StringWriter vcoreError = new();

        int vcoreExitCode = CreateRunner(
            vcoreFailureController,
            vcoreOutput,
            vcoreError).Execute(
                EmptyRequest() with
                {
                    TelemetryStreamIntervalMilliseconds = 500,
                });

        Assert.Equal((int)ExitCode.OperationFailed, vcoreExitCode);
        Assert.Equal(string.Empty, vcoreOutput.ToString());
        Assert.Equal(1, vcoreFailureController.VcoreReadCount);
        Assert.Equal(0, vcoreFailureController.CpuTemperatureReadCount);
        Assert.Contains("Vcore telemetry refresh failed", vcoreError.ToString());

        FakeRyzenController temperatureFailureController = new(
            8,
            Enumerable.Range(0, 8))
        {
            GetCpuTemperatureResult = OperationResult<double>.Fail(
                "temperature telemetry refresh failed"),
        };
        StringWriter temperatureOutput = new();
        StringWriter temperatureError = new();

        int temperatureExitCode = CreateRunner(
            temperatureFailureController,
            temperatureOutput,
            temperatureError).Execute(
                EmptyRequest() with
                {
                    TelemetryStreamIntervalMilliseconds = 500,
                });

        Assert.Equal((int)ExitCode.OperationFailed, temperatureExitCode);
        Assert.Equal(string.Empty, temperatureOutput.ToString());
        Assert.Equal(1, temperatureFailureController.VcoreReadCount);
        Assert.Equal(1, temperatureFailureController.CpuTemperatureReadCount);
        Assert.Contains(
            "temperature telemetry refresh failed",
            temperatureError.ToString());
    }

    [Fact]
    public void PmTableResolutionRequiresFullIdentityAndKeepsGraniteRidgeRail()
    {
        (uint Family, uint Model, uint Package, uint Version, uint Size, int Index)[]
            knownLayouts =
            [
                (0x19, 0x61, 0, 0x00540000, 0x0828, 47),
                (0x19, 0x61, 0, 0x00540001, 0x082C, 47),
                (0x19, 0x61, 0, 0x00540002, 0x087C, 47),
                (0x19, 0x61, 0, 0x00540003, 0x089C, 47),
                (0x19, 0x61, 0, 0x00540004, 0x08BC, 47),
                (0x19, 0x61, 0, 0x00540005, 0x08C8, 47),
                (0x19, 0x61, 0, 0x00540100, 0x0618, 47),
                (0x19, 0x61, 0, 0x00540101, 0x061C, 47),
                (0x19, 0x61, 0, 0x00540102, 0x066C, 47),
                (0x19, 0x61, 0, 0x00540103, 0x068C, 47),
                (0x19, 0x61, 0, 0x00540104, 0x06A8, 47),
                (0x19, 0x61, 0, 0x00540105, 0x06B4, 47),
                (0x19, 0x61, 0, 0x00540108, 0x06BC, 47),
                (0x19, 0x61, 1, 0x00540208, 0x08D0, 48),
                (0x1A, 0x44, 0, 0x00620105, 0x0724, 49),
                (0x1A, 0x44, 0, 0x00620205, 0x0994, 49),
                (0x1A, 0x44, 0, 0x00621102, 0x0724, 49),
                (0x1A, 0x44, 0, 0x00621202, 0x0994, 49),
                (0x1A, 0x44, 1, 0x00621102, 0x0724, 49),
                (0x1A, 0x44, 1, 0x00621202, 0x0994, 49),
            ];

        foreach ((uint family,
                     uint model,
                     uint package,
                     uint version,
                     uint size,
                     int index) in knownLayouts)
        {
            Assert.True(VcoreTelemetryLayout.TryResolve(
                family,
                model,
                package,
                version,
                size,
                out VcoreTelemetryLayout? layout));
            Assert.NotNull(layout);
            Assert.Equal(family, layout!.CpuFamily);
            Assert.Equal(model, layout.CpuModel);
            Assert.Equal(
                package == 1
                    ? VcorePackageClass.Mobile
                    : VcorePackageClass.Desktop,
                layout.PackageClass);
            Assert.Equal(version, layout.PmTableVersion);
            Assert.Equal(size, layout.PmTableSize);
            Assert.Equal(index, layout.ValueIndex);
            Assert.Equal("VDDCR CPU Telemetry", layout.SourceName);
        }

        Assert.False(VcoreTelemetryLayout.TryResolve(
            0x1A,
            0x44,
            0,
            0x00620105,
            0x0994,
            out _));
        Assert.False(VcoreTelemetryLayout.TryResolve(
            0x19,
            0x61,
            1,
            0x00540104,
            0x06A8,
            out _));
        Assert.False(VcoreTelemetryLayout.TryResolve(
            0x19,
            0x74,
            0,
            0x00540104,
            0x06A8,
            out _));
        Assert.False(VcoreTelemetryLayout.TryResolve(
            0x1A,
            0x44,
            0,
            0x00650005,
            0x0B78,
            out _));

        Assert.True(VcoreTelemetryLayout.TryResolve(
            0x1A,
            0x44,
            0,
            0x00620105,
            0x0724,
            out VcoreTelemetryLayout? graniteRidge));
        float[] table = new float[272];
        table[18] = 1.37f;
        table[19] = 1.31f;
        table[48] = 1.31f;
        table[49] = 1.28f;
        table[271] = 1.26f;

        OperationResult<double> result = graniteRidge!.Read(table);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1.28, result.Value, precision: 6);
    }

    [Fact]
    public void UnknownAndSentinelSviAndTemperatureDataFailClosed()
    {
        foreach ((uint family, uint model) in new[]
                 {
                     (0x19u, 0x61u),
                     (0x1Au, 0x44u),
                     (0x19u, 0x21u),
                     (0x17u, 0x71u),
                 })
        {
            Assert.False(SviTfnVcoreTelemetry.TryResolve(
                family,
                model,
                out _));
        }

        Assert.True(SviTfnVcoreTelemetry.TryResolve(
            0x19,
            0x74,
            out SviTfnVcoreTelemetry? svi));
        foreach (uint rawValue in new[] { 0u, uint.MaxValue })
        {
            OperationResult<double> result = svi!.Decode(rawValue);
            Assert.False(result.Success);
            Assert.Contains("sentinel", result.Error);
        }

        OperationResult<double> emptyVid = svi!.Decode(0x00000001);
        Assert.False(emptyVid.Success);
        Assert.Contains("empty", emptyVid.Error);

        foreach (uint unknownFamily in new[] { 0x16u, 0x18u, 0x1Bu })
        {
            Assert.False(CpuTemperatureTelemetry.TryResolve(
                unknownFamily,
                out _));
        }

        Assert.True(CpuTemperatureTelemetry.TryResolve(
            0x1A,
            out CpuTemperatureTelemetry? temperature));
        foreach (uint rawValue in new[] { 0u, uint.MaxValue })
        {
            OperationResult<double> result = temperature!.Decode(
                rawValue,
                "AMD Ryzen 7 9800X3D 8-Core Processor");
            Assert.False(result.Success);
            Assert.Contains("sentinel", result.Error);
        }

        OperationResult<double> implausible = temperature!.Decode(
            EncodeTemperature(160.0),
            "AMD Ryzen 7 9800X3D 8-Core Processor");
        Assert.False(implausible.Success);
        Assert.Contains("implausible", implausible.Error);
    }

    [Fact]
    public void CadenceAlwaysAdvancesToAFutureMonotonicBoundary()
    {
        (long Previous, long Elapsed, int Interval, long Expected)[] cases =
        [
            (0, 10, 100, 100),
            (100, 150, 100, 200),
            (100, 200, 100, 300),
            (100, 350, 100, 400),
            (0, 250, 100, 300),
        ];

        foreach ((long previous,
                     long elapsed,
                     int interval,
                     long expected) in cases)
        {
            long next = VcoreStreaming.GetNextSampleDueMilliseconds(
                previous,
                elapsed,
                interval);

            Assert.Equal(expected, next);
            Assert.True(next > elapsed);
            Assert.True(next > previous);
            Assert.Equal(0, (next - previous) % interval);
        }
    }

    private static CommandRunner CreateRunner(
        FakeRyzenController controller,
        StringWriter? output = null,
        StringWriter? error = null) =>
        new(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output ?? new StringWriter(),
            error ?? new StringWriter());

    private static CommandRunner CreateRunnerWithCancellation(
        FakeRyzenController controller,
        StringWriter output,
        CancellationToken cancellationToken) =>
        new(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter(),
            cancellationToken);

    private static CliRequest EmptyRequest() =>
        new(
            null,
            null,
            false,
            false,
            false,
            false,
            null,
            false,
            null,
            false,
            false,
            null,
            false);

    private static uint EncodeTemperature(double temperatureCelsius)
    {
        uint eighthDegrees = checked((uint)Math.Round(
            temperatureCelsius * 8.0,
            MidpointRounding.AwayFromZero));
        return checked(eighthDegrees << 21);
    }
}
