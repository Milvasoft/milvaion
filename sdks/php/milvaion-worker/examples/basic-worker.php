<?php

/**
 * Sample Milvaion worker.
 *
 *     MILVAION_WORKER_ID=php-sample-worker \
 *     MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
 *     MILVAION_REDIS_URL=redis://localhost:6379 \
 *     php examples/basic-worker.php
 */

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use Milvasoft\Milvaion\Worker\JobContext;
use Milvasoft\Milvaion\Worker\JobOptions;
use Milvasoft\Milvaion\Worker\PermanentJobException;
use Milvasoft\Milvaion\Worker\Worker;
use Milvasoft\Milvaion\Worker\WorkerOptions;

$worker = new Worker(WorkerOptions::fromEnv(
    workerId: getenv('MILVAION_WORKER_ID') ?: 'php-sample-worker',
    heartbeatInterval: 5,
));

$worker->job('GreetJob', function (JobContext $ctx): array {
    $data = $ctx->data() ?? [];

    if (empty($data['name'])) {
        throw new PermanentJobException('Job data needs a "name".');
    }

    $ctx->log("Greeting {$data['name']}", ['attempt' => $ctx->retryCount + 1]);

    // With ext-pcntl a cancel or timeout interrupts this sleep; without it, check $ctx->isAborted().
    usleep(($data['delayMs'] ?? 1000) * 1000);

    return ['greeting' => "Hello, {$data['name']}!"];
}, new JobOptions(
    executionTimeout: 60,
    dataSchema: [
        'type' => 'object',
        'properties' => [
            'name' => ['type' => 'string', 'description' => 'Who to greet'],
            'delayMs' => ['type' => 'integer', 'description' => 'How long the job takes'],
        ],
        'required' => ['name'],
    ],
));

$worker->job('FlakyJob', function (JobContext $ctx): void {
    // Fails on the first attempt to show retries in the dashboard.
    if ($ctx->retryCount === 0) {
        throw new RuntimeException('Simulated transient failure');
    }

    $ctx->log('Succeeded on retry');
}, new JobOptions(maxRetries: 2, baseRetryDelay: 2));

$worker->run(); // blocks; SIGTERM or Ctrl+C shuts down gracefully
