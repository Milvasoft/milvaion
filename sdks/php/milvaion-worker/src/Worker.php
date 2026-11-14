<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Milvasoft\Milvaion\Worker\Kafka\KafkaPublisher;
use Milvasoft\Milvaion\Worker\Kafka\KafkaTransport;
use Milvasoft\Milvaion\Worker\Kafka\Topics;
use PhpAmqpLib\Channel\AMQPChannel;
use PhpAmqpLib\Connection\AMQPStreamConnection;
use PhpAmqpLib\Exception\AMQPTimeoutException;
use PhpAmqpLib\Message\AMQPMessage;
use PhpAmqpLib\Wire\AMQPTable;
use Psr\Log\LoggerInterface;

/**
 * A Milvaion worker. Register jobs with job(), then call run(), which blocks until SIGTERM or
 * SIGINT (or stop()) and then shuts down gracefully.
 *
 * A worker process runs one job at a time. Run more processes to run more jobs in parallel.
 *
 * With ext-pcntl and ext-posix (Linux, macOS) each job runs in a forked child process. The worker
 * keeps heartbeats going and watches for cancellation while it runs, and stops it by killing the
 * child on cancel, timeout or shutdown. Without them, jobs run in the worker process and those only
 * take effect when the job calls into its JobContext.
 *
 * With WorkerOptions transport "kafka" (needs ext-rdkafka) the worker reads its jobs from Kafka
 * instead of RabbitMQ. Only this process uses the Kafka client: job processes report back to it.
 */
final class Worker
{
    /** Version of this SDK, reported to the dashboard. */
    public const SDK_VERSION = '1.2.0';

    private readonly LoggerInterface $logger;
    /** @var array<string, ResolvedJob> */
    private array $jobs = [];
    private readonly CancellationRegistry $cancellations;
    private readonly ResourceSampler $sampler;
    private readonly Publisher $publisher;
    private readonly ?AmqpPublisher $amqp;
    private readonly ?KafkaTransport $kafka;
    private bool $heartbeatFailed = false;
    private readonly LogBuffer $logs;
    private readonly Processor $processor;
    private ?AMQPStreamConnection $connection = null;
    private ?AMQPChannel $consumeChannel = null;
    private ?RedisSubscriber $redis = null;
    private bool $started = false;
    private float $nextHeartbeat = 0;
    private readonly bool $signals;

    public function __construct(private readonly WorkerOptions $options)
    {
        $this->logger = $options->logger ?? new StderrLogger();
        $this->cancellations = new CancellationRegistry();
        $this->sampler = new ResourceSampler();

        if ($options->usesKafka()) {
            if (!extension_loaded('rdkafka')) {
                throw new \RuntimeException('The Kafka transport needs the rdkafka PHP extension (ext-rdkafka). Install it with "pecl install rdkafka" (it needs librdkafka), or use the RabbitMQ transport.');
            }

            $topics = new Topics($options->kafka->topicPrefix, $options->workerId);
            $this->publisher = $kafkaPublisher = new KafkaPublisher($topics, $options->instanceId);
            $this->amqp = null;
        } else {
            $this->publisher = $this->amqp = new AmqpPublisher();
        }

        $this->logs = new LogBuffer(fn (string $batch) => $this->publisher->publish('', Protocol::QUEUE_WORKER_LOGS, $batch), $this->logger);
        $this->processor = new Processor($options, [], $this->publisher, $this->logs, $this->cancellations, $this->logger);
        $this->kafka = isset($kafkaPublisher, $topics)
            ? new KafkaTransport($options, $topics, $kafkaPublisher, $this->processor, $this->logger, fn (int $count) => $this->sendHeartbeat(false, $count))
            : null;
        $this->signals = function_exists('pcntl_fork') && function_exists('pcntl_async_signals') && function_exists('posix_kill');
    }

    public function instanceId(): string
    {
        return $this->options->instanceId;
    }

    /**
     * Registers a handler under the name the dashboard dispatches the job by (JobNameInWorker).
     * Names are unique within a worker and matched case-insensitively.
     *
     * @param callable(JobContext): mixed $handler Its return value becomes the occurrence result.
     */
    public function job(string $name, callable $handler, ?JobOptions $options = null): self
    {
        if ($this->started) {
            throw new \LogicException('Jobs must be added before run().');
        }

        $name = trim($name);

        if ($name === '') {
            throw new \InvalidArgumentException('Job name is required.');
        }

        if (isset($this->jobs[strtolower($name)])) {
            throw new \InvalidArgumentException("Job '{$name}' is already registered. Job names must be unique within a worker (case-insensitive).");
        }

        $this->jobs[strtolower($name)] = ($options ?? new JobOptions())->resolve($name, \Closure::fromCallable($handler), $this->options);

        return $this;
    }

    /** Asks run() to stop after the current job; safe to call from a signal handler. */
    public function stop(): void
    {
        $this->processor->requestStop();
    }

    /**
     * Connects, registers this instance and processes jobs until SIGTERM, SIGINT or stop(). Then
     * it stops taking jobs, lets a running job finish for up to shutdownTimeout, returns it to the
     * queue otherwise, and tells the API the instance has left.
     */
    public function run(): void
    {
        if ($this->started) {
            throw new \LogicException('run() can only be called once.');
        }

        if ($this->jobs === []) {
            throw new \LogicException('Add at least one job before run().');
        }

        $this->started = true;
        $this->logger->info("Starting worker {$this->options->workerId} (instance {$this->options->instanceId}) with " . count($this->jobs) . ' job type(s).');
        $this->configureProcessor();

        if (!$this->signals) {
            $this->logger->warning('ext-pcntl or ext-posix is not available: running jobs are only cancelled or timed out when they call their context.');
        }

        if ($this->options->redisUrl !== null) {
            $this->redis = new RedisSubscriber($this->options->redisUrl, $this->options->cancellationChannel(), $this->logger);
            $this->redis->connect($this->options->startupTimeout);
            $this->logger->info("Listening for cancellations on {$this->options->cancellationChannel()}");
        } else {
            $this->logger->warning('Redis is disabled: jobs cannot be cancelled from the dashboard.');
        }

        if ($this->kafka !== null) {
            $this->runKafka($this->kafka);

            return;
        }

        $this->connect($this->options->startupTimeout);

        $this->installSignals();

        while (!$this->processor->stopRequested()) {
            try {
                $this->consumeChannel->wait(null, false, 1.0);
            } catch (AMQPTimeoutException) {
                // Idle second: fall through to the periodic work.
            } catch (\Throwable $e) {
                if ($this->processor->stopRequested()) {
                    break;
                }

                $this->logger->warning("Lost the RabbitMQ connection ({$e->getMessage()}). Reconnecting...");
                $this->reconnect();
            }

            $this->periodic();
        }

        $this->shutdown();
    }

    /** The worker loop on Kafka: one job at a time, taken from the job and retry topics. */
    private function runKafka(KafkaTransport $kafka): void
    {
        try {
            $kafka->start(fn () => $this->register());
        } catch (\Throwable $e) {
            $this->redis?->close();

            throw $e;
        }

        $this->installSignals();
        // Short enough to send the job heartbeats of occurrences that wait for a retry on time.
        $interval = $this->options->jobHeartbeatInterval;
        $wait = $interval > 0 ? max(0.05, min(1.0, $interval)) : 1.0;

        while (!$this->processor->stopRequested()) {
            try {
                $kafka->step($wait);
            } catch (\Throwable $e) {
                $this->logger->warning('Kafka worker loop error: ' . $e->getMessage());
                usleep(500_000);
            }

            $this->periodic();
        }

        $kafka->stop();
        $this->shutdown();
        $kafka->close();
    }

    private function configureProcessor(): void
    {
        $this->processor->setJobs($this->jobs);
        $this->processor->isolate = $this->signals;
        $this->processor->noDestructorsInChild = $this->kafka !== null;
        $this->processor->housekeeping = function (): void {
            $this->kafka?->service();
            $this->processor->heartbeatWaiting();
            $this->pollCancellations();

            if (microtime(true) >= $this->nextHeartbeat) {
                $this->sendHeartbeat(false, $this->processor->running());
            }
        };
    }

    private function installSignals(): void
    {
        if (!$this->signals) {
            return;
        }

        pcntl_async_signals(true);

        // Only flags the stop: the main loop and the running job's watcher act on it.
        foreach ([SIGTERM, SIGINT] as $signal) {
            pcntl_signal($signal, function (): void {
                if (!$this->processor->stopRequested()) {
                    $this->logger->info("Stopping worker instance {$this->options->instanceId}...");
                    $this->processor->requestStop();
                }
            });
        }
    }

    private function connect(float $timeout): void
    {
        $deadline = microtime(true) + $timeout;

        while (true) {
            try {
                $this->open();
                $this->logger->info('Connected to RabbitMQ.');

                return;
            } catch (\Throwable $e) {
                $this->logger->warning("Could not connect to RabbitMQ: {$e->getMessage()}");
                $this->closeConnection();

                if (microtime(true) + 2 > $deadline) {
                    throw new \RuntimeException("Could not connect to RabbitMQ within {$timeout}s: {$e->getMessage()}", 0, $e);
                }

                sleep(2);
            }
        }
    }

    private function reconnect(): void
    {
        $this->closeConnection();

        while (!$this->processor->stopRequested()) {
            try {
                $this->open();
                $this->logger->info('Reconnected to RabbitMQ.');

                return;
            } catch (\Throwable $e) {
                $this->logger->warning("Could not connect to RabbitMQ: {$e->getMessage()}");
                $this->closeConnection();
                sleep(2);
            }
        }
    }

    /** Opens the connection, declares the topology, starts consuming and registers. */
    private function open(): void
    {
        $this->connection = AmqpConnector::open($this->options->rabbitMqUrl);

        $queueArgs = $this->options->quorum() ? new AMQPTable(['x-queue-type' => 'quorum']) : [];
        $publish = $this->connection->channel();
        $publish->confirm_select();

        foreach ([Protocol::QUEUE_STATUS_UPDATES, Protocol::QUEUE_WORKER_LOGS, Protocol::QUEUE_WORKER_HEARTBEAT, Protocol::QUEUE_WORKER_REGISTRATION] as $queue) {
            $publish->queue_declare($queue, false, true, false, false, false, $queueArgs);
        }

        $publish->exchange_declare(Protocol::EXCHANGE_JOBS, 'topic', false, true, false);
        $publish->exchange_declare(Protocol::EXCHANGE_DEAD_LETTER, 'direct', false, true, false);
        $publish->queue_declare(Protocol::QUEUE_FAILED_OCCURRENCES, false, true, false, false, false, $queueArgs);
        $publish->queue_bind(Protocol::QUEUE_FAILED_OCCURRENCES, Protocol::EXCHANGE_DEAD_LETTER, Protocol::DEAD_LETTER_ROUTING_KEY);

        // Before consuming, so the first delivery can already report its status.
        $this->amqp?->attach($publish);

        $consume = $this->connection->channel();
        // One job at a time across every job type this process handles.
        $consume->basic_qos(0, 1, true);

        foreach ($this->jobs as $job) {
            $args = ['x-dead-letter-exchange' => Protocol::EXCHANGE_DEAD_LETTER, 'x-dead-letter-routing-key' => Protocol::DEAD_LETTER_ROUTING_KEY];

            if ($this->options->quorum()) {
                $args['x-queue-type'] = 'quorum';
            }

            $consume->queue_declare($job->queue, false, true, false, false, false, new AMQPTable($args));
            $consume->queue_bind($job->queue, Protocol::EXCHANGE_JOBS, $job->routingPattern);
            $consume->basic_consume($job->queue, "{$this->options->instanceId}.{$job->consumerId}", false, false, false, false,
                fn (AMQPMessage $message) => $this->onMessage($message, $job));

            $this->logger->info("Consuming {$job->name} from {$job->queue} (pattern {$job->routingPattern}).");
        }

        $this->consumeChannel = $consume;
        $this->register();
    }

    private function onMessage(AMQPMessage $message, ResolvedJob $job): void
    {
        $headers = $message->has('application_headers') ? $message->get('application_headers')->getNativeData() : [];

        $delivery = new Delivery(
            $message->getBody(),
            $headers,
            $message->has('correlation_id') ? (string) $message->get('correlation_id') : null,
            (bool) $message->isRedelivered(),
            fn () => $message->ack(),
            fn (bool $requeue) => $message->nack($requeue),
        );

        $this->sendHeartbeat(false, 1);

        try {
            $this->processor->handle($delivery, $job);
        } finally {
            $this->sendHeartbeat(false, 0);
        }
    }

    private function periodic(): void
    {
        $this->pollCancellations();
        $this->processor->heartbeatWaiting();

        if (microtime(true) >= $this->nextHeartbeat) {
            $this->sendHeartbeat(false, 0);
        }

        if ($this->logs->pending() > 0) {
            try {
                $this->logs->flush();
            } catch (\Throwable $e) {
                $this->logger->debug('Failed to send logs; will retry: ' . $e->getMessage());
            }
        }
    }

    private function pollCancellations(): void
    {
        if ($this->redis === null) {
            return;
        }

        foreach ($this->redis->poll() as $payload) {
            $request = json_decode($payload, true);

            if (!is_array($request)) {
                continue;
            }

            $occurrenceId = $request['CorrelationId'] ?? $request['OccurrenceId'] ?? null;

            if (is_string($occurrenceId) && $this->cancellations->cancel($occurrenceId, $request['Reason'] ?? null)) {
                $this->logger->info("Cancelling occurrence {$occurrenceId}: " . ($request['Reason'] ?? 'no reason given'));
            }
        }
    }

    private function register(): void
    {
        $this->publisher->publish('', Protocol::QUEUE_WORKER_REGISTRATION, Protocol::json($this->buildRegistration()));
        $this->sendHeartbeat(false, $this->processor->hasRunningJob() ? 1 : 0);
        $transport = $this->options->usesKafka() ? 'Kafka' : 'RabbitMQ';
        $this->logger->info("Registered {$this->options->workerId} (instance {$this->options->instanceId}) on {$transport}.");
    }

    /**
     * @return array<string, mixed>
     * @internal
     */
    public function buildRegistration(): array
    {
        $configs = [];
        $routing = [];
        $dataSchemas = [];
        $resultSchemas = [];

        foreach ($this->jobs as $job) {
            $routing[$job->name] = $job->routingPattern;

            if ($job->dataSchema !== null) {
                $dataSchemas[$job->name] = $job->dataSchema;
            }

            if ($job->resultSchema !== null) {
                $resultSchemas[$job->name] = $job->resultSchema;
            }

            $configs[] = ['JobType' => $job->name, 'ConsumerId' => $job->consumerId, 'MaxParallelJobs' => 1, 'ExecutionTimeoutSeconds' => (int) $job->executionTimeout];
        }

        return [
            'workerId' => $this->options->workerId,
            'instanceId' => $this->options->instanceId,
            'displayName' => "{$this->options->workerId} ({$this->options->instanceId})",
            'hostName' => gethostname() ?: 'Unknown',
            'ipAddress' => ResourceSampler::localIpAddress(),
            'routingPatterns' => (object) $routing,
            'jobDataDefinitions' => (object) $dataSchemas,
            'jobResultDefinitions' => (object) $resultSchemas,
            'jobTypes' => array_values(array_map(fn (ResolvedJob $job) => $job->name, $this->jobs)),
            'maxParallelJobs' => 1,
            'version' => $this->options->version,
            // The API reads this case-sensitively, so its properties are PascalCase.
            'metadata' => Protocol::json([
                'IsExternal' => false,
                'ExternalScheduler' => null,
                'ProcessorCount' => 1,
                'OSVersion' => php_uname('s') . ' ' . php_uname('r'),
                'RuntimeVersion' => 'PHP ' . PHP_VERSION,
                'HeartbeatInterval' => (int) $this->options->heartbeatInterval,
                'Sdk' => 'php',
                'SdkVersion' => self::SDK_VERSION,
                'JobConfigs' => $configs,
            ]),
        ];
    }

    private function sendHeartbeat(bool $stopping, int $currentJobs): void
    {
        $this->nextHeartbeat = microtime(true) + $this->options->heartbeatInterval;

        try {
            $this->publisher->publish('', Protocol::QUEUE_WORKER_HEARTBEAT, Protocol::json([
                'workerId' => $this->options->workerId,
                'instanceId' => $this->options->instanceId,
                'currentJobs' => $stopping ? 0 : $currentJobs,
                'memoryBytes' => $this->sampler->memoryBytes(),
                'cpuUsagePercent' => $this->sampler->cpuUsagePercent(),
                'timestamp' => Protocol::now(),
                'isStopping' => $stopping,
            ]), false);
        } catch (\Throwable $e) {
            $this->heartbeatFailed = true;
            $this->logger->debug('Heartbeat failed; will retry on the next interval: ' . $e->getMessage());

            return;
        }

        // On Kafka there is no reconnect to register again after: once heartbeats get through
        // again, register, since the API may have dropped the instance meanwhile.
        if ($this->heartbeatFailed && $this->kafka !== null && !$stopping) {
            $this->heartbeatFailed = false;

            try {
                $this->register();
            } catch (\Throwable $e) {
                $this->heartbeatFailed = true;
                $this->logger->warning('Could not register again after Kafka recovered: ' . $e->getMessage());
            }
        }
    }

    private function shutdown(): void
    {
        if ($this->consumeChannel !== null) {
            foreach ($this->jobs as $job) {
                try {
                    $this->consumeChannel->basic_cancel("{$this->options->instanceId}.{$job->consumerId}");
                } catch (\Throwable) {
                    // Channel already closed
                }
            }
        }

        $this->sendHeartbeat(true, 0);

        try {
            $this->logs->flush();
        } catch (\Throwable $e) {
            $this->logger->warning('Some job logs could not be sent before shutdown: ' . $e->getMessage());
        }

        $this->redis?->close();
        $this->closeConnection();
        $this->logger->info("Worker instance {$this->options->instanceId} stopped.");
    }

    private function closeConnection(): void
    {
        $this->amqp?->detach();
        $this->consumeChannel = null;

        if ($this->connection !== null) {
            try {
                $this->connection->close();
            } catch (\Throwable) {
                // Already closed
            }

            $this->connection = null;
        }
    }
}
