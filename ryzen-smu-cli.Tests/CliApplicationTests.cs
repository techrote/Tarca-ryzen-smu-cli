using System.Text.Json;

namespace ryzen_smu_cli.Tests;

public sealed class CliApplicationTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void InformationalOptionsDoNotInitializeHardware(string option)
    {
        int factoryCalls = 0;
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = CliApplication.Run(
            [option],
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Must not be called");
            },
            FakePrivilegeChecker.Administrator(),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("--set-pbo-scalar", "0")]
    [InlineData("--set-pbo-scalar", "11")]
    [InlineData("--set-pbo-scalar", "not-a-number")]
    [InlineData("--set-fmax", "0")]
    [InlineData("--set-fmax", "5226")]
    [InlineData("--set-fmax", "not-a-number")]
    [InlineData("--offset", "0:-10,-20")]
    [InlineData("--disable-cores", "1,1")]
    [InlineData("--interval-ms", "150")]
    [InlineData("--samples", "20")]
    [InlineData("--diagnose-vcore", "--samples", "0")]
    [InlineData("--diagnose-vcore", "--samples", "1001")]
    [InlineData("--diagnose-vcore", "--samples")]
    [InlineData("--diagnose-vcore", "--interval-ms")]
    [InlineData("--diagnose-vcore", "--samples", "1", "--samples", "2")]
    [InlineData("--diagnose-vcore", "--interval-ms", "50", "--interval-ms", "60")]
    [InlineData("--stream-vcore", "--interval-ms", "49")]
    [InlineData("--stream-vcore", "--interval-ms", "60001")]
    [InlineData("--stream-vcore", "--get-vcore")]
    [InlineData("--stream-vcore", "--diagnose-vcore")]
    [InlineData("--stream-vcore", "--info")]
    [InlineData("--stream-telemetry", "--interval-ms", "99")]
    [InlineData("--stream-telemetry", "--interval-ms", "60001")]
    [InlineData("--stream-telemetry", "--get-vcore")]
    [InlineData("--stream-telemetry", "--get-cpu-temperature")]
    [InlineData("--stream-telemetry", "--stream-vcore")]
    [InlineData("--stream-telemetry", "--diagnose-vcore")]
    [InlineData("--stream-telemetry", "--info")]
    [InlineData("--diagnose-vcore", "--info")]
    public void InvalidArgumentsDoNotInitializeHardware(params string[] args)
    {
        int factoryCalls = 0;

        int exitCode = CliApplication.Run(
            args,
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            new StringWriter());

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void ExistingVcoreIntervalValidationKeepsItsErrorContract()
    {
        int factoryCalls = 0;
        StringWriter error = new();

        int exitCode = CliApplication.Run(
            ["--stream-vcore", "--interval-ms", "49"],
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            error);

        Assert.NotEqual((int)ExitCode.Success, exitCode);
        Assert.Equal(0, factoryCalls);
        Assert.Contains(
            "Vcore sample interval must be a whole number from 50 through " +
            "60000 milliseconds.",
            error.ToString());
        Assert.DoesNotContain("Telemetry stream interval", error.ToString());
    }

    [Fact]
    public void ExistingVcoreStreamConflictKeepsItsErrorContract()
    {
        int factoryCalls = 0;
        StringWriter error = new();

        int exitCode = CliApplication.Run(
            ["--stream-vcore", "--diagnose-vcore"],
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            error);

        Assert.NotEqual((int)ExitCode.Success, exitCode);
        Assert.Equal(0, factoryCalls);
        Assert.Contains(
            "--stream-vcore cannot be combined with --get-vcore or " +
            "--diagnose-vcore.",
            error.ToString());
    }

    [Theory]
    [InlineData("99")]
    [InlineData("not-a-number")]
    public void TelemetryIntervalUsesItsOwnErrorContract(string value)
    {
        int factoryCalls = 0;
        StringWriter error = new();

        int exitCode = CliApplication.Run(
            ["--stream-telemetry", "--interval-ms", value],
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            error);

        Assert.NotEqual((int)ExitCode.Success, exitCode);
        Assert.Equal(0, factoryCalls);
        Assert.Contains(
            "Telemetry stream interval must be a whole number from 100 through " +
            "60000 milliseconds.",
            error.ToString());
        Assert.DoesNotContain("Vcore sample interval", error.ToString());
    }

    [Fact]
    public void ValidFMaxIsPassedToTheController()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
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
    public void InfoIsAHardwareOperation()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter output = new();

        int exitCode = CliApplication.Run(
            ["--info"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Contains("B40F40 (GraniteRidge)", output.ToString());
        Assert.True(controller.Disposed);
    }

    [Fact]
    public void GetVcoreIsPassedToTheController()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Ok(1.2),
        };
        StringWriter output = new();

        int exitCode = CliApplication.Run(
            ["--get-vcore"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(
            $"Current Vcore: 1.200000 V.{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void GetCpuTemperatureIsPassedToTheController()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetCpuTemperatureResult = OperationResult<double>.Ok(71.875),
        };
        StringWriter output = new();

        int exitCode = CliApplication.Run(
            ["--get-cpu-temperature"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.CpuTemperatureReadCount);
        Assert.Equal(
            $"Current CPU temperature: 71.875 C.{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void HelpDocumentsVcoreAndTemperatureReadModes()
    {
        StringWriter output = new();

        int exitCode = CliApplication.Run(
            ["--help"],
            () => throw new InvalidOperationException("Must not be called"),
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Contains("--get-vcore", output.ToString());
        Assert.Contains("--stream-vcore", output.ToString());
        Assert.Contains("--diagnose-vcore", output.ToString());
        Assert.Contains("--get-cpu-temperature", output.ToString());
        Assert.Contains("--stream-telemetry", output.ToString());
        Assert.Contains("--samples", output.ToString());
        Assert.Contains("--interval-ms", output.ToString());
    }

    [Fact]
    public void VcoreDiagnosticProducesMachineReadableRawRegisterReport()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CpuFamily = 0x19,
            CpuModel = 0x74,
            HasUsableCoreTopology = false,
            CoreTopologyUnavailableReason =
                "Per-core selectors are not qualified for this topology.",
        };
        controller.SmuRegisterReads[0x0005A008] =
            OperationResult<uint>.Ok(0);
        controller.SmuRegisterReads[0x0006F038] =
            OperationResult<uint>.Ok(0x00002B01);
        StringWriter output = new();

        int exitCode = CliApplication.Run(
            ["--diagnose-vcore", "--samples", "1", "--interval-ms", "50"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter());

        Assert.Equal((int)ExitCode.Success, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith("0.4.0", root.GetProperty("toolVersion").GetString());
        Assert.Equal(1, root.GetProperty("samples").GetArrayLength());
        JsonElement extendedApu = root
            .GetProperty("samples")[0]
            .GetProperty("registers")
            .EnumerateArray()
            .Single(register =>
                register.GetProperty("address").GetString() == "0x0006F038");
        Assert.Equal("0x00002B01", extendedApu.GetProperty("raw").GetString());
        Assert.Equal(
            [1, 43, 0, 0],
            extendedApu
                .GetProperty("bytesLittleEndian")
                .EnumerateArray()
                .Select(item => item.GetInt32()));
        Assert.Equal(
            1.28125,
            extendedApu.GetProperty("voltsBits15To8").GetDouble(),
            precision: 6);
        JsonElement legacyStatus = root
            .GetProperty("samples")[0]
            .GetProperty("registers")
            .EnumerateArray()
            .Single(register =>
                register.GetProperty("address").GetString() == "0x0005A008");
        Assert.Equal(
            JsonValueKind.Null,
            legacyStatus.GetProperty("voltsBits23To16").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            legacyStatus.GetProperty("voltsBits15To8").ValueKind);
        Assert.Equal(
            "smn-svi",
            root.GetProperty("source").GetProperty("kind").GetString());
        JsonElement cpu = root.GetProperty("cpu");
        Assert.Equal(8, cpu.GetProperty("reportedPhysicalCoreCount").GetInt32());
        Assert.Equal(
            16,
            cpu.GetProperty("reportedLogicalProcessorCount").GetInt32());
        Assert.Equal(2, cpu.GetProperty("threadsPerCore").GetInt32());
        Assert.True(cpu.GetProperty("smtEnabled").GetBoolean());
        Assert.False(
            cpu.GetProperty("coreTopologyQualified").GetBoolean());
    }

    [Fact]
    public void PreCancelledVcoreDiagnosticProducesExplicitTerminationState()
    {
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CpuFamily = 0x19,
            CpuModel = 0x74,
        };
        StringWriter output = new();

        int exitCode = CliApplication.RunWithCancellation(
            ["--diagnose-vcore", "--samples", "40"],
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter(),
            cancellationSource.Token);

        Assert.Equal((int)ExitCode.Success, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("cancelled").GetBoolean());
        Assert.Equal(0, root.GetProperty("capturedSamples").GetInt32());
        Assert.Equal("cancelled", root.GetProperty("selectionState").GetString());
    }

    [Fact]
    public void ConflictingDowncoreOptionsAreRejectedBeforeHardwareAccess()
    {
        int factoryCalls = 0;

        int exitCode = CliApplication.Run(
            ["--disable-cores", "1", "--enable-all-cores"],
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            new StringWriter());

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void HardwareCommandChecksPrivilegeBeforeCreatingController()
    {
        int factoryCalls = 0;
        StringWriter error = new();

        int exitCode = CliApplication.Run(
            ["--get-pbo-scalar"],
            () =>
            {
                factoryCalls++;
                return new FakeRyzenController(8, Enumerable.Range(0, 8));
            },
            new FakePrivilegeChecker(true, false),
            new StringWriter(),
            error);

        Assert.Equal((int)ExitCode.NotAdministrator, exitCode);
        Assert.Equal(0, factoryCalls);
        Assert.Contains("administrator", error.ToString());
    }
}
