namespace Milvaion.Application.Utils.Models.Options;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public class MilvaionConfig
{
    public RedisOptions Redis { get; set; }
    public RabbitMQOptions RabbitMQ { get; set; }
    public JobDispatcherOptions JobDispatcher { get; set; }
    public ZombieOccurrenceDetectorOptions ZombieOccurrenceDetector { get; set; }
    public JobAutoDisableOptions JobAutoDisable { get; set; }
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
