package com.milvasoft.milvaion.worker;

import java.util.Locale;
import java.util.Map;
import java.util.regex.Pattern;

/**
 * Kafka connection and topic settings, used when the worker's transport is {@link TransportType#KAFKA}.
 * They must match the API's {@code MilvaionConfig:Kafka} settings, in particular the topic prefix.
 */
public final class KafkaOptions {
    private static final Pattern PREFIX = Pattern.compile("^[a-zA-Z0-9._-]*$");

    private String bootstrapServers = "localhost:9092";
    private String securityProtocol = "PLAINTEXT";
    private String saslMechanism = "PLAIN";
    private String saslUsername;
    private String saslPassword;
    private String sslTruststoreLocation;
    private String sslTruststorePassword;
    private String sslTruststoreType;
    private String sslKeystoreLocation;
    private String sslKeystorePassword;
    private String sslKeystoreType;
    private String sslKeyPassword;
    private String topicPrefix = "";
    private boolean autoCreateTopics = true;
    private int numPartitions = 12;
    private short replicationFactor = -1;
    private Integer maxInFlightPerPartition;

    /** Comma separated {@code host:port} list. Default: {@code localhost:9092}. */
    public KafkaOptions bootstrapServers(String bootstrapServers) {
        this.bootstrapServers = bootstrapServers;
        return this;
    }

    /** {@code PLAINTEXT} (default), {@code SSL}, {@code SASL_PLAINTEXT} or {@code SASL_SSL}. The .NET spellings such as {@code SaslSsl} work too. */
    public KafkaOptions securityProtocol(String securityProtocol) {
        this.securityProtocol = securityProtocol;
        return this;
    }

    /** {@code PLAIN} (default), {@code SCRAM-SHA-256} or {@code SCRAM-SHA-512}. The .NET spellings such as {@code ScramSha256} work too. */
    public KafkaOptions saslMechanism(String saslMechanism) {
        this.saslMechanism = saslMechanism;
        return this;
    }

    public KafkaOptions saslUsername(String saslUsername) {
        this.saslUsername = saslUsername;
        return this;
    }

    public KafkaOptions saslPassword(String saslPassword) {
        this.saslPassword = saslPassword;
        return this;
    }

    /** Trust store that verifies the brokers. A PEM CA bundle works with {@code sslTruststoreType("PEM")}. */
    public KafkaOptions sslTruststoreLocation(String sslTruststoreLocation) {
        this.sslTruststoreLocation = sslTruststoreLocation;
        return this;
    }

    public KafkaOptions sslTruststorePassword(String sslTruststorePassword) {
        this.sslTruststorePassword = sslTruststorePassword;
        return this;
    }

    /** {@code JKS}, {@code PKCS12} or {@code PEM}. Default: the Kafka client's default. */
    public KafkaOptions sslTruststoreType(String sslTruststoreType) {
        this.sslTruststoreType = sslTruststoreType;
        return this;
    }

    /** Client certificate for mutual TLS. */
    public KafkaOptions sslKeystoreLocation(String sslKeystoreLocation) {
        this.sslKeystoreLocation = sslKeystoreLocation;
        return this;
    }

    public KafkaOptions sslKeystorePassword(String sslKeystorePassword) {
        this.sslKeystorePassword = sslKeystorePassword;
        return this;
    }

    /** {@code JKS}, {@code PKCS12} or {@code PEM}. */
    public KafkaOptions sslKeystoreType(String sslKeystoreType) {
        this.sslKeystoreType = sslKeystoreType;
        return this;
    }

    public KafkaOptions sslKeyPassword(String sslKeyPassword) {
        this.sslKeyPassword = sslKeyPassword;
        return this;
    }

    /** Prepended to every topic and consumer group name, for example {@code prod.}. Must match the API. Default: empty. */
    public KafkaOptions topicPrefix(String topicPrefix) {
        this.topicPrefix = topicPrefix;
        return this;
    }

    /** Create missing topics at startup. Default: true. */
    public KafkaOptions autoCreateTopics(boolean autoCreateTopics) {
        this.autoCreateTopics = autoCreateTopics;
        return this;
    }

    /** Partitions of auto-created topics. Default: 12. */
    public KafkaOptions numPartitions(int numPartitions) {
        this.numPartitions = numPartitions;
        return this;
    }

    /** Replication of auto-created topics. -1 (default) uses the broker's {@code default.replication.factor}. */
    public KafkaOptions replicationFactor(int replicationFactor) {
        this.replicationFactor = (short) replicationFactor;
        return this;
    }

    /** Jobs of one partition that may run at the same time. Default: the worker's {@code maxParallelJobs}. */
    public KafkaOptions maxInFlightPerPartition(int maxInFlightPerPartition) {
        this.maxInFlightPerPartition = maxInFlightPerPartition;
        return this;
    }

    /**
     * Reads MILVAION_KAFKA_BOOTSTRAP_SERVERS (or KAFKA_BOOTSTRAP_SERVERS), MILVAION_KAFKA_SECURITY_PROTOCOL,
     * MILVAION_KAFKA_SASL_MECHANISM, MILVAION_KAFKA_SASL_USERNAME, MILVAION_KAFKA_SASL_PASSWORD,
     * MILVAION_KAFKA_SSL_TRUSTSTORE_LOCATION, MILVAION_KAFKA_SSL_TRUSTSTORE_PASSWORD, MILVAION_KAFKA_SSL_TRUSTSTORE_TYPE,
     * MILVAION_KAFKA_SSL_KEYSTORE_LOCATION, MILVAION_KAFKA_SSL_KEYSTORE_PASSWORD, MILVAION_KAFKA_SSL_KEYSTORE_TYPE,
     * MILVAION_KAFKA_SSL_KEY_PASSWORD, MILVAION_KAFKA_TOPIC_PREFIX, MILVAION_KAFKA_AUTO_CREATE_TOPICS,
     * MILVAION_KAFKA_NUM_PARTITIONS (or MILVAION_KAFKA_PARTITIONS), MILVAION_KAFKA_REPLICATION_FACTOR and
     * MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION.
     */
    public static KafkaOptions fromEnv() {
        return fromEnv(System.getenv());
    }

    static KafkaOptions fromEnv(Map<String, String> env) {
        KafkaOptions options = new KafkaOptions();
        String value;

        if (!(value = first(env, "MILVAION_KAFKA_BOOTSTRAP_SERVERS", "KAFKA_BOOTSTRAP_SERVERS")).isEmpty()) options.bootstrapServers(value);
        if (!(value = get(env, "MILVAION_KAFKA_SECURITY_PROTOCOL")).isEmpty()) options.securityProtocol(value);
        if (!(value = get(env, "MILVAION_KAFKA_SASL_MECHANISM")).isEmpty()) options.saslMechanism(value);
        if (!(value = get(env, "MILVAION_KAFKA_SASL_USERNAME")).isEmpty()) options.saslUsername(value);
        if (!(value = get(env, "MILVAION_KAFKA_SASL_PASSWORD")).isEmpty()) options.saslPassword(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_TRUSTSTORE_LOCATION")).isEmpty()) options.sslTruststoreLocation(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_TRUSTSTORE_PASSWORD")).isEmpty()) options.sslTruststorePassword(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_TRUSTSTORE_TYPE")).isEmpty()) options.sslTruststoreType(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_KEYSTORE_LOCATION")).isEmpty()) options.sslKeystoreLocation(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_KEYSTORE_PASSWORD")).isEmpty()) options.sslKeystorePassword(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_KEYSTORE_TYPE")).isEmpty()) options.sslKeystoreType(value);
        if (!(value = get(env, "MILVAION_KAFKA_SSL_KEY_PASSWORD")).isEmpty()) options.sslKeyPassword(value);
        if (!(value = get(env, "MILVAION_KAFKA_TOPIC_PREFIX")).isEmpty()) options.topicPrefix(value);
        if (!(value = get(env, "MILVAION_KAFKA_AUTO_CREATE_TOPICS")).isEmpty()) options.autoCreateTopics(bool("MILVAION_KAFKA_AUTO_CREATE_TOPICS", value));
        if (!(value = first(env, "MILVAION_KAFKA_NUM_PARTITIONS", "MILVAION_KAFKA_PARTITIONS")).isEmpty()) options.numPartitions(integer("MILVAION_KAFKA_NUM_PARTITIONS", value));
        if (!(value = get(env, "MILVAION_KAFKA_REPLICATION_FACTOR")).isEmpty()) options.replicationFactor(integer("MILVAION_KAFKA_REPLICATION_FACTOR", value));
        if (!(value = get(env, "MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION")).isEmpty())
            options.maxInFlightPerPartition(integer("MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION", value));

        return options;
    }

    private static String first(Map<String, String> env, String key, String fallback) {
        String value = get(env, key);
        return value.isEmpty() ? get(env, fallback) : value;
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

    private static boolean bool(String key, String value) {
        if (value.equalsIgnoreCase("true") || value.equals("1")) return true;
        if (value.equalsIgnoreCase("false") || value.equals("0")) return false;

        throw new IllegalArgumentException(key + " must be true or false, got '" + value + "'");
    }

    /** Validates and normalizes the settings. */
    KafkaOptions resolve(int maxParallelJobs) {
        bootstrapServers = bootstrapServers == null ? "" : bootstrapServers.trim();

        if (bootstrapServers.isEmpty()) throw new IllegalArgumentException("Kafka bootstrapServers is required");

        topicPrefix = topicPrefix == null ? "" : topicPrefix.trim();

        if (!PREFIX.matcher(topicPrefix).matches())
            throw new IllegalArgumentException("Kafka topicPrefix may only contain a-z A-Z 0-9 . _ -, got '" + topicPrefix + "'");

        securityProtocol = normalizeProtocol(securityProtocol);
        saslMechanism = normalizeMechanism(saslMechanism);

        if (numPartitions < 1) throw new IllegalArgumentException("Kafka numPartitions must be positive, got " + numPartitions);
        if (replicationFactor == 0 || replicationFactor < -1)
            throw new IllegalArgumentException("Kafka replicationFactor must be -1 or positive, got " + replicationFactor);

        if (maxInFlightPerPartition == null) maxInFlightPerPartition = maxParallelJobs;
        if (maxInFlightPerPartition < 1)
            throw new IllegalArgumentException("Kafka maxInFlightPerPartition must be positive, got " + maxInFlightPerPartition);

        return this;
    }

    static String normalizeProtocol(String value) {
        String key = value == null ? "" : value.trim().toUpperCase(Locale.ROOT).replace("_", "").replace("-", "");

        return switch (key) {
            case "", "PLAINTEXT" -> "PLAINTEXT";
            case "SSL" -> "SSL";
            case "SASLPLAINTEXT" -> "SASL_PLAINTEXT";
            case "SASLSSL" -> "SASL_SSL";
            default -> throw new IllegalArgumentException("Kafka securityProtocol must be PLAINTEXT, SSL, SASL_PLAINTEXT or SASL_SSL, got '" + value + "'");
        };
    }

    static String normalizeMechanism(String value) {
        String key = value == null ? "" : value.trim().toUpperCase(Locale.ROOT).replace("_", "").replace("-", "");

        return switch (key) {
            case "", "PLAIN" -> "PLAIN";
            case "SCRAMSHA256" -> "SCRAM-SHA-256";
            case "SCRAMSHA512" -> "SCRAM-SHA-512";
            default -> throw new IllegalArgumentException("Kafka saslMechanism must be PLAIN, SCRAM-SHA-256 or SCRAM-SHA-512, got '" + value + "'");
        };
    }

    String bootstrapServers() { return bootstrapServers; }
    String securityProtocol() { return securityProtocol; }
    String saslMechanism() { return saslMechanism; }
    String saslUsername() { return saslUsername; }
    String saslPassword() { return saslPassword; }
    String sslTruststoreLocation() { return sslTruststoreLocation; }
    String sslTruststorePassword() { return sslTruststorePassword; }
    String sslTruststoreType() { return sslTruststoreType; }
    String sslKeystoreLocation() { return sslKeystoreLocation; }
    String sslKeystorePassword() { return sslKeystorePassword; }
    String sslKeystoreType() { return sslKeystoreType; }
    String sslKeyPassword() { return sslKeyPassword; }
    String topicPrefix() { return topicPrefix; }
    boolean autoCreateTopics() { return autoCreateTopics; }
    int numPartitions() { return numPartitions; }
    short replicationFactor() { return replicationFactor; }
    int maxInFlightPerPartition() { return maxInFlightPerPartition; }
}
