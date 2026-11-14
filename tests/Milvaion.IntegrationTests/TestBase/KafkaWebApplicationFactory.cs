using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Testcontainers.Kafka;

namespace Milvaion.IntegrationTests.TestBase;

/// <summary>
/// API host on the Kafka transport, with the consumers that the end-to-end tests need (status tracker, log collector,
/// failed occurrence handler) switched on. Uses its own database so it runs in parallel with the other collections.
/// </summary>
public class KafkaWebApplicationFactory : CustomWebApplicationFactory
{
    private readonly KafkaContainer _kafkaContainer = new KafkaBuilder("apache/kafka:3.8.0")
        .WithCleanUp(true)
        .Build();

    protected override string DatabaseName => "testDb_kafka";

    /// <summary>
    /// Bootstrap address of the Kafka container, reachable from the test process.
    /// </summary>
    public string GetKafkaBootstrapServers() => $"{_kafkaContainer.Hostname}:{_kafkaContainer.GetMappedPublicPort(9092)}";

    protected override Task StartAdditionalContainersAsync() => _kafkaContainer.StartAsync();

    private Dictionary<string, string> KafkaSettings => new()
    {
        ["MilvaionConfig:Transport:Type"] = "Kafka",
        ["MilvaionConfig:Kafka:BootstrapServers"] = GetKafkaBootstrapServers(),
        ["MilvaionConfig:Kafka:NumPartitions"] = "3",
        ["MilvaionConfig:Kafka:ReplicationFactor"] = "1",
        ["MilvaionConfig:StatusTracker:Enabled"] = "true",
        ["MilvaionConfig:StatusTracker:BatchIntervalMs"] = "200",
        ["MilvaionConfig:LogCollector:Enabled"] = "true",
        ["MilvaionConfig:FailedOccurrenceHandler:Enabled"] = "true",
    };

    protected override void ConfigureAdditionalEnvironmentVariables()
    {
        foreach (var (key, value) in KafkaSettings)
            Environment.SetEnvironmentVariable(key.Replace(":", "__"), value);
    }

    protected override void ClearAdditionalEnvironmentVariables()
    {
        foreach (var key in KafkaSettings.Keys)
            Environment.SetEnvironmentVariable(key.Replace(":", "__"), null);

        // The base sets these on every build; put back the values the other collections expect.
        Environment.SetEnvironmentVariable("MilvaionConfig__StatusTracker__Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig__LogCollector__Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig__FailedOccurrenceHandler__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Added after the base's in-memory values, so these win for options read late.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(KafkaSettings));
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await Task.WhenAny(_kafkaContainer.StopAsync(), Task.Delay(5000));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Kafka container dispose error: {ex.Message}");
        }

        await base.DisposeAsync();
    }
}

[CollectionDefinition(nameof(KafkaTestCollection))]
public class KafkaTestCollection : ICollectionFixture<KafkaWebApplicationFactory>;
