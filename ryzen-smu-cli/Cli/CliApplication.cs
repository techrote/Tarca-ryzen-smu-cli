using System.CommandLine;
using System.Globalization;

namespace ryzen_smu_cli;

internal static class CliApplication
{
    public static int Run(
        string[] args,
        Func<IRyzenController> controllerFactory,
        IPrivilegeChecker privilegeChecker,
        TextWriter output,
        TextWriter error) =>
        RunWithCancellation(
            args,
            controllerFactory,
            privilegeChecker,
            output,
            error,
            CancellationToken.None);

    public static int RunWithCancellation(
        string[] args,
        Func<IRyzenController> controllerFactory,
        IPrivilegeChecker privilegeChecker,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(controllerFactory);
        ArgumentNullException.ThrowIfNull(privilegeChecker);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        RootCommand rootCommand = CreateRootCommand(
            controllerFactory,
            privilegeChecker,
            output,
            error,
            cancellationToken);
        string[] effectiveArgs = args.Length == 0 ? ["--help"] : args;

        InvocationConfiguration invocationConfiguration = new()
        {
            Output = output,
            Error = error,
        };

        return rootCommand.Parse(effectiveArgs).Invoke(invocationConfiguration);
    }

    internal static RootCommand CreateRootCommand(
        Func<IRyzenController> controllerFactory,
        IPrivilegeChecker privilegeChecker,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        Option<string?> offsetOption = new("--offset")
        {
            Description =
                "Set Curve Optimizer offsets. Use a comma-separated positional list such as " +
                "-10,5,-20 or compact-core assignments such as 0:-10,1:5,2:-20. " +
                $"Offsets must be between {OffsetSpecification.MinimumOffset} and " +
                $"{OffsetSpecification.MaximumOffset}.",
        };
        offsetOption.Validators.Add(result =>
        {
            string? value = result.GetValue(offsetOption);
            if (value is not null &&
                !OffsetSpecification.TryParse(value, out _, out string? validationError))
            {
                result.AddError(validationError!);
            }
        });

        Option<string?> disableCoresOption = new("--disable-cores")
        {
            Description =
                "Set the complete list of physical core slots to disable, for example 0,1,4,7. " +
                "Unspecified slots are enabled. The change requires a reboot.",
        };
        disableCoresOption.Validators.Add(result =>
        {
            string? value = result.GetValue(disableCoresOption);
            if (value is not null &&
                !CoreSelection.TryParse(value, out _, out string? validationError))
            {
                result.AddError(validationError!);
            }
        });

        Option<bool> enableAllCoresOption = new("--enable-all-cores")
        {
            Description = "Enable every physical core slot. The change requires a reboot.",
        };
        Option<bool> getOffsetsTerseOption = new("--get-offsets-terse")
        {
            Description =
                "Print Curve Optimizer offsets for the compact operable-core map " +
                "as one comma-separated line.",
        };
        Option<bool> getPhysicalCoresOption = new("--get-physical-cores")
        {
            Description = "Print the factory-fused status of every physical core slot.",
        };
        Option<bool> getEnabledCoresOption = new("--get-enabled-cores")
        {
            Description =
                "Print the compact operable-core to SMU-selector-slot mapping.",
        };
        Option<string?> setPboScalarOption = new("--set-pbo-scalar")
        {
            Description = "Set the PBO scalar to a whole number from 1 through 10.",
        };
        setPboScalarOption.Validators.Add(result =>
        {
            string? rawValue = result.GetValue(setPboScalarOption);
            if (rawValue is null)
            {
                return;
            }

            if (!int.TryParse(
                    rawValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value) ||
                value is < 1 or > 10)
            {
                result.AddError("PBO scalar must be a whole number from 1 through 10.");
            }
        });

        Option<bool> getPboScalarOption = new("--get-pbo-scalar")
        {
            Description = "Print the current PBO scalar.",
        };
        Option<string?> setFMaxOption = new("--set-fmax")
        {
            Description =
                $"Set the maximum boost-frequency limit in MHz. The value must use " +
                $"{FMaxFrequency.StepMegahertz} MHz steps, for example 5225 or 5250.",
        };
        setFMaxOption.Validators.Add(result =>
        {
            string? value = result.GetValue(setFMaxOption);
            if (value is not null &&
                !FMaxFrequency.TryParse(value, out _, out string? validationError))
            {
                result.AddError(validationError!);
            }
        });
        Option<bool> getFMaxOption = new("--get-fmax")
        {
            Description = "Print the current maximum boost-frequency limit in MHz.",
        };
        Option<bool> getVcoreOption = new("--get-vcore")
        {
            Description =
                "Print one mapped CPU-core rail telemetry sample in volts.",
        };
        Option<bool> streamVcoreOption = new("--stream-vcore")
        {
            Description =
                "Continuously print mapped CPU-core rail telemetry without " +
                "reinitializing hardware between samples.",
        };
        Option<bool> getCpuTemperatureOption = new("--get-cpu-temperature")
        {
            Description =
                "Print one mapped CPU Tctl/Tdie temperature sample in degrees Celsius.",
        };
        Option<bool> streamTelemetryOption = new("--stream-telemetry")
        {
            Description =
                "Continuously print mapped Vcore and CPU Tctl/Tdie samples as JSON Lines.",
        };
        Option<bool> streamSystemTelemetryOption =
            new("--stream-system-telemetry")
            {
                Description =
                    "Continuously print atomic Vcore, CPU Tctl/Tdie, AMD RAPL " +
                    "package power, and per-target APERF/MPERF telemetry as " +
                    "schema-2 JSON Lines.",
            };
        Option<string?> systemTelemetryTargetsOption = new("--targets")
        {
            Description =
                "Set the complete Windows logical-processor targets for " +
                "--stream-system-telemetry as group:processor pairs, for " +
                "example 0:0,0:1,0:2,0:3.",
        };
        systemTelemetryTargetsOption.Validators.Add(result =>
        {
            string? value = result.GetValue(systemTelemetryTargetsOption);
            if (value is not null &&
                !SystemTelemetryTargetSet.TryParse(
                    value,
                    out _,
                    out string? validationError))
            {
                result.AddError(validationError!);
            }
        });
        Option<bool> diagnoseVcoreOption = new("--diagnose-vcore")
        {
            Description =
                "Capture raw Zen 4/5 SVI register candidates and decoded VID " +
                "fields as one JSON hardware report.",
        };
        Option<string?> samplesOption = new("--samples")
        {
            Description =
                $"Set the --diagnose-vcore sample count " +
                $"({VcoreDiagnostics.MinimumSampleCount} through " +
                $"{VcoreDiagnostics.MaximumSampleCount}; default " +
                $"{VcoreDiagnostics.DefaultSampleCount}).",
        };
        samplesOption.Validators.Add(result =>
        {
            string? rawValue = result.GetValue(samplesOption);
            if (rawValue is null)
            {
                return;
            }

            if (!int.TryParse(
                    rawValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value) ||
                !VcoreDiagnostics.IsValidSampleCount(value))
            {
                result.AddError(
                    $"Vcore diagnostic sample count must be a whole number from " +
                    $"{VcoreDiagnostics.MinimumSampleCount} through " +
                    $"{VcoreDiagnostics.MaximumSampleCount}.");
            }
        });
        Option<string?> intervalMillisecondsOption = new("--interval-ms")
        {
            Description =
                "Set a streaming or diagnostic sampling interval in milliseconds. " +
                $"--stream-vcore and --diagnose-vcore allow " +
                $"{VcoreStreaming.MinimumIntervalMilliseconds} through " +
                $"{VcoreStreaming.MaximumIntervalMilliseconds} (default " +
                $"{VcoreStreaming.DefaultIntervalMilliseconds}); --stream-telemetry " +
                $"allows {CpuTelemetryStreaming.MinimumIntervalMilliseconds} through " +
                $"{CpuTelemetryStreaming.MaximumIntervalMilliseconds} (default " +
                $"{CpuTelemetryStreaming.DefaultIntervalMilliseconds}); " +
                $"--stream-system-telemetry allows " +
                $"{SystemTelemetryStreaming.MinimumIntervalMilliseconds} through " +
                $"{SystemTelemetryStreaming.MaximumIntervalMilliseconds} (default " +
                $"{SystemTelemetryStreaming.DefaultIntervalMilliseconds}).",
        };
        Option<bool> infoOption = new("--info")
        {
            Description =
                "Print CPU, motherboard, firmware, SMU, and PM-table information.",
        };

        RootCommand rootCommand = new("A CLI for the Ryzen System Management Unit (SMU).")
        {
            Options =
            {
                offsetOption,
                disableCoresOption,
                enableAllCoresOption,
                getOffsetsTerseOption,
                getPhysicalCoresOption,
                getEnabledCoresOption,
                setPboScalarOption,
                getPboScalarOption,
                setFMaxOption,
                getFMaxOption,
                getVcoreOption,
                streamVcoreOption,
                getCpuTemperatureOption,
                streamTelemetryOption,
                streamSystemTelemetryOption,
                systemTelemetryTargetsOption,
                diagnoseVcoreOption,
                samplesOption,
                intervalMillisecondsOption,
                infoOption,
            },
        };

        rootCommand.Validators.Add(result =>
        {
            bool WasSpecified(Option option)
            {
                var optionResult = result.GetResult(option);
                return optionResult is not null && !optionResult.Implicit;
            }

            // Cross-option validation needs only presence. Avoid typed
            // GetValue calls here because a missing or repeated argument is
            // already a parser error and GetValue would throw before that
            // canonical error can be returned to the user.
            if (WasSpecified(disableCoresOption) &&
                WasSpecified(enableAllCoresOption))
            {
                result.AddError(
                    "--disable-cores and --enable-all-cores cannot be used together.");
            }

            bool streamVcore = WasSpecified(streamVcoreOption);
            bool streamTelemetry = WasSpecified(streamTelemetryOption);
            bool streamSystemTelemetry =
                WasSpecified(streamSystemTelemetryOption);
            bool diagnoseVcore = WasSpecified(diagnoseVcoreOption);
            var intervalResult = result.GetResult(intervalMillisecondsOption);
            if (intervalResult is { Implicit: false } &&
                intervalResult.Tokens.Count == 1)
            {
                bool parsedInterval = int.TryParse(
                    intervalResult.Tokens[0].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int interval);
                if (streamSystemTelemetry &&
                    (!parsedInterval ||
                     !SystemTelemetryStreaming.IsValidInterval(interval)))
                {
                    result.AddError(
                        $"System telemetry stream interval must be a whole " +
                        $"number from " +
                        $"{SystemTelemetryStreaming.MinimumIntervalMilliseconds} " +
                        $"through " +
                        $"{SystemTelemetryStreaming.MaximumIntervalMilliseconds} " +
                        "milliseconds.");
                }
                else if (streamTelemetry &&
                    (!parsedInterval ||
                     !CpuTelemetryStreaming.IsValidInterval(interval)))
                {
                    result.AddError(
                        $"Telemetry stream interval must be a whole number from " +
                        $"{CpuTelemetryStreaming.MinimumIntervalMilliseconds} through " +
                        $"{CpuTelemetryStreaming.MaximumIntervalMilliseconds} " +
                        "milliseconds.");
                }
                else if (!streamTelemetry &&
                         !streamSystemTelemetry &&
                         (!parsedInterval ||
                          !VcoreStreaming.IsValidInterval(interval)))
                {
                    // Keep the v0.3.5 error contract for the existing Vcore
                    // stream and diagnostic modes.
                    result.AddError(
                        $"Vcore sample interval must be a whole number from " +
                        $"{VcoreStreaming.MinimumIntervalMilliseconds} through " +
                        $"{VcoreStreaming.MaximumIntervalMilliseconds} milliseconds.");
                }
            }
            if (WasSpecified(intervalMillisecondsOption) &&
                !streamVcore &&
                !streamTelemetry &&
                !streamSystemTelemetry &&
                !diagnoseVcore)
            {
                result.AddError(
                    "--interval-ms requires --stream-vcore, --stream-telemetry, " +
                    "--stream-system-telemetry, or --diagnose-vcore.");
            }

            if (streamSystemTelemetry &&
                !WasSpecified(systemTelemetryTargetsOption))
            {
                result.AddError(
                    "--stream-system-telemetry requires --targets.");
            }

            if (WasSpecified(systemTelemetryTargetsOption) &&
                !streamSystemTelemetry)
            {
                result.AddError(
                    "--targets requires --stream-system-telemetry.");
            }

            if (WasSpecified(samplesOption) && !diagnoseVcore)
            {
                result.AddError("--samples requires --diagnose-vcore.");
            }

            if (streamVcore &&
                (WasSpecified(getVcoreOption) ||
                 diagnoseVcore))
            {
                result.AddError(
                    "--stream-vcore cannot be combined with --get-vcore or " +
                    "--diagnose-vcore.");
            }

            if (streamVcore && streamTelemetry)
            {
                result.AddError(
                    "--stream-vcore and --stream-telemetry cannot be used together.");
            }

            if (streamSystemTelemetry && (streamVcore || streamTelemetry))
            {
                result.AddError(
                    "--stream-system-telemetry cannot be combined with another " +
                    "streaming operation.");
            }

            if (streamVcore &&
                (WasSpecified(offsetOption) ||
                 WasSpecified(disableCoresOption) ||
                 WasSpecified(enableAllCoresOption) ||
                 WasSpecified(getOffsetsTerseOption) ||
                 WasSpecified(getPhysicalCoresOption) ||
                 WasSpecified(getEnabledCoresOption) ||
                 WasSpecified(setPboScalarOption) ||
                 WasSpecified(getPboScalarOption) ||
                 WasSpecified(setFMaxOption) ||
                 WasSpecified(getFMaxOption) ||
                 WasSpecified(getCpuTemperatureOption) ||
                 streamSystemTelemetry ||
                 WasSpecified(infoOption)))
            {
                result.AddError(
                    "--stream-vcore cannot be combined with another hardware operation.");
            }

            if (streamTelemetry &&
                (WasSpecified(offsetOption) ||
                 WasSpecified(disableCoresOption) ||
                 WasSpecified(enableAllCoresOption) ||
                 WasSpecified(getOffsetsTerseOption) ||
                 WasSpecified(getPhysicalCoresOption) ||
                 WasSpecified(getEnabledCoresOption) ||
                 WasSpecified(setPboScalarOption) ||
                 WasSpecified(getPboScalarOption) ||
                 WasSpecified(setFMaxOption) ||
                 WasSpecified(getFMaxOption) ||
                 WasSpecified(getVcoreOption) ||
                 WasSpecified(getCpuTemperatureOption) ||
                 diagnoseVcore ||
                 streamSystemTelemetry ||
                 WasSpecified(infoOption)))
            {
                result.AddError(
                    "--stream-telemetry cannot be combined with another hardware operation.");
            }

            if (diagnoseVcore &&
                (WasSpecified(offsetOption) ||
                 WasSpecified(disableCoresOption) ||
                 WasSpecified(enableAllCoresOption) ||
                 WasSpecified(getOffsetsTerseOption) ||
                 WasSpecified(getPhysicalCoresOption) ||
                 WasSpecified(getEnabledCoresOption) ||
                 WasSpecified(setPboScalarOption) ||
                 WasSpecified(getPboScalarOption) ||
                 WasSpecified(setFMaxOption) ||
                 WasSpecified(getFMaxOption) ||
                 WasSpecified(getVcoreOption) ||
                 WasSpecified(getCpuTemperatureOption) ||
                 streamTelemetry ||
                 streamSystemTelemetry ||
                 WasSpecified(infoOption)))
            {
                result.AddError(
                    "--diagnose-vcore cannot be combined with another hardware operation.");
            }

            if (streamSystemTelemetry &&
                (WasSpecified(offsetOption) ||
                 WasSpecified(disableCoresOption) ||
                 WasSpecified(enableAllCoresOption) ||
                 WasSpecified(getOffsetsTerseOption) ||
                 WasSpecified(getPhysicalCoresOption) ||
                 WasSpecified(getEnabledCoresOption) ||
                 WasSpecified(setPboScalarOption) ||
                 WasSpecified(getPboScalarOption) ||
                 WasSpecified(setFMaxOption) ||
                 WasSpecified(getFMaxOption) ||
                 WasSpecified(getVcoreOption) ||
                 WasSpecified(getCpuTemperatureOption) ||
                 diagnoseVcore ||
                 WasSpecified(infoOption)))
            {
                result.AddError(
                    "--stream-system-telemetry cannot be combined with another " +
                    "hardware operation.");
            }
        });

        rootCommand.SetAction(parseResult =>
        {
            OffsetSpecification? offsetSpecification = null;
            string? rawOffsets = parseResult.GetValue(offsetOption);
            if (rawOffsets is not null)
            {
                OffsetSpecification.TryParse(rawOffsets, out offsetSpecification, out _);
            }

            CoreSelection? disabledCores = null;
            string? rawDisabledCores = parseResult.GetValue(disableCoresOption);
            if (rawDisabledCores is not null)
            {
                CoreSelection.TryParse(rawDisabledCores, out disabledCores, out _);
            }

            FMaxFrequency? fMax = null;
            string? rawFMax = parseResult.GetValue(setFMaxOption);
            if (rawFMax is not null)
            {
                FMaxFrequency.TryParse(rawFMax, out FMaxFrequency parsedFMax, out _);
                fMax = parsedFMax;
            }

            int? vcoreStreamIntervalMilliseconds = null;
            if (parseResult.GetValue(streamVcoreOption))
            {
                vcoreStreamIntervalMilliseconds =
                    int.TryParse(
                        parseResult.GetValue(intervalMillisecondsOption),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int interval)
                        ? interval
                        : VcoreStreaming.DefaultIntervalMilliseconds;
            }

            int? telemetryStreamIntervalMilliseconds = null;
            if (parseResult.GetValue(streamTelemetryOption))
            {
                telemetryStreamIntervalMilliseconds =
                    int.TryParse(
                        parseResult.GetValue(intervalMillisecondsOption),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int interval)
                        ? interval
                        : CpuTelemetryStreaming.DefaultIntervalMilliseconds;
            }

            int? systemTelemetryStreamIntervalMilliseconds = null;
            SystemTelemetryTargetSet? systemTelemetryTargets = null;
            if (parseResult.GetValue(streamSystemTelemetryOption))
            {
                systemTelemetryStreamIntervalMilliseconds =
                    int.TryParse(
                        parseResult.GetValue(intervalMillisecondsOption),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int interval)
                        ? interval
                        : SystemTelemetryStreaming.DefaultIntervalMilliseconds;
                SystemTelemetryTargetSet.TryParse(
                    parseResult.GetValue(systemTelemetryTargetsOption),
                    out systemTelemetryTargets,
                    out _);
            }

            CliRequest request = new(
                offsetSpecification,
                disabledCores,
                parseResult.GetValue(enableAllCoresOption),
                parseResult.GetValue(getOffsetsTerseOption),
                parseResult.GetValue(getPhysicalCoresOption),
                parseResult.GetValue(getEnabledCoresOption),
                int.TryParse(
                    parseResult.GetValue(setPboScalarOption),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int scalar)
                    ? scalar
                    : null,
                parseResult.GetValue(getPboScalarOption),
                fMax,
                parseResult.GetValue(getFMaxOption),
                parseResult.GetValue(getVcoreOption),
                vcoreStreamIntervalMilliseconds,
                parseResult.GetValue(infoOption))
            {
                DiagnoseVcore = parseResult.GetValue(diagnoseVcoreOption),
                GetCpuTemperature =
                    parseResult.GetValue(getCpuTemperatureOption),
                TelemetryStreamIntervalMilliseconds =
                    telemetryStreamIntervalMilliseconds,
                SystemTelemetryStreamIntervalMilliseconds =
                    systemTelemetryStreamIntervalMilliseconds,
                SystemTelemetryTargets = systemTelemetryTargets,
                VcoreDiagnosticSampleCount = int.TryParse(
                    parseResult.GetValue(samplesOption),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int diagnosticSampleCount)
                    ? diagnosticSampleCount
                    : VcoreDiagnostics.DefaultSampleCount,
                VcoreDiagnosticIntervalMilliseconds = int.TryParse(
                    parseResult.GetValue(intervalMillisecondsOption),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int diagnosticInterval)
                    ? diagnosticInterval
                    : VcoreStreaming.DefaultIntervalMilliseconds,
            };

            if (!request.HasOperation)
            {
                error.WriteLine("No operation was specified. Use --help to list the options.");
                return (int)ExitCode.InvalidInput;
            }

            CommandRunner runner = new(
                controllerFactory,
                privilegeChecker,
                output,
                error,
                cancellationToken);
            return runner.Execute(request);
        });

        return rootCommand;
    }
}
