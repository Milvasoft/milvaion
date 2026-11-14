<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/**
 * Settings of the Kafka transport (WorkerOptions transport "kafka"). The defaults connect to
 * localhost:9092 without TLS or SASL. See docs/githubdocs/KAFKA-TRANSPORT.md in the repository.
 */
final class KafkaOptions
{
    private const TOPIC_PREFIX = '/^[A-Za-z0-9._-]*$/';

    /** @var list<string> Bootstrap brokers as "host:port". */
    public readonly array $brokers;
    public readonly int $replicationFactor;

    /**
     * @param string|list<string> $brokers Bootstrap list; an entry may itself be comma separated.
     * @param bool $tls Enables TLS. Setting any of the files below enables it too.
     * @param string|null $tlsCaFile PEM bundle used to verify the brokers. Default: the system roots.
     * @param string|null $tlsCertFile PEM client certificate for mutual TLS, set together with $tlsKeyFile.
     * @param string|null $saslMechanism "plain", "scram-sha-256" or "scram-sha-512". null disables SASL.
     * @param string $topicPrefix Prepended to every topic and consumer group name, e.g. "prod.". Must match the API's.
     * @param bool $autoCreateTopics Create the worker's topics, and the shared topics it writes to, at startup.
     * @param int $numPartitions Partitions of the topics it creates.
     * @param int $replicationFactor Replication of the topics it creates. -1 uses the broker's default.
     * @param int|null $maxInFlightPerPartition Jobs of one partition that may run at once. Default: the
     *        worker's parallelism, which is 1 for a PHP worker process.
     */
    public function __construct(
        string|array $brokers = 'localhost:9092',
        public readonly bool $tls = false,
        public readonly ?string $tlsCaFile = null,
        public readonly ?string $tlsCertFile = null,
        public readonly ?string $tlsKeyFile = null,
        public readonly ?string $saslMechanism = null,
        public readonly ?string $saslUsername = null,
        public readonly ?string $saslPassword = null,
        public readonly string $topicPrefix = '',
        public readonly bool $autoCreateTopics = true,
        public readonly int $numPartitions = 12,
        int $replicationFactor = -1,
        public readonly ?int $maxInFlightPerPartition = null,
    ) {
        $list = [];

        foreach ((array) $brokers as $entry) {
            foreach (explode(',', (string) $entry) as $broker) {
                if (($broker = trim($broker)) !== '') {
                    $list[] = $broker;
                }
            }
        }

        $this->brokers = $list === [] ? ['localhost:9092'] : $list;
        $this->replicationFactor = $replicationFactor === 0 ? -1 : $replicationFactor;

        if (!preg_match(self::TOPIC_PREFIX, $topicPrefix)) {
            throw new \InvalidArgumentException("Kafka topicPrefix may only contain a-z A-Z 0-9 . _ -, got '{$topicPrefix}'");
        }

        if ($numPartitions < 1) {
            throw new \InvalidArgumentException("Kafka numPartitions must be positive, got {$numPartitions}");
        }

        if ($this->replicationFactor < -1 || $this->replicationFactor > 32767) {
            throw new \InvalidArgumentException("Kafka replicationFactor must be -1 or positive, got {$replicationFactor}");
        }

        if ($maxInFlightPerPartition !== null && $maxInFlightPerPartition < 1) {
            throw new \InvalidArgumentException("Kafka maxInFlightPerPartition must be positive, got {$maxInFlightPerPartition}");
        }

        if (($tlsCertFile === null || $tlsCertFile === '') !== ($tlsKeyFile === null || $tlsKeyFile === '')) {
            throw new \InvalidArgumentException('Kafka tlsCertFile and tlsKeyFile must be set together');
        }

        if ($this->saslMechanismName() !== null && ($saslUsername === null || $saslUsername === '')) {
            throw new \InvalidArgumentException('Kafka saslUsername is required with saslMechanism');
        }
    }

    /** TLS is on when asked for or when any certificate file is set. */
    public function tlsEnabled(): bool
    {
        return $this->tls || ($this->tlsCaFile ?? '') !== '' || ($this->tlsCertFile ?? '') !== '' || ($this->tlsKeyFile ?? '') !== '';
    }

    /** The librdkafka name of the SASL mechanism: PLAIN, SCRAM-SHA-256, SCRAM-SHA-512, or null without SASL. */
    public function saslMechanismName(): ?string
    {
        $mechanism = str_replace(['-', '_'], '', strtolower(trim($this->saslMechanism ?? '')));

        return match ($mechanism) {
            '' => null,
            'plain' => 'PLAIN',
            'scramsha256' => 'SCRAM-SHA-256',
            'scramsha512' => 'SCRAM-SHA-512',
            default => throw new \InvalidArgumentException("Kafka saslMechanism must be \"plain\", \"scram-sha-256\" or \"scram-sha-512\", got '{$this->saslMechanism}'"),
        };
    }

    /** Jobs of one partition that may run at once, given the worker's parallelism. */
    public function inFlightPerPartition(int $maxParallelJobs): int
    {
        return $this->maxInFlightPerPartition ?? $maxParallelJobs;
    }

    /**
     * The librdkafka settings shared by the producer and the consumer.
     *
     * @return array<string, string>
     */
    public function clientConfig(string $clientId): array
    {
        $tls = $this->tlsEnabled();
        $sasl = $this->saslMechanismName();

        $config = [
            'bootstrap.servers' => implode(',', $this->brokers),
            'client.id' => $clientId,
            'security.protocol' => match (true) {
                $tls && $sasl !== null => 'SASL_SSL',
                $sasl !== null => 'SASL_PLAINTEXT',
                $tls => 'SSL',
                default => 'PLAINTEXT',
            },
        ];

        if (($this->tlsCaFile ?? '') !== '') {
            $config['ssl.ca.location'] = $this->tlsCaFile;
        }

        if (($this->tlsCertFile ?? '') !== '') {
            $config['ssl.certificate.location'] = $this->tlsCertFile;
            $config['ssl.key.location'] = (string) $this->tlsKeyFile;
        }

        if ($sasl !== null) {
            $config['sasl.mechanisms'] = $sasl;
            $config['sasl.username'] = (string) $this->saslUsername;
            $config['sasl.password'] = (string) $this->saslPassword;
        }

        return $config;
    }

    /**
     * Reads MILVAION_KAFKA_BOOTSTRAP_SERVERS (or KAFKA_BOOTSTRAP_SERVERS), MILVAION_KAFKA_TLS,
     * MILVAION_KAFKA_TLS_CA_FILE, MILVAION_KAFKA_TLS_CERT_FILE, MILVAION_KAFKA_TLS_KEY_FILE,
     * MILVAION_KAFKA_SASL_MECHANISM, MILVAION_KAFKA_SASL_USERNAME, MILVAION_KAFKA_SASL_PASSWORD,
     * MILVAION_KAFKA_TOPIC_PREFIX, MILVAION_KAFKA_AUTO_CREATE_TOPICS, MILVAION_KAFKA_PARTITIONS
     * (or MILVAION_KAFKA_NUM_PARTITIONS), MILVAION_KAFKA_REPLICATION_FACTOR and
     * MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION. Named arguments win over the environment.
     *
     * @param array<string, string>|null $env Defaults to getenv().
     */
    public static function fromEnv(?array $env = null, mixed ...$overrides): self
    {
        $env ??= getenv();
        $get = static fn (string $key): string => trim((string) ($env[$key] ?? ''));
        $args = [];

        $brokers = $get('MILVAION_KAFKA_BOOTSTRAP_SERVERS') ?: $get('KAFKA_BOOTSTRAP_SERVERS');

        if ($brokers !== '') {
            $args['brokers'] = $brokers;
        }

        if (strcasecmp($get('MILVAION_KAFKA_TLS'), 'true') === 0) {
            $args['tls'] = true;
        }

        if (strcasecmp($get('MILVAION_KAFKA_AUTO_CREATE_TOPICS'), 'false') === 0) {
            $args['autoCreateTopics'] = false;
        }

        $strings = [
            'MILVAION_KAFKA_TLS_CA_FILE' => 'tlsCaFile',
            'MILVAION_KAFKA_TLS_CERT_FILE' => 'tlsCertFile',
            'MILVAION_KAFKA_TLS_KEY_FILE' => 'tlsKeyFile',
            'MILVAION_KAFKA_SASL_MECHANISM' => 'saslMechanism',
            'MILVAION_KAFKA_SASL_USERNAME' => 'saslUsername',
            'MILVAION_KAFKA_SASL_PASSWORD' => 'saslPassword',
            'MILVAION_KAFKA_TOPIC_PREFIX' => 'topicPrefix',
        ];

        foreach ($strings as $key => $name) {
            if (($value = $get($key)) !== '') {
                $args[$name] = $value;
            }
        }

        $ints = [
            'MILVAION_KAFKA_NUM_PARTITIONS' => 'numPartitions',
            'MILVAION_KAFKA_PARTITIONS' => 'numPartitions',
            'MILVAION_KAFKA_REPLICATION_FACTOR' => 'replicationFactor',
            'MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION' => 'maxInFlightPerPartition',
        ];

        foreach ($ints as $key => $name) {
            $value = $get($key);

            if ($value === '') {
                continue;
            }

            if (!preg_match('/^-?\d+$/', $value)) {
                throw new \InvalidArgumentException("{$key} must be an integer, got '{$value}'");
            }

            $args[$name] = (int) $value;
        }

        return new self(...array_merge($args, $overrides));
    }
}
