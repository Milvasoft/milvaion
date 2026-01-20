namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class RabbitMQSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Routing key patterns this consumer subscribes to (e.g., ["test.*", "email.*"]).
    /// </summary>
    public string RoutingKeyPattern { get; set; }
}