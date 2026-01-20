namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class RedisSettings
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public string Password { get; set; } = "";
    public int Database { get; set; } = 0;
    public string CancellationChannel { get; set; } = "Milvaion:JobScheduler:cancellation_channel";
}
