using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace ryzen_smu_cli;

internal readonly record struct SystemTelemetryTarget(
    ushort Group,
    byte Processor) : IComparable<SystemTelemetryTarget>
{
    public int CompareTo(SystemTelemetryTarget other)
    {
        int groupComparison = Group.CompareTo(other.Group);
        return groupComparison != 0
            ? groupComparison
            : Processor.CompareTo(other.Processor);
    }

    public override string ToString() =>
        $"{Group.ToString(CultureInfo.InvariantCulture)}:" +
        Processor.ToString(CultureInfo.InvariantCulture);
}

internal sealed class SystemTelemetryTargetSet
{
    public const int MaximumTargetCount = 256;

    private SystemTelemetryTargetSet(
        IReadOnlyList<SystemTelemetryTarget> targets)
    {
        Targets = targets;
    }

    public IReadOnlyList<SystemTelemetryTarget> Targets { get; }

    public static bool IsCanonical(
        IReadOnlyList<SystemTelemetryTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count is < 1 or > MaximumTargetCount)
        {
            return false;
        }

        for (int index = 0; index < targets.Count; index++)
        {
            if (targets[index].Processor > 63 ||
                (index > 0 &&
                 targets[index - 1].CompareTo(targets[index]) >= 0))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParse(
        string? value,
        out SystemTelemetryTargetSet? targetSet,
        out string? error)
    {
        targetSet = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error =
                "System telemetry targets must contain at least one " +
                "group:processor pair.";
            return false;
        }

        string[] entries = value.Split(',', StringSplitOptions.None);
        if (entries.Length > MaximumTargetCount)
        {
            error =
                $"System telemetry supports at most {MaximumTargetCount} targets.";
            return false;
        }

        HashSet<SystemTelemetryTarget> uniqueTargets = [];
        foreach (string entry in entries)
        {
            string[] parts = entry.Split(':', StringSplitOptions.None);
            if (parts.Length != 2 ||
                !ushort.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ushort group) ||
                !byte.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte processor) ||
                processor > 63)
            {
                error =
                    $"Invalid system telemetry target '{entry}'. Use " +
                    "group:processor with group 0 through 65535 and " +
                    "processor 0 through 63.";
                return false;
            }

            SystemTelemetryTarget target = new(group, processor);
            if (!uniqueTargets.Add(target))
            {
                error = $"Duplicate system telemetry target '{target}'.";
                return false;
            }
        }

        targetSet = new SystemTelemetryTargetSet(
            uniqueTargets.OrderBy(target => target).ToArray());
        return true;
    }
}

internal static class SystemTelemetryStreaming
{
    public const int DefaultIntervalMilliseconds = 500;
    public const int MinimumIntervalMilliseconds = 500;
    public const int MaximumIntervalMilliseconds = 60_000;
    public const double MaximumEffectiveMegahertz = 10_000.0;
    public const double MaximumPackagePowerWatts = 2_000.0;
    public const string PackagePowerSource = "amd-rapl-package-energy";

    private const int MaximumMeasurementIntervalMultiplier = 3;
    private const int MaximumAcquisitionDivisor = 2;

    public static bool IsValidInterval(int milliseconds) =>
        milliseconds is >= MinimumIntervalMilliseconds and
            <= MaximumIntervalMilliseconds;

    public static long ToMeasurementMilliseconds(long timestampDelta)
    {
        double milliseconds =
            timestampDelta * 1000.0 / Stopwatch.Frequency;
        return Math.Max(
            1,
            checked((long)Math.Round(
                milliseconds,
                MidpointRounding.AwayFromZero)));
    }

    public static long ToAcquisitionMilliseconds(long timestampDelta)
    {
        double milliseconds =
            timestampDelta * 1000.0 / Stopwatch.Frequency;
        return Math.Max(0, checked((long)Math.Ceiling(milliseconds)));
    }

    public static long MaximumMeasurementTimestampDelta(
        int requestedIntervalMilliseconds) =>
        checked(
            Stopwatch.Frequency *
            (long)requestedIntervalMilliseconds *
            MaximumMeasurementIntervalMultiplier /
            1000L);

    public static long MaximumAcquisitionTimestampDelta(
        int requestedIntervalMilliseconds) =>
        checked(
            Stopwatch.Frequency *
            (long)requestedIntervalMilliseconds /
            (1000L * MaximumAcquisitionDivisor));
}

internal sealed record SystemTelemetryTargetRawReading(
    SystemTelemetryTarget Target,
    ulong Aperf,
    ulong Mperf,
    long Timestamp);

internal sealed record SystemTelemetryRawSnapshot(
    long AcquisitionStartedTimestamp,
    long AcquisitionCompletedTimestamp,
    double VcoreVolts,
    double CpuTemperatureCelsius,
    int PackageEnergyUnitExponent,
    uint PackageEnergy,
    long PackageEnergyTimestamp,
    IReadOnlyList<SystemTelemetryTargetRawReading> Targets);

internal sealed record SystemTelemetryTargetMeasurement(
    SystemTelemetryTarget Target,
    ulong AperfDelta,
    ulong MperfDelta,
    long MeasurementIntervalMilliseconds,
    double EffectiveMegahertz);

internal sealed record SystemTelemetryMeasurement(
    long MeasurementIntervalMilliseconds,
    long AcquisitionDurationMilliseconds,
    double VcoreVolts,
    double CpuTemperatureCelsius,
    double PackagePowerWatts,
    IReadOnlyList<SystemTelemetryTargetMeasurement> Targets);

internal sealed record SystemTelemetryObservation(
    bool Success,
    bool IsBaseline,
    SystemTelemetryMeasurement? Measurement,
    string? Error)
{
    public static SystemTelemetryObservation Baseline() =>
        new(true, true, null, null);

    public static SystemTelemetryObservation Sample(
        SystemTelemetryMeasurement measurement) =>
        new(true, false, measurement, null);

    public static SystemTelemetryObservation Fail(string error) =>
        new(false, false, null, error);
}

internal sealed class SystemTelemetryAccumulator
{
    private readonly int _requestedIntervalMilliseconds;
    private readonly IReadOnlyList<SystemTelemetryTarget> _targets;
    private SystemTelemetryRawSnapshot? _baseline;

    public SystemTelemetryAccumulator(
        int requestedIntervalMilliseconds,
        IReadOnlyList<SystemTelemetryTarget> targets)
    {
        if (!SystemTelemetryStreaming.IsValidInterval(
                requestedIntervalMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedIntervalMilliseconds));
        }

        ArgumentNullException.ThrowIfNull(targets);
        if (!SystemTelemetryTargetSet.IsCanonical(targets))
        {
            throw new ArgumentException(
                "System telemetry targets must be unique and in canonical order.",
                nameof(targets));
        }

        _requestedIntervalMilliseconds = requestedIntervalMilliseconds;
        _targets = targets.ToArray();
    }

    public SystemTelemetryObservation Observe(
        SystemTelemetryRawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? shapeError = ValidateSnapshotShape(snapshot);
        if (shapeError is not null)
        {
            return SystemTelemetryObservation.Fail(shapeError);
        }

        if (_baseline is null)
        {
            _baseline = snapshot;
            return SystemTelemetryObservation.Baseline();
        }

        SystemTelemetryRawSnapshot baseline = _baseline;
        if (snapshot.PackageEnergyUnitExponent !=
            baseline.PackageEnergyUnitExponent)
        {
            return SystemTelemetryObservation.Fail(
                "AMD RAPL package-energy unit changed during the stream.");
        }

        OperationResult<long> packageInterval = ValidateMeasurementInterval(
            baseline.PackageEnergyTimestamp,
            snapshot.PackageEnergyTimestamp,
            "AMD RAPL package-energy");
        if (!packageInterval.Success)
        {
            return SystemTelemetryObservation.Fail(packageInterval.Error!);
        }

        long packageTimestampDelta =
            snapshot.PackageEnergyTimestamp - baseline.PackageEnergyTimestamp;
        uint packageEnergyDelta = unchecked(
            snapshot.PackageEnergy - baseline.PackageEnergy);
        double energyUnitJoules = Math.Pow(
            0.5,
            snapshot.PackageEnergyUnitExponent);
        double packagePowerWatts =
            packageEnergyDelta * energyUnitJoules * Stopwatch.Frequency /
            packageTimestampDelta;
        if (!double.IsFinite(packagePowerWatts) ||
            packagePowerWatts is < 0 or >
                SystemTelemetryStreaming.MaximumPackagePowerWatts)
        {
            return SystemTelemetryObservation.Fail(
                $"AMD RAPL package-energy delta produced an implausible " +
                $"package power value ({packagePowerWatts.ToString(
                    "G17",
                    CultureInfo.InvariantCulture)} W). This can indicate a " +
                "counter reset rather than a valid 32-bit wrap.");
        }

        List<SystemTelemetryTargetMeasurement> targetMeasurements =
            new(_targets.Count);
        for (int index = 0; index < _targets.Count; index++)
        {
            SystemTelemetryTargetRawReading previous =
                baseline.Targets[index];
            SystemTelemetryTargetRawReading current =
                snapshot.Targets[index];
            OperationResult<long> targetInterval = ValidateMeasurementInterval(
                previous.Timestamp,
                current.Timestamp,
                $"target {current.Target}");
            if (!targetInterval.Success)
            {
                return SystemTelemetryObservation.Fail(targetInterval.Error!);
            }

            if (current.Aperf < previous.Aperf ||
                current.Mperf < previous.Mperf)
            {
                return SystemTelemetryObservation.Fail(
                    $"APERF/MPERF reset or 64-bit wrap detected for target " +
                    $"{current.Target}.");
            }

            ulong aperfDelta = current.Aperf - previous.Aperf;
            ulong mperfDelta = current.Mperf - previous.Mperf;
            long targetTimestampDelta = current.Timestamp - previous.Timestamp;
            long intervalSkew = Math.Abs(
                targetTimestampDelta - packageTimestampDelta);
            if (intervalSkew >
                SystemTelemetryStreaming.MaximumAcquisitionTimestampDelta(
                    _requestedIntervalMilliseconds))
            {
                return SystemTelemetryObservation.Fail(
                    $"Target {current.Target} measurement interval diverged " +
                    "from the package interval.");
            }

            double effectiveMegahertz =
                aperfDelta * (double)Stopwatch.Frequency /
                targetTimestampDelta /
                1_000_000.0;
            double mperfRateMegahertz =
                mperfDelta * (double)Stopwatch.Frequency /
                targetTimestampDelta /
                1_000_000.0;
            if (!double.IsFinite(effectiveMegahertz) ||
                !double.IsFinite(mperfRateMegahertz) ||
                effectiveMegahertz is < 0 or >
                    SystemTelemetryStreaming.MaximumEffectiveMegahertz ||
                mperfRateMegahertz is < 0 or >
                    SystemTelemetryStreaming.MaximumEffectiveMegahertz)
            {
                return SystemTelemetryObservation.Fail(
                    $"APERF/MPERF delta produced an implausible frequency " +
                    $"for target {current.Target}.");
            }

            targetMeasurements.Add(new SystemTelemetryTargetMeasurement(
                current.Target,
                aperfDelta,
                mperfDelta,
                targetInterval.Value,
                effectiveMegahertz));
        }

        _baseline = snapshot;
        return SystemTelemetryObservation.Sample(new SystemTelemetryMeasurement(
            packageInterval.Value,
            SystemTelemetryStreaming.ToAcquisitionMilliseconds(
                snapshot.AcquisitionCompletedTimestamp -
                snapshot.AcquisitionStartedTimestamp),
            snapshot.VcoreVolts,
            snapshot.CpuTemperatureCelsius,
            packagePowerWatts,
            targetMeasurements));
    }

    private string? ValidateSnapshotShape(SystemTelemetryRawSnapshot snapshot)
    {
        if (snapshot.AcquisitionStartedTimestamp < 0 ||
            snapshot.AcquisitionCompletedTimestamp <
                snapshot.AcquisitionStartedTimestamp)
        {
            return "System telemetry returned an invalid acquisition interval.";
        }

        long acquisitionTimestampDelta =
            snapshot.AcquisitionCompletedTimestamp -
            snapshot.AcquisitionStartedTimestamp;
        if (acquisitionTimestampDelta >
            SystemTelemetryStreaming.MaximumAcquisitionTimestampDelta(
                _requestedIntervalMilliseconds))
        {
            return
                "System telemetry acquisition exceeded half of the requested " +
                "sampling interval.";
        }

        if (!double.IsFinite(snapshot.VcoreVolts) ||
            snapshot.VcoreVolts is < 0.1 or > 2.0)
        {
            return "System telemetry returned an invalid Vcore value.";
        }

        if (!double.IsFinite(snapshot.CpuTemperatureCelsius) ||
            snapshot.CpuTemperatureCelsius is < -50.0 or > 150.0)
        {
            return "System telemetry returned an invalid CPU temperature.";
        }

        if (snapshot.PackageEnergyUnitExponent is < 0 or > 31)
        {
            return "AMD RAPL returned an invalid package-energy unit.";
        }

        if (snapshot.PackageEnergyTimestamp <
                snapshot.AcquisitionStartedTimestamp ||
            snapshot.PackageEnergyTimestamp >
                snapshot.AcquisitionCompletedTimestamp)
        {
            return
                "AMD RAPL package-energy timestamp fell outside the acquisition.";
        }

        if (snapshot.Targets.Count != _targets.Count)
        {
            return "System telemetry returned an incomplete target set.";
        }

        long previousTimestamp = snapshot.PackageEnergyTimestamp;
        for (int index = 0; index < _targets.Count; index++)
        {
            SystemTelemetryTargetRawReading reading = snapshot.Targets[index];
            if (reading.Target != _targets[index])
            {
                return
                    "System telemetry target identity or canonical order changed.";
            }

            if (reading.Timestamp < snapshot.AcquisitionStartedTimestamp ||
                reading.Timestamp > snapshot.AcquisitionCompletedTimestamp ||
                reading.Timestamp < previousTimestamp)
            {
                return
                    $"System telemetry returned an invalid timestamp for target " +
                    $"{reading.Target}.";
            }

            previousTimestamp = reading.Timestamp;
        }

        return null;
    }

    private OperationResult<long> ValidateMeasurementInterval(
        long previousTimestamp,
        long currentTimestamp,
        string source)
    {
        if (currentTimestamp <= previousTimestamp)
        {
            return OperationResult<long>.Fail(
                $"The {source} measurement timestamp did not advance.");
        }

        long timestampDelta = currentTimestamp - previousTimestamp;
        if (timestampDelta >=
            SystemTelemetryStreaming.MaximumMeasurementTimestampDelta(
                _requestedIntervalMilliseconds))
        {
            return OperationResult<long>.Fail(
                $"The {source} measurement became stale; sleep or a scheduler " +
                "stall may have interrupted the stream.");
        }

        return OperationResult<long>.Ok(
            SystemTelemetryStreaming.ToMeasurementMilliseconds(
                timestampDelta));
    }
}

internal sealed record SystemTelemetryTargetStreamSample(
    [property: JsonPropertyName("group")] ushort Group,
    [property: JsonPropertyName("processor")] byte Processor,
    [property: JsonPropertyName("aperfDelta")] ulong AperfDelta,
    [property: JsonPropertyName("mperfDelta")] ulong MperfDelta,
    [property: JsonPropertyName("measurementIntervalMilliseconds")]
        long MeasurementIntervalMilliseconds,
    [property: JsonPropertyName("effectiveMhz")] double EffectiveMhz);

internal sealed record SystemTelemetryStreamSample(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("elapsedMilliseconds")] long ElapsedMilliseconds,
    [property: JsonPropertyName("timestampUtc")] string TimestampUtc,
    [property: JsonPropertyName("measurementIntervalMilliseconds")]
        long MeasurementIntervalMilliseconds,
    [property: JsonPropertyName("acquisitionDurationMilliseconds")]
        long AcquisitionDurationMilliseconds,
    [property: JsonPropertyName("vcoreVolts")] double VcoreVolts,
    [property: JsonPropertyName("cpuTemperatureCelsius")]
        double CpuTemperatureCelsius,
    [property: JsonPropertyName("temperatureSource")] string TemperatureSource,
    [property: JsonPropertyName("packagePowerWatts")] double PackagePowerWatts,
    [property: JsonPropertyName("packagePowerSource")]
        string PackagePowerSource,
    [property: JsonPropertyName("targets")]
        IReadOnlyList<SystemTelemetryTargetStreamSample> Targets)
{
    public const string RecordKind = "system-telemetry";
    public const int CurrentSchemaVersion = 2;
}
