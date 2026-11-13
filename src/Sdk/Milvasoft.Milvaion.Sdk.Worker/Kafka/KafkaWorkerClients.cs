using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Options;

namespace Milvasoft.Milvaion.Sdk.Worker.Kafka;

/// <summary>
/// The worker's shared Kafka producer and admin client, and the topics it needs. Thread-safe singleton.
/// </summary>
public sealed class KafkaWorkerClients : IDisposable
{
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(10);

    private readonly WorkerOptions _options;
    private readonly IMilvaLogger _logger;
    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly Lazy<IAdminClient> _admin;
    private readonly SemaphoreSlim _topicsLock = new(1, 1);
    private bool _topicsReady;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaWorkerClients"/> class.
    /// </summary>
    public KafkaWorkerClients(WorkerOptions options, IMilvaLogger logger)
    {
        _options = options;
        _logger = logger;

        KafkaTopicNames.ValidatePrefix(Settings.TopicPrefix);

        _producer = new(() => new ProducerBuilder<string, byte[]>(BuildProducerConfig(Settings))
                                .SetErrorHandler((_, error) => _logger?.Warning("Kafka producer error: {Reason} (Code: {Code}, Fatal: {Fatal})", error.Reason, error.Code, error.IsFatal))
                                .SetLogHandler((_, message) => LogLibrdkafka(message))
                                .Build());

        _admin = new(() => new AdminClientBuilder(BuildClientConfig(Settings))
                             .SetErrorHandler((_, error) => _logger?.Warning("Kafka admin client error: {Reason} (Code: {Code})", error.Reason, error.Code))
                             .SetLogHandler((_, message) => LogLibrdkafka(message))
                             .Build());
    }

    /// <summary>Kafka settings in effect.</summary>
    public KafkaSettings Settings => _options.Kafka ?? new KafkaSettings();

    /// <summary>Topic prefix.</summary>
    public string Prefix => Settings.TopicPrefix ?? string.Empty;

    /// <summary>This worker's job topic.</summary>
    public string JobsTopic => KafkaTopicNames.Jobs(Prefix, _options.WorkerId);

    /// <summary>This worker's retry delay topic.</summary>
    public string RetryTopic => KafkaTopicNames.Retry(Prefix, _options.WorkerId);

    /// <summary>The dead letter topic.</summary>
    public string DeadLetterTopic => KafkaTopicNames.WithPrefix(Prefix, KafkaTopicNames.DeadLetter);

    /// <summary>This worker's consumer group.</summary>
    public string Group => KafkaTopicNames.WorkerGroup(Prefix, _options.WorkerId);

    /// <summary>Shared idempotent producer (acks=all).</summary>
    public IProducer<string, byte[]> Producer => _producer.Value;

    /// <summary>Shared admin client.</summary>
    public IAdminClient Admin => _admin.Value;

    /// <summary>
    /// Asks the cluster for its metadata. Throws when no broker answers.
    /// </summary>
    public Metadata GetMetadata() => Admin.GetMetadata(_metadataTimeout);

    /// <summary>
    /// Creates the worker's job and retry topics, and the shared topics it produces to, when they are missing and
    /// auto-creation is on. Runs once per process; later calls return at once.
    /// </summary>
    public async Task EnsureWorkerTopicsAsync(CancellationToken cancellationToken = default)
    {
        if (_topicsReady)
            return;

        await _topicsLock.WaitAsync(cancellationToken);

        try
        {
            if (_topicsReady)
                return;

            if (Settings.AutoCreateTopics)
            {
                var topics = new List<string> { JobsTopic, RetryTopic };

                topics.AddRange(KafkaTopicNames.SharedTopics(Prefix));

                try
                {
                    await Admin.CreateTopicsAsync(topics.Distinct().Select(topic => new TopicSpecification
                    {
                        Name = topic,
                        NumPartitions = Settings.NumPartitions,
                        ReplicationFactor = Settings.ReplicationFactor
                    }));

                    _logger?.Information("Created Kafka topics: {Topics}", string.Join(", ", topics));
                }
                catch (CreateTopicsException ex)
                {
                    var failures = ex.Results.Where(r => r.Error.Code is not ErrorCode.NoError and not ErrorCode.TopicAlreadyExists).ToList();

                    if (failures.Count > 0)
                        throw new KafkaException(failures[0].Error);
                }
            }

            _topicsReady = true;
        }
        finally
        {
            _topicsLock.Release();
        }
    }

    /// <summary>
    /// Connection settings common to every client.
    /// </summary>
    public static ClientConfig BuildClientConfig(KafkaSettings settings)
    {
        var config = new ClientConfig
        {
            BootstrapServers = settings.BootstrapServers,
            SecurityProtocol = settings.SecurityProtocol switch
            {
                KafkaSecurityProtocol.Ssl => SecurityProtocol.Ssl,
                KafkaSecurityProtocol.SaslPlaintext => SecurityProtocol.SaslPlaintext,
                KafkaSecurityProtocol.SaslSsl => SecurityProtocol.SaslSsl,
                _ => SecurityProtocol.Plaintext
            },
            SocketKeepaliveEnable = true
        };

        if (settings.SecurityProtocol is KafkaSecurityProtocol.SaslPlaintext or KafkaSecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = settings.SaslMechanism switch
            {
                KafkaSaslMechanism.ScramSha256 => SaslMechanism.ScramSha256,
                KafkaSaslMechanism.ScramSha512 => SaslMechanism.ScramSha512,
                _ => SaslMechanism.Plain
            };
            config.SaslUsername = settings.SaslUsername;
            config.SaslPassword = settings.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(settings.SslCaLocation))
            config.SslCaLocation = settings.SslCaLocation;

        if (!string.IsNullOrWhiteSpace(settings.SslCertificateLocation))
            config.SslCertificateLocation = settings.SslCertificateLocation;

        if (!string.IsNullOrWhiteSpace(settings.SslKeyLocation))
            config.SslKeyLocation = settings.SslKeyLocation;

        if (!string.IsNullOrWhiteSpace(settings.SslKeyPassword))
            config.SslKeyPassword = settings.SslKeyPassword;

        return config;
    }

    /// <summary>
    /// Producer settings: idempotent, acknowledged by every in-sync replica.
    /// </summary>
    public static ProducerConfig BuildProducerConfig(KafkaSettings settings) => new(BuildClientConfig(settings))
    {
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = 5,
        // Murmur2, like the Java, Go and Node clients: a key lands on the same partition whoever produces it.
        Partitioner = Partitioner.Murmur2Random,
        MessageTimeoutMs = 30000
    };

    /// <summary>
    /// Consumer settings: manual commits from the earliest offset, cooperative rebalancing.
    /// </summary>
    public static ConsumerConfig BuildConsumerConfig(KafkaSettings settings, string groupId, string clientId) => new(BuildClientConfig(settings))
    {
        GroupId = groupId,
        ClientId = clientId,
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        AllowAutoCreateTopics = false
    };

    private void LogLibrdkafka(LogMessage message)
    {
        if (message.Level <= SyslogLevel.Warning)
            _logger?.Warning("librdkafka {Facility}: {Message}", message.Facility, message.Message);
        else
            _logger?.Debug("librdkafka {Facility}: {Message}", message.Facility, message.Message);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            try
            {
                _producer.Value.Flush(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Kafka producer flush failed during dispose");
            }

            _producer.Value.Dispose();
        }

        if (_admin.IsValueCreated)
            _admin.Value.Dispose();

        _topicsLock.Dispose();
    }
}
