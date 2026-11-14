<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

use Milvasoft\Milvaion\Worker\Protocol;
use Milvasoft\Milvaion\Worker\Publisher;

/**
 * Publishes to Kafka: maps the processor's RabbitMQ-named destinations to topics and waits until
 * the broker has acknowledged each record. Only the worker process uses it; job processes never
 * touch the librdkafka handles they inherit.
 *
 * @internal
 */
final class KafkaPublisher implements Publisher
{
    private ?\RdKafka\Producer $producer = null;
    /** @var array<string, \RdKafka\ProducerTopic> */
    private array $topicHandles = [];
    private ?string $deliveryError = null;

    public function __construct(private readonly Topics $topics, private readonly string $instanceId)
    {
    }

    /** @param array<string, string> $config librdkafka settings */
    public function connect(array $config): \RdKafka\Producer
    {
        $conf = new \RdKafka\Conf();

        foreach ($config as $key => $value) {
            $conf->set($key, $value);
        }

        $conf->set('acks', 'all');
        // Uncompressed, so every client library of the deployment can read what this worker writes.
        $conf->set('compression.type', 'none');
        $conf->set('message.timeout.ms', '30000');
        $conf->set('linger.ms', '0');
        $conf->setDrMsgCb(function (\RdKafka\Producer $kafka, \RdKafka\Message $message): void {
            if ($message->err !== RD_KAFKA_RESP_ERR_NO_ERROR) {
                $this->deliveryError = $message->errstr();
            }
        });

        $this->producer = new \RdKafka\Producer($conf);
        $this->topicHandles = [];

        return $this->producer;
    }

    public function connected(): bool
    {
        return $this->producer !== null;
    }

    public function publish(string $exchange, string $routingKey, string $body, bool $persistent = true, ?string $correlationId = null, ?array $headers = null): void
    {
        [$topic, $key, $recordHeaders] = $this->record($exchange, $routingKey, $body, $correlationId, $headers);
        $this->produce($topic, $key, $body, $recordHeaders);
    }

    /**
     * The topic, key and headers a publish becomes.
     *
     * @param array<string, mixed>|null $headers
     * @return array{0: string, 1: ?string, 2: array<string, string>}
     */
    public function record(string $exchange, string $routingKey, string $body, ?string $correlationId, ?array $headers): array
    {
        [$topic, $key] = match (true) {
            $exchange === Protocol::EXCHANGE_DEAD_LETTER => [$this->topics->dlq, $correlationId],
            $exchange === '' && $routingKey === Protocol::QUEUE_STATUS_UPDATES => [$this->topics->status, self::bodyField($body, ['occurrenceId'])],
            $exchange === '' && $routingKey === Protocol::QUEUE_WORKER_LOGS => [$this->topics->logs, self::bodyField($body, ['logs', 0, 'occurrenceId'])],
            $exchange === '' && $routingKey === Protocol::QUEUE_WORKER_HEARTBEAT => [$this->topics->heartbeats, $this->instanceId],
            $exchange === '' && $routingKey === Protocol::QUEUE_WORKER_REGISTRATION => [$this->topics->registrations, $this->instanceId],
            default => throw new \InvalidArgumentException("No Kafka topic for exchange '{$exchange}' and routing key '{$routingKey}'."),
        };

        $headers ??= [];

        if ($correlationId !== null && $correlationId !== '') {
            $headers[Headers::OCCURRENCE_ID] = $correlationId;
            $headers[Headers::CORRELATION_ID] = $correlationId;
        }

        return [$topic, $key ?? $correlationId, Headers::encode($headers)];
    }

    /**
     * Produces one record and waits until the broker has acknowledged it.
     *
     * @param array<string, string> $headers
     */
    public function produce(string $topic, ?string $key, ?string $value, array $headers): void
    {
        if ($this->producer === null) {
            throw new \RuntimeException('Not connected to Kafka.');
        }

        $handle = $this->topicHandles[$topic] ??= $this->producer->newTopic($topic);
        $this->deliveryError = null;
        $handle->producev(RD_KAFKA_PARTITION_UA, 0, $value, $key, $headers === [] ? null : $headers);

        $result = $this->producer->flush(35_000);

        if ($result !== RD_KAFKA_RESP_ERR_NO_ERROR) {
            throw new \RuntimeException("Timed out writing a record to Kafka topic {$topic}.");
        }

        if ($this->deliveryError !== null) {
            throw new \RuntimeException("Kafka did not accept a record for {$topic}: {$this->deliveryError}");
        }
    }

    /** Produces, retrying a few times, for records whose loss would leave an offset stuck. */
    public function produceReliably(string $topic, ?string $key, ?string $value, array $headers): void
    {
        foreach ([0, 1, 2, 5] as $attempt => $delay) {
            if ($attempt > 0) {
                sleep($delay);
            }

            try {
                $this->produce($topic, $key, $value, $headers);

                return;
            } catch (\RuntimeException $e) {
                if ($this->producer === null || $attempt === 3) {
                    throw $e;
                }
            }
        }
    }

    public function close(): void
    {
        if ($this->producer !== null) {
            $this->producer->flush(10_000);
        }

        $this->topicHandles = [];
        $this->producer = null;
    }

    /** @param list<string|int> $path */
    private static function bodyField(string $body, array $path): ?string
    {
        $value = json_decode($body, true);

        foreach ($path as $segment) {
            if (!is_array($value) || !isset($value[$segment])) {
                return null;
            }

            $value = $value[$segment];
        }

        return is_string($value) ? $value : null;
    }
}
