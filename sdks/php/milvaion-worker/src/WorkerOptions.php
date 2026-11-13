<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\LoggerInterface;

/**
 * Worker configuration. Only $workerId is required.
 *
 * A PHP worker process runs one job at a time; run more processes to run more jobs in parallel.
 */
final class WorkerOptions
{
    public readonly string $instanceId;

    /**
     * @param string $workerId Identifies the worker application. Use the same value on every replica.
     * @param string $queueType Must match the API's MilvaionConfig:RabbitMQ:QueueType: "classic" or "quorum".
     * @param string|null $redisUrl Redis only carries cancellation requests. null disables them.
     * @param string $redisKeyPrefix Must match the API's MilvaionConfig:Redis:KeyPrefix.
     * @param float $executionTimeout Default job timeout in seconds. 0 disables it.
     * @param float $jobHeartbeatInterval Keeps long jobs from being flagged as zombies. 0 disables it.
     * @param float $shutdownTimeout How long a running job may finish after SIGTERM before it is returned to the queue.
     * @param float $startupTimeout How long run() waits for RabbitMQ and Redis.
     * @param LoggerInterface|null $logger Any PSR-3 logger. Default: messages go to STDERR.
     */
    public function __construct(
        public readonly string $workerId,
        public readonly string $rabbitMqUrl = 'amqp://guest:guest@localhost:5672/',
        public readonly string $queueType = 'classic',
        public readonly ?string $redisUrl = 'redis://localhost:6379',
        public readonly string $redisKeyPrefix = 'Milvaion:JobScheduler:',
        public readonly float $executionTimeout = 3600,
        public readonly float $heartbeatInterval = 30,
        public readonly float $jobHeartbeatInterval = 60,
        public readonly float $shutdownTimeout = 30,
        public readonly float $startupTimeout = 30,
        public readonly string $version = '1.0.0',
        ?string $instanceId = null,
        public readonly ?LoggerInterface $logger = null,
    ) {
        if (trim($workerId) === '') {
            throw new \InvalidArgumentException('workerId is required');
        }

        if (!in_array(strtolower($queueType), ['classic', 'quorum'], true)) {
            throw new \InvalidArgumentException("queueType must be \"classic\" or \"quorum\", got '{$queueType}'");
        }

        $this->instanceId = $instanceId ?: trim($workerId) . '-' . bin2hex(random_bytes(4));
    }

    public function quorum(): bool
    {
        return strtolower($this->queueType) === 'quorum';
    }

    public function cancellationChannel(): string
    {
        return $this->redisKeyPrefix . 'cancellation_channel';
    }

    /**
     * Reads MILVAION_WORKER_ID, MILVAION_RABBITMQ_URL, MILVAION_RABBITMQ_QUEUE_TYPE, MILVAION_REDIS_URL
     * ("false" disables Redis), MILVAION_REDIS_KEY_PREFIX, MILVAION_EXECUTION_TIMEOUT_SECONDS and
     * MILVAION_HEARTBEAT_INTERVAL_SECONDS. Named arguments win over the environment.
     *
     * @param array<string, string>|null $env Defaults to getenv().
     */
    public static function fromEnv(?array $env = null, mixed ...$overrides): self
    {
        $env ??= getenv();
        $get = static fn (string $key): string => trim((string) ($env[$key] ?? ''));
        $args = [];

        $map = [
            'MILVAION_WORKER_ID' => ['workerId', 'string'],
            'MILVAION_RABBITMQ_URL' => ['rabbitMqUrl', 'string'],
            'MILVAION_RABBITMQ_QUEUE_TYPE' => ['queueType', 'string'],
            'MILVAION_REDIS_KEY_PREFIX' => ['redisKeyPrefix', 'string'],
            'MILVAION_EXECUTION_TIMEOUT_SECONDS' => ['executionTimeout', 'float'],
            'MILVAION_HEARTBEAT_INTERVAL_SECONDS' => ['heartbeatInterval', 'float'],
        ];

        foreach ($map as $key => [$name, $type]) {
            $value = $get($key);

            if ($value === '') {
                continue;
            }

            if ($type === 'float' && !is_numeric($value)) {
                throw new \InvalidArgumentException("{$key} must be a number, got '{$value}'");
            }

            $args[$name] = $type === 'float' ? (float) $value : $value;
        }

        $redis = $get('MILVAION_REDIS_URL');

        if (strcasecmp($redis, 'false') === 0) {
            $args['redisUrl'] = null;
        } elseif ($redis !== '') {
            $args['redisUrl'] = $redis;
        }

        $args = array_merge($args, $overrides);
        $args['workerId'] ??= '';

        return new self(...$args);
    }
}
