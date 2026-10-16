namespace ryzen_smu_cli;

internal sealed record PowerLimitSnapshot(
    uint PptWatts,
    uint TdcAmps,
    uint EdcAmps);

internal sealed record PowerLimitTelemetryLayout(
    uint CpuFamily,
    uint CpuModel,
    uint CpuPackage,
    uint PmTableVersion,
    uint PmTableSize,
    int PptLimitIndex,
    int TdcLimitIndex,
    int EdcLimitIndex)
{
    private const double WholeNumberTolerance = 0.05;

    // Vermeer 0x380904 hardware work established the front-of-table Zen 3
    // limit/value ordering:
    //   d[0] PPT limit, d[2] TDC limit, d[8] EDC limit.
    // The later 0x380905 layout keeps that front block and inserts its known
    // additional eight-float block much later in the per-core area. This
    // exact 0x380905/0x5D0 identity is also the physically observed Tarca
    // Ryzen 5 5600X platform; no other PM-table revision is admitted here.
    private static readonly PowerLimitTelemetryLayout Vermeer380905 = new(
        0x19,
        0x21,
        2,
        0x00380905,
        0x000005D0,
        0,
        2,
        8);

    public static bool TryResolve(
        uint cpuFamily,
        uint cpuModel,
        uint cpuPackage,
        uint pmTableVersion,
        uint pmTableSize,
        out PowerLimitTelemetryLayout? layout)
    {
        PowerLimitTelemetryLayout candidate = Vermeer380905;
        if (candidate.CpuFamily == cpuFamily &&
            candidate.CpuModel == cpuModel &&
            candidate.CpuPackage == cpuPackage &&
            candidate.PmTableVersion == pmTableVersion &&
            candidate.PmTableSize == pmTableSize)
        {
            layout = candidate;
            return true;
        }

        layout = null;
        return false;
    }

    public OperationResult<PowerLimitSnapshot> Read(float[]? table)
    {
        if (table is null ||
            table.Length <= Math.Max(
                PptLimitIndex,
                Math.Max(TdcLimitIndex, EdcLimitIndex)))
        {
            return OperationResult<PowerLimitSnapshot>.Fail(
                $"PM table 0x{PmTableVersion:X8} does not contain the " +
                "required configured power-limit entries.");
        }

        OperationResult<uint> ppt =
            ReadWholeLimit(table[PptLimitIndex], "PPT", "W");
        if (!ppt.Success)
        {
            return OperationResult<PowerLimitSnapshot>.Fail(ppt.Error!);
        }

        OperationResult<uint> tdc =
            ReadWholeLimit(table[TdcLimitIndex], "TDC", "A");
        if (!tdc.Success)
        {
            return OperationResult<PowerLimitSnapshot>.Fail(tdc.Error!);
        }

        OperationResult<uint> edc =
            ReadWholeLimit(table[EdcLimitIndex], "EDC", "A");
        if (!edc.Success)
        {
            return OperationResult<PowerLimitSnapshot>.Fail(edc.Error!);
        }

        return OperationResult<PowerLimitSnapshot>.Ok(
            new PowerLimitSnapshot(ppt.Value, tdc.Value, edc.Value));
    }

    private OperationResult<uint> ReadWholeLimit(
        float rawValue,
        string name,
        string unit)
    {
        if (!float.IsFinite(rawValue) ||
            rawValue < CliRequest.MinimumPowerLimitValue ||
            rawValue > CliRequest.MaximumPowerLimitValue)
        {
            return OperationResult<uint>.Fail(
                $"PM table 0x{PmTableVersion:X8} returned an invalid " +
                $"{name} limit ({rawValue} {unit}).");
        }

        double rounded = Math.Round(
            rawValue,
            MidpointRounding.AwayFromZero);
        if (Math.Abs(rawValue - rounded) > WholeNumberTolerance)
        {
            return OperationResult<uint>.Fail(
                $"PM table 0x{PmTableVersion:X8} returned a non-integral " +
                $"{name} limit ({rawValue} {unit}); refusing to guess.");
        }

        return OperationResult<uint>.Ok(checked((uint)rounded));
    }
}
