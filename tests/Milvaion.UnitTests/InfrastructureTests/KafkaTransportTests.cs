using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Infrastructure;
using Milvaion.Infrastructure.Services.Kafka;
using Milvaion.Infrastructure.Services.Messaging;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using System.Text;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "Kafka topic naming unit tests.")]
public class KafkaTopicNamesTests
{
    [Theory]
    [InlineData("report-worker", "report-worker")]
    [InlineData("Report_Worker.1", "report_worker.1")]
    [InlineData("my worker/ä", "my_worker__")]
    [InlineData("", "")]
    public void Sanitize_ShouldLowerCaseAndReplaceIllegalCharacters(string input, string expected)
        => KafkaTopicNames.Sanitize(input).Should().Be(expected);

    [Fact]
    public void Names_ShouldFollowTheDocumentedScheme()
    {
        KafkaTopicNames.Jobs("prod.", "Report-Worker").Should().Be("prod.milvaion.jobs.report-worker");
        KafkaTopicNames.Retry("prod.", "Report-Worker").Should().Be("prod.milvaion.jobs.report-worker.retry");
        KafkaTopicNames.WorkerGroup("prod.", "Report-Worker").Should().Be("prod.milvaion.worker.report-worker");
        KafkaTopicNames.WithPrefix("", KafkaTopicNames.ApiGroup).Should().Be("milvaion.api");
    }

    [Theory]
    [InlineData(WorkerConstant.Queues.StatusUpdates, "p.milvaion.status")]
    [InlineData(WorkerConstant.Queues.WorkerLogs, "p.milvaion.logs")]
    [InlineData(WorkerConstant.Queues.WorkerHeartbeat, "p.milvaion.heartbeats")]
    [InlineData(WorkerConstant.Queues.WorkerRegistration, "p.milvaion.registrations")]
    [InlineData(WorkerConstant.Queues.FailedOccurrences, "p.milvaion.dlq")]
    [InlineData(WorkerConstant.Queues.ExternalJobRegistration, "p.milvaion.external.registrations")]
    [InlineData(WorkerConstant.Queues.ExternalJobOccurrence, "p.milvaion.external.occurrences")]
    [InlineData("scheduled_jobs_queue.x", null)]
    public void ForQueue_ShouldMapEveryApiQueue(string queue, string expected)
        => KafkaTopicNames.ForQueue("p.", queue).Should().Be(expected);

    [Fact]
    public void SharedTopics_ShouldCoverEveryMappedQueue()
        => KafkaTopicNames.SharedTopics("").Should().HaveCount(7).And.OnlyHaveUniqueItems();

    [Theory]
    [InlineData("w1", "w1.sendemail.*", "w1")]
    [InlineData(null, "report-worker.sendemail.*", "report-worker")]
    [InlineData("", "*.anything", null)]
    [InlineData(null, null, null)]
    public void ResolveWorkerId_ShouldPreferTheJobWorkerThenThePattern(string workerId, string pattern, string expected)
        => KafkaTopicNames.ResolveWorkerId(workerId, pattern).Should().Be(expected);

    [Theory]
    [InlineData("milvaion.jobs.w1", "w1")]
    [InlineData("milvaion.jobs.w1.retry", "w1")]
    [InlineData("milvaion.status", null)]
    public void WorkerOfJobTopic_ShouldRecogniseJobAndRetryTopics(string topic, string expected)
        => KafkaTopicNames.WorkerOfJobTopic("", topic).Should().Be(expected);

    [Theory]
    [InlineData("prod.")]
    [InlineData("team_a-")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidatePrefix_ShouldAcceptLegalPrefixes(string prefix)
        => FluentActions.Invoking(() => KafkaTopicNames.ValidatePrefix(prefix)).Should().NotThrow();

    [Theory]
    [InlineData("prod/")]
    [InlineData("a b")]
    public void ValidatePrefix_ShouldRejectIllegalPrefixes(string prefix)
        => FluentActions.Invoking(() => KafkaTopicNames.ValidatePrefix(prefix)).Should().Throw<ArgumentException>();
}

[Trait("Infrastructure Unit Tests", "Message header value unit tests.")]
public class MessageHeaderValuesTests
{
    [Fact]
    public void GetInt_ShouldReadAmqpNumbersAndKafkaDecimalStrings()
    {
        var headers = new Dictionary<string, object>
        {
            ["amqp-int"] = 3,
            ["amqp-long"] = 4L,
            ["kafka"] = Encoding.UTF8.GetBytes("5"),
            ["text"] = "6",
            ["garbage"] = Encoding.UTF8.GetBytes("x")
        };

        MessageHeaderValues.GetInt(headers, "amqp-int").Should().Be(3);
        MessageHeaderValues.GetInt(headers, "amqp-long").Should().Be(4);
        MessageHeaderValues.GetInt(headers, "kafka").Should().Be(5);
        MessageHeaderValues.GetInt(headers, "text").Should().Be(6);
        MessageHeaderValues.GetInt(headers, "garbage").Should().BeNull();
        MessageHeaderValues.GetInt(headers, "missing").Should().BeNull();
        MessageHeaderValues.GetInt(null, "missing").Should().BeNull();
    }

    [Fact]
    public void GetGuid_ShouldReadBytesAndStrings()
    {
        var id = Guid.NewGuid();
        var headers = new Dictionary<string, object> { ["a"] = Encoding.UTF8.GetBytes(id.ToString()), ["b"] = id.ToString(), ["c"] = "nope" };

        MessageHeaderValues.GetGuid(headers, "a").Should().Be(id);
        MessageHeaderValues.GetGuid(headers, "b").Should().Be(id);
        MessageHeaderValues.GetGuid(headers, "c").Should().BeNull();
    }
}

[Trait("Infrastructure Unit Tests", "Ordered offset tracker unit tests.")]
public class PartitionOffsetTrackerTests
{
    [Fact]
    public void Committable_ShouldBeNullBeforeAnyRecord()
        => new PartitionOffsetTracker().GetCommittable().Should().BeNull();

    [Fact]
    public void Committable_ShouldNeverPassAnUnfinishedRecord()
    {
        var tracker = new PartitionOffsetTracker();

        tracker.Add(10);
        tracker.Add(11);
        tracker.Add(12);

        // Records finish out of order: the slow record 10 holds the commit back.
        tracker.Complete(12);
        tracker.Complete(11);

        tracker.GetCommittable().Should().Be(10);
        tracker.InFlight.Should().Be(1);

        tracker.Complete(10);

        tracker.GetCommittable().Should().Be(13);
        tracker.InFlight.Should().Be(0);
    }

    [Fact]
    public void Committable_ShouldBeNullOnceCommitted()
    {
        var tracker = new PartitionOffsetTracker();

        tracker.Add(0);
        tracker.Complete(0);
        tracker.MarkCommitted(tracker.GetCommittable()!.Value);

        tracker.GetCommittable().Should().BeNull();

        tracker.Add(1);

        tracker.GetCommittable().Should().BeNull("record 1 is still pending and offset 1 is already committed");

        tracker.Complete(1);

        tracker.GetCommittable().Should().Be(2);
    }

    [Fact]
    public void Complete_ShouldReportUnknownOffsets()
    {
        var tracker = new PartitionOffsetTracker();

        tracker.Add(5);

        tracker.Complete(6).Should().BeFalse();
        tracker.Complete(5).Should().BeTrue();
        tracker.Complete(5).Should().BeFalse();
    }
}

[Trait("Infrastructure Unit Tests", "Message transport selection unit tests.")]
public class MessageTransportRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMessageTransport(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_ShouldRegisterRabbitMQ()
    {
        using var provider = Build(new() { ["MilvaionConfig:RabbitMQ:Host"] = "localhost" });

        InfraServiceCollectionExtensions.GetTransportType(new ConfigurationBuilder().Build()).Should().Be(MessageTransportType.RabbitMQ);
        provider.GetRequiredService<IJobPublisher>().Should().BeOfType<RabbitMQPublisher>();
        provider.GetRequiredService<IRabbitMQPublisher>().Should().BeSameAs(provider.GetRequiredService<IJobPublisher>());
        provider.GetRequiredService<IMessageConsumerTransport>().Should().BeOfType<RabbitMQConsumerTransport>();
        provider.GetRequiredService<IMessageTransportInitializer>().Should().BeOfType<RabbitMQTransportInitializer>();
    }

    [Fact]
    public void Kafka_ShouldRegisterKafkaServicesAndNoRabbitMQ()
    {
        using var provider = Build(new()
        {
            ["MilvaionConfig:Transport:Type"] = "Kafka",
            ["MilvaionConfig:Kafka:BootstrapServers"] = "broker:9092",
            ["MilvaionConfig:Kafka:TopicPrefix"] = "test."
        });

        provider.GetRequiredService<IJobPublisher>().Should().BeOfType<KafkaJobPublisher>();
        provider.GetRequiredService<IMessageConsumerTransport>().Name.Should().Be("Kafka");
        provider.GetRequiredService<IMessageTransportInitializer>().Should().BeOfType<KafkaTransportInitializer>();
        provider.GetRequiredService<IQueueDepthMonitor>().Should().BeOfType<KafkaQueueDepthMonitor>();
        provider.GetRequiredService<KafkaClientFactory>().Prefix.Should().Be("test.");
        provider.GetService<IRabbitMQPublisher>().Should().BeNull();
        provider.GetService<RabbitMQConnectionFactory>().Should().BeNull();
    }

    [Fact]
    public void ClientConfig_ShouldCarrySaslAndSsl()
    {
        var config = KafkaClientFactory.BuildConsumerConfig(new KafkaSettings
        {
            BootstrapServers = "b:9093",
            SecurityProtocol = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism = KafkaSaslMechanism.ScramSha512,
            SaslUsername = "u",
            SaslPassword = "p",
            SslCaLocation = "/ca.pem"
        }, "g", "c");

        config.SecurityProtocol.Should().Be(SecurityProtocol.SaslSsl);
        config.SaslMechanism.Should().Be(SaslMechanism.ScramSha512);
        config.SaslUsername.Should().Be("u");
        config.SslCaLocation.Should().Be("/ca.pem");
        config.EnableAutoCommit.Should().BeFalse();
        config.AutoOffsetReset.Should().Be(AutoOffsetReset.Earliest);
        config.PartitionAssignmentStrategy.Should().Be(PartitionAssignmentStrategy.CooperativeSticky);
    }
}

[Trait("Worker SDK Unit Tests", "Kafka job record unit tests.")]
public class KafkaJobDeliveryTests
{
    private static ConsumeResult<string, byte[]> Record(Headers headers, string key = null) => new()
    {
        Topic = "milvaion.jobs.w.retry",
        Partition = new Partition(0),
        Offset = new Offset(7),
        Message = new Message<string, byte[]> { Key = key, Value = Encoding.UTF8.GetBytes("{}"), Headers = headers }
    };

    [Fact]
    public void Headers_ShouldBeReadAsUtf8Strings()
    {
        var occurrenceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var due = DateTimeOffset.UtcNow.AddMinutes(1);

        var delivery = new KafkaJobDelivery(Record(new Headers
        {
            { KafkaHeaderNames.OccurrenceId, Encoding.UTF8.GetBytes(occurrenceId.ToString()) },
            { KafkaHeaderNames.RetryCount, Encoding.UTF8.GetBytes("2") },
            { KafkaHeaderNames.JobType, Encoding.UTF8.GetBytes("SendEmailJob") },
            { KafkaHeaderNames.NotBefore, Encoding.UTF8.GetBytes(due.ToUnixTimeMilliseconds().ToString()) }
        }, jobId.ToString()), new PartitionOffsetTracker());

        delivery.CorrelationId.Should().Be(occurrenceId);
        delivery.RetryCount.Should().Be(2);
        delivery.JobType.Should().Be("SendEmailJob");
        delivery.JobId.Should().Be(jobId);
        delivery.NotBefore.Should().BeCloseTo(due.UtcDateTime, TimeSpan.FromMilliseconds(1));
        delivery.Redelivered.Should().BeFalse();
        delivery.Offset.Should().Be(7);
    }

    [Fact]
    public void MissingHeaders_ShouldFallBackToCorrelationIdAndZeroRetries()
    {
        var occurrenceId = Guid.NewGuid();

        var delivery = new KafkaJobDelivery(Record(new Headers { { KafkaHeaderNames.CorrelationId, Encoding.UTF8.GetBytes(occurrenceId.ToString()) } }), new PartitionOffsetTracker());

        delivery.CorrelationId.Should().Be(occurrenceId);
        delivery.RetryCount.Should().Be(0);
        delivery.NotBefore.Should().Be(DateTime.MinValue);
        delivery.JobId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void WorkerClients_ShouldNameTheWorkerTopics()
    {
        using var clients = new KafkaWorkerClients(new WorkerOptions { WorkerId = "Sample Worker", Kafka = new KafkaSettings { TopicPrefix = "t." } }, null);

        clients.JobsTopic.Should().Be("t.milvaion.jobs.sample_worker");
        clients.RetryTopic.Should().Be("t.milvaion.jobs.sample_worker.retry");
        clients.DeadLetterTopic.Should().Be("t.milvaion.dlq");
        clients.Group.Should().Be("t.milvaion.worker.sample_worker");
    }

    [Fact]
    public void WorkerOptions_ShouldDefaultToRabbitMQ()
    {
        new WorkerOptions().UsesKafka.Should().BeFalse();
        new WorkerOptions { Transport = new WorkerTransportSettings { Type = MessageTransportType.Kafka } }.UsesKafka.Should().BeTrue();
    }
}
