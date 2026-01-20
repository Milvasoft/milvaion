namespace Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

public record HealthCheckResponse
{
    public required string Status { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; }
    public List<HealthCheckEntry> Checks { get; init; } = [];
}

public record HealthCheckEntry
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string Description { get; init; }
    public TimeSpan Duration { get; init; }
    public List<string> Tags { get; init; } = [];
    public Dictionary<string, string> Data { get; init; } = [];
}

public record LivenessResponse
{
    public required string Status { get; init; }
    public DateTime Timestamp { get; init; }
    public TimeSpan Uptime { get; init; }
}
