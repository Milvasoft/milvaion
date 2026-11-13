package com.milvasoft.milvaion.worker;

import java.security.SecureRandom;
import java.time.Duration;
import java.util.HexFormat;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;

/** Worker configuration. Only {@link #workerId(String)} is required. */
public final class WorkerOptions {
    private String workerId;
    private TransportType transport = TransportType.RABBITMQ;
    private KafkaOptions kafka = new KafkaOptions();
    private String rabbitMqUri = "amqp://guest:guest@localhost:5672/";
    private String queueType = "classic";
    private String redisUri = "redis://localhost:6379";
    private String redisKeyPrefix = "Milvaion:JobScheduler:";
    private int maxParallelJobs = Runtime.getRuntime().availableProcessors() * 2;
    private Duration executionTimeout = Duration.ofHours(1);
    private Duration heartbeatInterval = Duration.ofSeconds(30);
    private Duration jobHeartbeatInterval = Duration.ofSeconds(60);
    private Duration shutdownTimeout = Duration.ofSeconds(30);
    private Duration startupTimeout = Duration.ofSeconds(30);
    private String version = "1.0.0";
    private String instanceId;

    /** Identifies the worker application. Use the same value on every replica. */
    public WorkerOptions workerId(String workerId) {
        this.workerId = workerId;
        return this;
    }

    /** The broker to use: {@link TransportType#RABBITMQ} (default) or {@link TransportType#KAFKA}. Must match the API. */
    public WorkerOptions transport(TransportType transport) {
        this.transport = transport;
        return this;
    }

    /** Kafka settings, used when the transport is {@link TransportType#KAFKA}. */
    public WorkerOptions kafka(KafkaOptions kafka) {
        this.kafka = kafka;
        return this;
    }

    /** e.g. {@code amqp://guest:guest@localhost:5672/}. */
    public WorkerOptions rabbitMqUri(String rabbitMqUri) {
        this.rabbitMqUri = rabbitMqUri;
        return this;
    }

    /** Must match the API's MilvaionConfig:RabbitMQ:QueueType: "classic" (default) or "quorum". */
    public WorkerOptions queueType(String queueType) {
        this.queueType = queueType;
        return this;
    }

    /** Redis only carries cancellation requests. {@code null} disables them. */
    public WorkerOptions redisUri(String redisUri) {
        this.redisUri = redisUri;
        return this;
    }

    /** Must match the API's MilvaionConfig:Redis:KeyPrefix. */
    public WorkerOptions redisKeyPrefix(String redisKeyPrefix) {
        this.redisKeyPrefix = redisKeyPrefix;
        return this;
    }

    /** Capacity reported for this instance. Default: available processors × 2. */
    public WorkerOptions maxParallelJobs(int maxParallelJobs) {
        this.maxParallelJobs = maxParallelJobs;
        return this;
    }

    /** Default job timeout. {@link Duration#ZERO} disables it. Default: 1 hour. */
    public WorkerOptions executionTimeout(Duration executionTimeout) {
        this.executionTimeout = executionTimeout;
        return this;
    }

    public WorkerOptions heartbeatInterval(Duration heartbeatInterval) {
        this.heartbeatInterval = heartbeatInterval;
        return this;
    }

    /** Keeps long jobs from being flagged as zombies. {@link Duration#ZERO} disables it. Default: 60 s. */
    public WorkerOptions jobHeartbeatInterval(Duration jobHeartbeatInterval) {
        this.jobHeartbeatInterval = jobHeartbeatInterval;
        return this;
    }

    /** How long {@code close()} lets running jobs finish before returning them to the queue. Default: 30 s. */
    public WorkerOptions shutdownTimeout(Duration shutdownTimeout) {
        this.shutdownTimeout = shutdownTimeout;
        return this;
    }

    /** How long {@code start()} waits for RabbitMQ and Redis. Default: 30 s. */
    public WorkerOptions startupTimeout(Duration startupTimeout) {
        this.startupTimeout = startupTimeout;
        return this;
    }

    /** Shown in the dashboard. */
    public WorkerOptions version(String version) {
        this.version = version;
        return this;
    }

    /** Overrides the generated "{workerId}-{8 hex}" id. Must be unique per process. */
    public WorkerOptions instanceId(String instanceId) {
        this.instanceId = instanceId;
        return this;
    }

    /**
     * Reads MILVAION_WORKER_ID, MILVAION_RABBITMQ_URL, MILVAION_RABBITMQ_QUEUE_TYPE, MILVAION_REDIS_URL
     * ("false" disables Redis), MILVAION_REDIS_KEY_PREFIX, MILVAION_MAX_PARALLEL_JOBS,
     * MILVAION_EXECUTION_TIMEOUT_SECONDS, MILVAION_HEARTBEAT_INTERVAL_SECONDS and MILVAION_TRANSPORT
     * ("rabbitmq" or "kafka"), plus the MILVAION_KAFKA_* variables listed on {@link KafkaOptions#fromEnv()}.
     */
    public static WorkerOptions fromEnv() {
        return fromEnv(System.getenv());
    }

    static WorkerOptions fromEnv(Map<String, String> env) {
        WorkerOptions options = new WorkerOptions();
        String value;

        if (!(value = get(env, "MILVAION_WORKER_ID")).isEmpty()) options.workerId(value);
        if (!(value = get(env, "MILVAION_TRANSPORT")).isEmpty()) options.transport(TransportType.parse(value));

        options.kafka(KafkaOptions.fromEnv(env));

        if (!(value = get(env, "MILVAION_RABBITMQ_URL")).isEmpty()) options.rabbitMqUri(value);
        if (!(value = get(env, "MILVAION_RABBITMQ_QUEUE_TYPE")).isEmpty()) options.queueType(value);
        if (!(value = get(env, "MILVAION_REDIS_KEY_PREFIX")).isEmpty()) options.redisKeyPrefix(value);
        if (!(value = get(env, "MILVAION_MAX_PARALLEL_JOBS")).isEmpty()) options.maxParallelJobs(integer("MILVAION_MAX_PARALLEL_JOBS", value));
        if (!(value = get(env, "MILVAION_EXECUTION_TIMEOUT_SECONDS")).isEmpty()) options.executionTimeout(Duration.ofSeconds(integer("MILVAION_EXECUTION_TIMEOUT_SECONDS", value)));
        if (!(value = get(env, "MILVAION_HEARTBEAT_INTERVAL_SECONDS")).isEmpty()) options.heartbeatInterval(Duration.ofSeconds(integer("MILVAION_HEARTBEAT_INTERVAL_SECONDS", value)));

        value = get(env, "MILVAION_REDIS_URL");

        if (value.equalsIgnoreCase("false")) options.redisUri(null);
        else if (!value.isEmpty()) options.redisUri(value);

        return options;
    }

    private static String get(Map<String, String> env, String key) {
        String value = env.get(key);
        return value == null ? "" : value.trim();
    }

    private static int integer(String key, String value) {
        try {
            return Integer.parseInt(value);
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException(key + " must be an integer, got '" + value + "'", e);
        }
    }

    /** Validates and fills in defaults. */
    WorkerOptions resolve() {
        workerId = workerId == null ? "" : workerId.trim();

        if (workerId.isEmpty()) throw new IllegalArgumentException("workerId is required");

        queueType = queueType == null ? "classic" : queueType.trim().toLowerCase(Locale.ROOT);

        if (!queueType.equals("classic") && !queueType.equals("quorum"))
            throw new IllegalArgumentException("queueType must be \"classic\" or \"quorum\", got '" + queueType + "'");

        if (maxParallelJobs <= 0) throw new IllegalArgumentException("maxParallelJobs must be positive, got " + maxParallelJobs);

        if (transport == null) transport = TransportType.RABBITMQ;

        if (transport == TransportType.KAFKA) Objects.requireNonNull(kafka, "kafka").resolve(maxParallelJobs);
        else Objects.requireNonNull(rabbitMqUri, "rabbitMqUri");

        if (instanceId == null || instanceId.isBlank()) {
            byte[] bytes = new byte[4];
            new SecureRandom().nextBytes(bytes);
            instanceId = workerId + "-" + HexFormat.of().formatHex(bytes);
        }

        return this;
    }

    String workerId() { return workerId; }
    TransportType transport() { return transport; }
    KafkaOptions kafka() { return kafka; }
    String rabbitMqUri() { return rabbitMqUri; }
    boolean quorum() { return "quorum".equals(queueType); }
    String redisUri() { return redisUri; }
    String cancellationChannel() { return redisKeyPrefix + "cancellation_channel"; }
    int maxParallelJobs() { return maxParallelJobs; }
    Duration executionTimeout() { return executionTimeout; }
    Duration heartbeatInterval() { return heartbeatInterval; }
    Duration jobHeartbeatInterval() { return jobHeartbeatInterval; }
    Duration shutdownTimeout() { return shutdownTimeout; }
    Duration startupTimeout() { return startupTimeout; }
    String version() { return version; }
    String instanceId() { return instanceId; }
}
