using System.Globalization;
using System.Text.Json;

namespace ryzen_smu_cli.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public void MissingPawnIoReportsInstallationInstructions()
    {
        StringWriter error = new();
        CommandRunner runner = new(
            () => throw new PawnIoNotInstalledException(),
            FakePrivilegeChecker.Administrator(),
            new StringWriter(),
            error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetPboScalar = true,
        });

        Assert.Equal((int)ExitCode.InitializationFailed, exitCode);
        Assert.Contains("PawnIO is required", error.ToString());
        Assert.Contains("https://pawnio.eu/", error.ToString());
        Assert.Contains("interactively as an administrator", error.ToString());
    }

    [Fact]
    public void InfoPrintsCpuAndPlatformInformation()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            ShowInfo = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "CPU        AMD Ryzen 7 9800X3D 8-Core Processor",
                "CPUID      B40F40 (GraniteRidge)",
                "Model      44",
                "Package    FPX",
                "Config     1 CCD / 1 CCX / 8 physical cores",
                "Logical    16 logical processors",
                "SMT        Enabled (2 threads per core)",
                "MB Vendor  Micro-Star International Co., Ltd.",
                "MB Model   MPG X870I EDGE TI EVO WIFI (MS-7E50)",
                "BIOS       1.A32",
                "Firmware   0B404035",
                "SMU        98.83.0",
                "PM Version 0x00620105",
                "PM Size    0x00000724 bytes",
                "Vcore Map  VDDCR CPU Telemetry (entry 49, verified)",
                string.Empty),
            output.ToString());
    }

    [Fact]
    public void InfoAlwaysPrintsLogicalAndDisabledSmtLines()
    {
        CpuInformation singleThreadedInformation =
            new FakeRyzenController(8, Enumerable.Range(0, 8)).Information with
            {
                LogicalProcessorCount = 8,
                ThreadsPerCore = 1,
                SmtEnabled = false,
            };
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            Information = singleThreadedInformation,
        };
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            ShowInfo = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Contains(
            $"Logical    8 logical processors{Environment.NewLine}",
            output.ToString());
        Assert.Contains(
            $"SMT        Disabled (1 thread per core){Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void OffsetWriteUsesCoreOnThirdCcdAndReportsVerifiedSuccess()
    {
        FakeRyzenController controller = new(
            physicalCoreSlots: 24,
            enabledPhysicalCores: Enumerable.Range(0, 24));
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);
        OffsetSpecification.TryParse(
            "16:-25",
            out OffsetSpecification? offsets,
            out _);
        CliRequest request = EmptyRequest() with
        {
            OffsetSpecification = offsets,
        };

        int exitCode = runner.Execute(request);

        Assert.Equal((int)ExitCode.Success, exitCode);
        (CoreAddress core, int offset) = Assert.Single(controller.OffsetWrites);
        Assert.Equal(2, core.CcdIndex);
        Assert.Equal(0, core.CoreIndex);
        Assert.Equal(-25, offset);
        Assert.Equal(25, controller.OffsetReadCount);
        Assert.Contains("physical core 16", output.ToString());
    }

    [Fact]
    public void FailedOffsetWriteIsNotReportedAsSuccess()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            SetOffsetResult = OperationResult.Fail("SMU rejected write"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);
        OffsetSpecification.TryParse(
            "0:-10",
            out OffsetSpecification? offsets,
            out _);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.DoesNotContain("Set enabled core", output.ToString());
        Assert.Contains("SMU rejected write", error.ToString());
    }

    [Fact]
    public void OffsetWriteMustMatchImmediateHardwareReadBack()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            OffsetReadBackResult = OperationResult<int>.Ok(-9),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);
        OffsetSpecification.TryParse(
            "0:-10",
            out OffsetSpecification? offsets,
            out _);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.DoesNotContain("verified", output.ToString());
        Assert.Contains("read-back mismatch", error.ToString());
        Assert.Contains("requested -10, read -9", error.ToString());
    }

    [Fact]
    public void OffsetWriteWithFailedReadBackIsNotReportedAsSuccess()
    {
        FakeRyzenController controller = new(2, Enumerable.Range(0, 2))
        {
            OffsetReadBackResult =
                OperationResult<int>.Fail("read-back command failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);
        OffsetSpecification.TryParse(
            "1:-12",
            out OffsetSpecification? offsets,
            out _);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("read-back failed", error.ToString());
        Assert.Contains("read-back command failed", error.ToString());
    }

    [Fact]
    public void KeyedOffsetWritesRunInCompactKeyOrderAndAreVerified()
    {
        FakeRyzenController controller = new(3, Enumerable.Range(0, 3));
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);
        OffsetSpecification.TryParse(
            "2:-12,0:-10,1:-11",
            out OffsetSpecification? offsets,
            out _);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal([0, 1, 2], controller.OffsetWrites
            .Select(write => write.Core.PhysicalCoreIndex));
        Assert.Equal([-10, -11, -12], controller.OffsetWrites
            .Select(write => write.Offset));
        Assert.Equal(6, controller.OffsetReadCount);
        Assert.Equal(3, output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("(verified)", output.ToString());
    }

    [Fact]
    public void CombinedOffsetAndTerseReadUsesVerifiedPostWriteCache()
    {
        FakeRyzenController controller = new(2, Enumerable.Range(0, 2));
        controller.Offsets[0] = -1;
        controller.Offsets[1] = -2;
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);
        OffsetSpecification.TryParse(
            "1:-12",
            out OffsetSpecification? offsets,
            out _);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
            GetOffsetsTerse = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(3, controller.OffsetReadCount);
        Assert.EndsWith(
            $"-1,-12{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void CompactOffsetOperationsIgnoreUnqualifiedFuseTopology()
    {
        FakeRyzenController controller = new(4, Enumerable.Range(0, 4))
        {
            HasUsableCoreTopology = false,
            CoreTopologyUnavailableReason =
                "ZenStates-Core did not return a complete physical-core fuse map.",
        };
        controller.Offsets[0] = -1;
        controller.Offsets[1] = -2;
        controller.Offsets[2] = -3;
        controller.Offsets[3] = -4;
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetOffsetsTerse = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(
            $"-1,-2,-3,-4{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void CompactZeroCanTargetSurvivingSecondCcdSelector()
    {
        CoreAddress[] survivingCcd = Enumerable
            .Range(8, 8)
            .Select(CoreAddress.FromPhysicalCoreIndex)
            .ToArray();
        FakeRyzenController controller = new(
            physicalCoreSlots: 8,
            enabledPhysicalCores: Enumerable.Range(8, 8))
        {
            PboOffsetCandidates = survivingCcd,
            HasUsableCoreTopology = false,
        };
        OffsetSpecification.TryParse(
            "0:-15",
            out OffsetSpecification? offsets,
            out _);
        CommandRunner runner = CreateRunner(controller);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            OffsetSpecification = offsets,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        (CoreAddress core, int offset) = Assert.Single(controller.OffsetWrites);
        Assert.Equal(8, core.PhysicalCoreIndex);
        Assert.Equal(-15, offset);
    }

    [Fact]
    public void InvalidPhysicalCorePreventsDowncoreWrite()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        CoreSelection.TryParse("8", out CoreSelection? selection, out _);
        CommandRunner runner = CreateRunner(controller);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            DisabledCores = selection,
        });

        Assert.Equal((int)ExitCode.CoreOutOfRange, exitCode);
        Assert.Equal(0, controller.DowncoreWriteCount);
    }

    [Fact]
    public void DisablingEveryPhysicalCoreIsRefused()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        CoreSelection.TryParse(
            "0,1,2,3,4,5,6,7",
            out CoreSelection? selection,
            out _);
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, error: error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            DisabledCores = selection,
        });

        Assert.Equal((int)ExitCode.InvalidInput, exitCode);
        Assert.Equal(0, controller.DowncoreWriteCount);
        Assert.Contains("Refusing", error.ToString());
    }

    [Fact]
    public void DowncoreSuccessPrintsEveryCcdAndRebootNotice()
    {
        FakeRyzenController controller = new(
            24,
            Enumerable.Range(0, 24))
        {
            DowncoreResult = DowncoreOperationResult.Ok(
                [0b0000_0001, 0b0000_0010, 0b1000_0000]),
        };
        CoreSelection.TryParse(
            "0,9,23",
            out CoreSelection? selection,
            out _);
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            DisabledCores = selection,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal([0, 9, 23], controller.LastDisabledCores!.Order());
        Assert.Contains("CCD2", output.ToString());
        Assert.Contains("reboot is required", output.ToString());
    }

    [Fact]
    public void IncompleteDowncoreResultIsNotReportedAsSuccess()
    {
        FakeRyzenController controller = new(
            16,
            Enumerable.Range(0, 16))
        {
            DowncoreResult = DowncoreOperationResult.Ok([0]),
        };
        CoreSelection.TryParse("0", out CoreSelection? selection, out _);
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            DisabledCores = selection,
        });

        Assert.Equal((int)ExitCode.DowncoreUnavailable, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("incomplete", error.ToString());
    }

    [Fact]
    public void TerseOffsetsContainOnlyOneCsvLine()
    {
        FakeRyzenController controller = new(
            4,
            Enumerable.Range(0, 4));
        controller.Offsets[0] = -10;
        controller.Offsets[1] = 0;
        controller.Offsets[2] = 10;
        controller.Offsets[3] = -20;
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetOffsetsTerse = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(
            $"-10,0,10,-20{Environment.NewLine}",
            output.ToString());
        Assert.Equal(4, controller.OffsetReadCount);
    }

    [Fact]
    public void ScalarFailureReturnsNonZeroAndDoesNotClaimSuccess()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            SetScalarResult = OperationResult.Fail("scalar rejected"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            PboScalar = 5,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("scalar rejected", error.ToString());
    }

    [Fact]
    public void FMaxWriteFailureReturnsNonZeroAndDoesNotClaimSuccess()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            SetFMaxResult = OperationResult.Fail("FMax rejected"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            FMax = new FMaxFrequency(5250),
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal([5250u], controller.FMaxWrites);
        Assert.DoesNotContain("Set FMax", output.ToString());
        Assert.Contains("FMax rejected", error.ToString());
    }

    [Fact]
    public void UnsupportedFMaxReadIsRejectedBeforeCallingTheController()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CanReadFMax = false,
        };
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, error: error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetFMax = true,
        });

        Assert.Equal((int)ExitCode.UnsupportedOperation, exitCode);
        Assert.Equal(0, controller.FMaxReadCount);
        Assert.Contains("required to read FMax", error.ToString());
    }

    [Fact]
    public void UnsupportedFMaxWriteIsRejectedBeforeCallingTheController()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CanWriteFMax = false,
        };
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, error: error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            FMax = new FMaxFrequency(5250),
        });

        Assert.Equal((int)ExitCode.UnsupportedOperation, exitCode);
        Assert.Empty(controller.FMaxWrites);
        Assert.Contains("required to set FMax", error.ToString());
    }

    [Fact]
    public void VcoreReadUsesInvariantFixedPrecisionOutput()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Ok(1.225),
        };
        StringWriter output = new();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CommandRunner runner = CreateRunner(controller, output);

            int exitCode = runner.Execute(EmptyRequest() with
            {
                GetVcore = true,
            });

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
    public void PackageVcoreReadDoesNotRequireQualifiedPerCoreTopology()
    {
        FakeRyzenController controller = new(16, Enumerable.Range(0, 12))
        {
            HasUsableCoreTopology = false,
            CoreTopologyUnavailableReason =
                "Per-core selectors are not qualified for this topology.",
            GetVcoreResult = OperationResult<double>.Ok(1.1875),
        };
        StringWriter output = new();
        CommandRunner runner = CreateRunner(controller, output);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetVcore = true,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Contains("1.187500 V", output.ToString());
    }

    [Fact]
    public void UnsupportedVcoreReadReturnsCapabilityExitCodeWithoutStdout()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CanReadVcore = false,
            VcoreReadUnavailableReason =
                "Vcore telemetry is not mapped for PM table 0x00621102.",
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetVcore = true,
        });

        Assert.Equal((int)ExitCode.UnsupportedOperation, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, controller.VcoreReadCount);
        Assert.Contains("0x00621102", error.ToString());
    }

    [Fact]
    public void FailedVcoreReadReturnsOperationExitCodeWithoutStdout()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult =
                OperationResult<double>.Fail("PM-table Vcore read failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetVcore = true,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("PM-table Vcore read failed", error.ToString());
    }

    [Fact]
    public void VcoreStreamUsesStableTabSeparatedProtocolAndOneController()
    {
        using CancellationTokenSource cancellationSource = new();
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            HasUsableCoreTopology = false,
            CoreTopologyUnavailableReason =
                "Per-core selectors are not qualified for this topology.",
            GetVcoreHandler = () =>
            {
                cancellationSource.Cancel();
                return OperationResult<double>.Ok(1.1875);
            },
        };
        StringWriter output = new();
        CommandRunner runner = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token);

        int exitCode = runner.Execute(EmptyRequest() with
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
    public void VcoreStreamFailureKeepsDiagnosticsOutOfStdout()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult =
                OperationResult<double>.Fail("telemetry refresh failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            VcoreStreamIntervalMilliseconds = 150,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("telemetry refresh failed", error.ToString());
    }

    [Fact]
    public void ClosedVcoreStreamPipeDisposesHardwareSession()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter error = new();
        CommandRunner runner = new(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            new ThrowingTextWriter(),
            error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            VcoreStreamIntervalMilliseconds = 150,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.True(controller.Disposed);
        Assert.Contains("output pipe was closed", error.ToString());
    }

    [Fact]
    public void UnsupportedCpuTemperatureReturnsCapabilityExitCodeWithoutStdout()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CanReadCpuTemperature = false,
            CpuTemperatureReadUnavailableReason =
                "CPU temperature telemetry is not mapped for family 0x1B.",
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetCpuTemperature = true,
        });

        Assert.Equal((int)ExitCode.UnsupportedOperation, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, controller.CpuTemperatureReadCount);
        Assert.Contains("0x1B", error.ToString());
    }

    [Fact]
    public void FailedCpuTemperatureReadReturnsOperationExitCodeWithoutStdout()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetCpuTemperatureResult =
                OperationResult<double>.Fail("thermal register read failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            GetCpuTemperature = true,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(1, controller.CpuTemperatureReadCount);
        Assert.Contains("thermal register read failed", error.ToString());
    }

    [Fact]
    public void TelemetryStreamUsesVersionedJsonLinesAndOneController()
    {
        using CancellationTokenSource cancellationSource = new();
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult = OperationResult<double>.Ok(1.1875),
            GetCpuTemperatureHandler = () =>
            {
                cancellationSource.Cancel();
                return OperationResult<double>.Ok(71.875);
            },
        };
        StringWriter output = new();
        CommandRunner runner = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(1, controller.CpuTemperatureReadCount);
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
    public void TelemetryStreamSequencesJsonLinesWithIncreasingElapsedTime()
    {
        using CancellationTokenSource cancellationSource = new();
        int temperatureReads = 0;
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetCpuTemperatureHandler = () =>
            {
                temperatureReads++;
                if (temperatureReads == 2)
                {
                    cancellationSource.Cancel();
                }

                return OperationResult<double>.Ok(70.0 + temperatureReads);
            },
        };
        StringWriter output = new();
        CommandRunner runner = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 100,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        string[] lines = output
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal(0, first.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(1, second.RootElement.GetProperty("sequence").GetInt64());
        Assert.True(
            second.RootElement.GetProperty("elapsedMilliseconds").GetInt64() >
            first.RootElement.GetProperty("elapsedMilliseconds").GetInt64());
    }

    [Fact]
    public void PreCancelledTelemetryStreamIsSuccessfulAndReadsNothing()
    {
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter output = new();
        CommandRunner runner = CreateRunnerWithCancellation(
            controller,
            output,
            cancellationSource.Token);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, controller.VcoreReadCount);
        Assert.Equal(0, controller.CpuTemperatureReadCount);
    }

    [Fact]
    public void UnsupportedTelemetryTemperatureFailsBeforeEitherRead()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            CanReadCpuTemperature = false,
            CpuTemperatureReadUnavailableReason =
                "CPU temperature telemetry is not mapped for family 0x1B.",
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.UnsupportedOperation, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, controller.VcoreReadCount);
        Assert.Equal(0, controller.CpuTemperatureReadCount);
        Assert.Contains("0x1B", error.ToString());
    }

    [Fact]
    public void TelemetryStreamVcoreFailureEmitsNoPartialSample()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetVcoreResult =
                OperationResult<double>.Fail("Vcore telemetry refresh failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(0, controller.CpuTemperatureReadCount);
        Assert.Contains("Vcore telemetry refresh failed", error.ToString());
    }

    [Fact]
    public void TelemetryStreamTemperatureFailureEmitsNoPartialSample()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetCpuTemperatureResult =
                OperationResult<double>.Fail("temperature telemetry refresh failed"),
        };
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(1, controller.CpuTemperatureReadCount);
        Assert.Contains("temperature telemetry refresh failed", error.ToString());
    }

    [Theory]
    [InlineData(99)]
    [InlineData(60_001)]
    public void TelemetryStreamRejectsOutOfRangeIntervalsBeforeReadingHardware(
        int intervalMilliseconds)
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter output = new();
        StringWriter error = new();
        CommandRunner runner = CreateRunner(controller, output, error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = intervalMilliseconds,
        });

        Assert.Equal((int)ExitCode.InvalidInput, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, controller.VcoreReadCount);
        Assert.Equal(0, controller.CpuTemperatureReadCount);
    }

    [Fact]
    public void ClosedTelemetryStreamPipeDisposesHardwareSession()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8));
        StringWriter error = new();
        CommandRunner runner = new(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            new ThrowingTextWriter(),
            error);

        int exitCode = runner.Execute(EmptyRequest() with
        {
            TelemetryStreamIntervalMilliseconds = 500,
        });

        Assert.Equal((int)ExitCode.OperationFailed, exitCode);
        Assert.Equal(1, controller.VcoreReadCount);
        Assert.Equal(1, controller.CpuTemperatureReadCount);
        Assert.True(controller.Disposed);
        Assert.Contains("output pipe was closed", error.ToString());
    }

    [Fact]
    public void ControllerIsDisposedWhenAnOperationFails()
    {
        FakeRyzenController controller = new(8, Enumerable.Range(0, 8))
        {
            GetScalarResult = OperationResult<float>.Fail("read failed"),
        };
        CommandRunner runner = CreateRunner(controller);

        runner.Execute(EmptyRequest() with { GetPboScalar = true });

        Assert.True(controller.Disposed);
    }

    private static CommandRunner CreateRunner(
        FakeRyzenController controller,
        StringWriter? output = null,
        StringWriter? error = null)
    {
        return new CommandRunner(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output ?? new StringWriter(),
            error ?? new StringWriter());
    }

    private static CommandRunner CreateRunnerWithCancellation(
        FakeRyzenController controller,
        StringWriter output,
        CancellationToken cancellationToken)
    {
        return new CommandRunner(
            () => controller,
            FakePrivilegeChecker.Administrator(),
            output,
            new StringWriter(),
            cancellationToken);
    }

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
}
