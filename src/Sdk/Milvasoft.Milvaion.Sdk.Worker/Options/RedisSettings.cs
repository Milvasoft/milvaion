namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class RedisSettings
{
    private string _cancellationChannel;

    public string ConnectionString { get; set; } = "localhost:6379";
    public string Password { get; set; } = "";
    public int Database { get; set; } = 0;

    /// <summary>
    /// Key/channel prefix used to build Redis channel names (e.g. "Milvaion:JobScheduler:"). Must match the
    /// Milvaion API side (<c>MilvaionConfig:Redis:KeyPrefix</c>) so pub/sub channels line up between the API and workers.
    /// </summary>
    public string KeyPrefix { get; set; } = "Milvaion:JobScheduler:";

    /// <summary>
    /// Pub/Sub channel name for job cancellation signals. Defaults to "{KeyPrefix}cancellation_channel" unless explicitly set.
    /// </summary>
    public string CancellationChannel
    {
        get => _cancellationChannel ?? $"{KeyPrefix}cancellation_channel";
        set => _cancellationChannel = value;
    }
}
