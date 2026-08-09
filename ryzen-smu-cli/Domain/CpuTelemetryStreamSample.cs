using System.Text.Json.Serialization;

namespace ryzen_smu_cli;

internal sealed record CpuTelemetryStreamSample(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("elapsedMilliseconds")] long ElapsedMilliseconds,
    [property: JsonPropertyName("timestampUtc")] string TimestampUtc,
    [property: JsonPropertyName("vcoreVolts")] double VcoreVolts,
    [property: JsonPropertyName("cpuTemperatureCelsius")] double CpuTemperatureCelsius,
    [property: JsonPropertyName("temperatureSource")] string TemperatureSource)
{
    public const string RecordKind = "telemetry";
    public const int CurrentSchemaVersion = 1;
}
