using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;

namespace Milvaion.Infrastructure.Services.Kafka;

/// <summary>
/// Owns the API's shared Kafka producer and admin client, and builds consumers. Thread-safe singleton.
/// </summary>
public sealed class KafkaClientFactory : IDisposable
{
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _topicCacheLifetime = TimeSpan.FromSeconds(15);

    private readonly KafkaOptions _options;
    private readonly IMilvaLogger _logger;
    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly Lazy<IAdminClient> _admin;
    private readonly Lock _topicCacheLock = new();
    private HashSet<string> _knownTopics = [];
    private DateTime _knownTopicsAt = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaClientFactory"/> class.
    /// </summary>
    public KafkaClientFactory(IOptions<KafkaOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value ?? new KafkaOptions();
        _logger = loggerFactory.CreateMilvaLogger<KafkaClientFactory>();

        KafkaTopicNames.ValidatePrefix(_options.TopicPrefix);

        _producer = new(() => new ProducerBuilder<string, byte[]>(BuildProducerConfig(_options))
                                .SetErrorHandler((_, error) => _logger.Warning("Kafka producer error: {Reason} (Code: {Code}, Fatal: {Fatal})", error.Reason, error.Code, error.IsFatal))
                                .SetLogHandler((_, message) => LogLibrdkafka(message))
                                .Build());

        _admin = new(() => new AdminClientBuilder(BuildClientConfig(_options))
                             .SetErrorHandler((_, error) => _logger.Warning("Kafka admin client error: {Reason} (Code: {Code})", error.Reason, error.Code))
                             .SetLogHandler((_, message) => LogLibrdkafka(message))
                             .Build());
    }

    /// <summary>
    /// The Kafka settings in effect.
    /// </summary>
    public KafkaOptions Options => _options;

    /// <summary>
    /// Topic prefix.
    /// </summary>
    public string Prefix => _options.TopicPrefix ?? string.Empty;

    /// <summary>
    /// Shared idempotent producer (acks=all).
    /// </summary>
    public IProducer<string, byte[]> Producer => _producer.Value;

    /// <summary>
    /// Shared admin client.
    /// </summary>
    public IAdminClient Admin => _admin.Value;

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

    /// <summary>
    /// Builds a consumer of the API's group.
    /// </summary>
    public ConsumerBuilder<string, byte[]> CreateConsumerBuilder(string clientId)
        => new ConsumerBuilder<string, byte[]>(BuildConsumerConfig(_options, KafkaTopicNames.WithPrefix(Prefix, KafkaTopicNames.ApiGroup), clientId))
              .SetErrorHandler((_, error) => _logger.Warning("Kafka consumer {ClientId} error: {Reason} (Code: {Code}, Fatal: {Fatal})", clientId, error.Reason, error.Code, error.IsFatal))
              .SetLogHandler((_, message) => LogLibrdkafka(message));

    /// <summary>
    /// Asks the cluster for its metadata. Throws when no broker answers.
    /// </summary>
    public Metadata GetMetadata() => Admin.GetMetadata(_metadataTimeout);

    /// <summary>
    /// Creates the topics that do not exist yet, with the configured partitions and replication. Does nothing when
    /// auto-creation is off.
    /// </summary>
    public async Task EnsureTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default)
    {
        if (!_options.AutoCreateTopics)
            return;

        var specifications = topics.Distinct()
                                   .Select(topic => new TopicSpecification
                                   {
                                       Name = topic,
                                       NumPartitions = _options.NumPartitions,
                                       ReplicationFactor = _options.ReplicationFactor
                                   })
                                   .ToList();

        if (specifications.Count == 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await Admin.CreateTopicsAsync(specifications);

            _logger.Information("Created Kafka topics: {Topics}", string.Join(", ", specifications.Select(s => s.Name)));
        }
        catch (CreateTopicsException ex)
        {
            var failures = ex.Results.Where(r => r.Error.Code is not ErrorCode.NoError and not ErrorCode.TopicAlreadyExists).ToList();

            if (failures.Count > 0)
                throw new KafkaException(failures[0].Error);
        }

        InvalidateTopicCache();
    }

    /// <summary>
    /// Whether <paramref name="topic"/> exists. Cached briefly; a miss refreshes the cache once.
    /// </summary>
    public bool TopicExists(string topic)
    {
        lock (_topicCacheLock)
        {
            if (DateTime.UtcNow - _knownTopicsAt < _topicCacheLifetime && _knownTopics.Contains(topic))
                return true;
        }

        var topics = GetMetadata().Topics.Where(t => t.Error.Code == ErrorCode.NoError).Select(t => t.Topic).ToHashSet();

        lock (_topicCacheLock)
        {
            _knownTopics = topics;
            _knownTopicsAt = DateTime.UtcNow;
        }

        return topics.Contains(topic);
    }

    /// <summary>
    /// Forgets the cached topic list.
    /// </summary>
    public void InvalidateTopicCache()
    {
        lock (_topicCacheLock)
            _knownTopicsAt = DateTime.MinValue;
    }

    private void LogLibrdkafka(LogMessage message)
    {
        if (message.Level <= SyslogLevel.Warning)
            _logger.Warning("librdkafka {Facility}: {Message}", message.Facility, message.Message);
        else
            _logger.Debug("librdkafka {Facility}: {Message}", message.Facility, message.Message);
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
                _logger.Debug(ex, "Kafka producer flush failed during dispose");
            }

            _producer.Value.Dispose();
        }

        if (_admin.IsValueCreated)
            _admin.Value.Dispose();
    }
}
