<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Tests;

use Milvasoft\Milvaion\Worker\Errors;
use Milvasoft\Milvaion\Worker\JobOptions;
use Milvasoft\Milvaion\Worker\LogBuffer;
use Milvasoft\Milvaion\Worker\Processor;
use Milvasoft\Milvaion\Worker\Worker;
use Milvasoft\Milvaion\Worker\WorkerOptions;
use PHPUnit\Framework\TestCase;
use Psr\Log\NullLogger;

final class UnitsTest extends TestCase
{
    private const GUID = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';

    public function testRoutingMatchesDotNetSdk(): void
    {
        $this->assertSame('email-worker-01.sendemail.*', JobOptions::generateRoutingPattern('SendEmailJob', 'Email-Worker-01'));
        $this->assertSame('w.nonparallel.*', JobOptions::generateRoutingPattern('NonParallel', 'w'));
        $this->assertSame('scheduled_jobs_queue.email-worker-01.sendemail.wildcard', JobOptions::queueNameForPattern('email-worker-01.sendemail.*'));
        $this->assertSame('scheduled_jobs_queue.a.all', JobOptions::queueNameForPattern('a.#'));
        $this->assertMatchesRegularExpression('/^php-worker-[0-9a-f]{8}$/', (new WorkerOptions('php-worker'))->instanceId);
    }

    public function testParsesOccurrenceIds(): void
    {
        $this->assertSame(self::GUID, Processor::parseOccurrenceId(['OccurrenceId' => self::GUID], null));
        $this->assertSame(self::GUID, Processor::parseOccurrenceId(['CorrelationId' => self::GUID], null));
        $this->assertSame(self::GUID, Processor::parseOccurrenceId([], self::GUID));
        $this->assertSame(self::GUID, Processor::parseOccurrenceId(['OccurrenceId' => 'not-a-guid'], self::GUID));
        $this->assertNull(Processor::parseOccurrenceId([], null));
    }

    public function testParsesRetryCounts(): void
    {
        $this->assertSame(2, Processor::parseRetryCount(['x-retry-count' => 2]));
        $this->assertSame(4, Processor::parseRetryCount(['x-retry-count' => pack('V', 4)]));
        $this->assertSame(5, Processor::parseRetryCount(['x-retry-count' => '5']));
        $this->assertSame(0, Processor::parseRetryCount([]));
    }

    public function testFormatsErrorsWithPrevious(): void
    {
        $text = Errors::format(new \RuntimeException('outer', 0, new \InvalidArgumentException('inner')));

        $this->assertStringStartsWith("Type: RuntimeException\nMessage: outer\nStackTrace: ", $text);
        $this->assertStringContainsString("Inner Exception:\nType: InvalidArgumentException\nMessage: inner", $text);
    }

    public function testLogBufferKeepsOrderAfterFailedSend(): void
    {
        $sent = [];
        $fail = true;
        $buffer = new LogBuffer(function (string $batch) use (&$sent, &$fail): void {
            if ($fail) {
                $fail = false;

                throw new \RuntimeException('broker down');
            }

            foreach (json_decode($batch, true)['logs'] as $entry) {
                $sent[] = $entry['log']['message'];
            }
        }, new NullLogger(), batchSize: 1000);

        $buffer->add('o', 'i', ['message' => '1']);
        $buffer->add('o', 'i', ['message' => '2']);

        try {
            $buffer->flush();
            $this->fail('the first flush should fail');
        } catch (\RuntimeException) {
        }

        $buffer->add('o', 'i', ['message' => '3']);
        $buffer->flush();

        $this->assertSame(['1', '2', '3'], $sent);
    }

    public function testLogBufferDropsOldestBeyondLimit(): void
    {
        $sent = [];
        $buffer = new LogBuffer(function (string $batch) use (&$sent): void {
            foreach (json_decode($batch, true)['logs'] as $entry) {
                $sent[] = $entry['log']['message'];
            }
        }, new NullLogger(), batchSize: 1000, maxBuffered: 2);

        foreach (['1', '2', '3'] as $message) {
            $buffer->add('o', 'i', ['message' => $message]);
        }

        $buffer->flush();

        $this->assertSame(['2', '3'], $sent);
    }

    public function testAppliesDefaults(): void
    {
        $options = new WorkerOptions('report-worker');
        $this->assertSame('Milvaion:JobScheduler:cancellation_channel', $options->cancellationChannel());
        $this->assertSame(3600.0, $options->executionTimeout);

        $job = (new JobOptions())->resolve('SendEmailJob', fn () => null, $options);
        $this->assertSame(['report-worker.sendemail.*', 'sendemail-consumer', 3, 5.0], [$job->routingPattern, $job->consumerId, $job->maxRetries, $job->baseRetryDelay]);

        $this->expectException(\InvalidArgumentException::class);
        new WorkerOptions('w', queueType: 'lazy');
    }

    public function testReadsEnvironment(): void
    {
        $options = WorkerOptions::fromEnv([
            'MILVAION_WORKER_ID' => 'env-worker',
            'MILVAION_RABBITMQ_QUEUE_TYPE' => 'Quorum',
            'MILVAION_REDIS_URL' => 'redis://redis:6379',
            'MILVAION_REDIS_KEY_PREFIX' => 'X:',
        ]);

        $this->assertSame('env-worker', $options->workerId);
        $this->assertTrue($options->quorum());
        $this->assertSame('X:cancellation_channel', $options->cancellationChannel());
        $this->assertNull(WorkerOptions::fromEnv(['MILVAION_WORKER_ID' => 'w', 'MILVAION_REDIS_URL' => 'false'])->redisUrl);
        $this->assertSame('override', WorkerOptions::fromEnv(['MILVAION_WORKER_ID' => 'w'], workerId: 'override')->workerId);
    }

    public function testRejectsDuplicateJobNames(): void
    {
        $worker = new Worker(new WorkerOptions('w', redisUrl: null, logger: new NullLogger()));
        $worker->job('TestJob', fn () => null);

        $this->expectException(\InvalidArgumentException::class);
        $worker->job('testjob', fn () => null);
    }

    public function testRegistrationMetadataIsPascalCase(): void
    {
        $worker = new Worker(new WorkerOptions('w', redisUrl: null, logger: new NullLogger()));
        $worker->job('GreetJob', fn () => null, new JobOptions(dataSchema: ['type' => 'object']));

        $registration = $worker->buildRegistration();
        $metadata = json_decode($registration['metadata'], true);

        $this->assertSame(['GreetJob', 'greet-consumer'], [$metadata['JobConfigs'][0]['JobType'], $metadata['JobConfigs'][0]['ConsumerId']]);
        $this->assertSame('{"type":"object"}', $registration['jobDataDefinitions']->GreetJob);
        $this->assertSame('w.greet.*', $registration['routingPatterns']->GreetJob);
    }
}
