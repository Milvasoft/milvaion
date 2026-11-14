<?php

// A worker on Kafka for KafkaIntegrationTest, run as its own process. Configured by environment:
// MILVAION_KAFKA_BROKERS, TEST_PREFIX, TEST_INSTANCE_ID, TEST_MODE (complete, fail, sleep),
// TEST_MAX_RETRIES, TEST_BASE_DELAY, TEST_SHUTDOWN_TIMEOUT, TEST_ATTEMPTS_FILE.

declare(strict_types=1);

require __DIR__ . '/../../vendor/autoload.php';

use Milvasoft\Milvaion\Worker\JobContext;
use Milvasoft\Milvaion\Worker\JobOptions;
use Milvasoft\Milvaion\Worker\KafkaOptions;
use Milvasoft\Milvaion\Worker\StderrLogger;
use Milvasoft\Milvaion\Worker\Worker;
use Milvasoft\Milvaion\Worker\WorkerOptions;

$env = static fn (string $key, string $default = ''): string => (string) (getenv($key) ?: $default);

$worker = new Worker(new WorkerOptions(
    workerId: 'PHP-Kafka-Worker',
    redisUrl: null,
    heartbeatInterval: 1,
    jobHeartbeatInterval: 0.15,
    shutdownTimeout: (float) $env('TEST_SHUTDOWN_TIMEOUT', '5'),
    startupTimeout: 30,
    instanceId: $env('TEST_INSTANCE_ID'),
    logger: new StderrLogger(),
    transport: 'kafka',
    // TLS and SASL settings, if any, come from the MILVAION_KAFKA_* variables.
    kafka: KafkaOptions::fromEnv(
        null,
        brokers: $env('MILVAION_KAFKA_BROKERS'),
        topicPrefix: $env('TEST_PREFIX'),
        numPartitions: 2,
        replicationFactor: 1,
    ),
));

$mode = $env('TEST_MODE', 'complete');
$attempts = $env('TEST_ATTEMPTS_FILE');

$worker->job('TestJob', function (JobContext $ctx) use ($mode, $attempts) {
    if ($attempts !== '') {
        file_put_contents($attempts, sprintf("%s %d %.3F\n", $ctx->occurrenceId, $ctx->retryCount, microtime(true)), FILE_APPEND | LOCK_EX);
    }

    $ctx->log('hello from php', ['n' => $ctx->data()['n'] ?? null]);

    return match ($mode) {
        'fail' => throw new RuntimeException('always fails'),
        'sleep' => sleep(60),
        default => ['done' => true, 'retry' => $ctx->retryCount],
    };
}, new JobOptions(maxRetries: (int) $env('TEST_MAX_RETRIES', '0'), baseRetryDelay: (float) $env('TEST_BASE_DELAY', '1')));

$worker->run();
