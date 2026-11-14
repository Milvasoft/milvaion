<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Tests;

use Milvasoft\Milvaion\Worker\AbortReason;
use Milvasoft\Milvaion\Worker\CancellationRegistry;
use Milvasoft\Milvaion\Worker\Delivery;
use Milvasoft\Milvaion\Worker\JobAbortedException;
use Milvasoft\Milvaion\Worker\JobContext;
use Milvasoft\Milvaion\Worker\JobOptions;
use Milvasoft\Milvaion\Worker\JobStatus;
use Milvasoft\Milvaion\Worker\LogBuffer;
use Milvasoft\Milvaion\Worker\PermanentJobException;
use Milvasoft\Milvaion\Worker\Processor;
use Milvasoft\Milvaion\Worker\Protocol;
use Milvasoft\Milvaion\Worker\Publisher;
use Milvasoft\Milvaion\Worker\ResolvedJob;
use Milvasoft\Milvaion\Worker\RetryAfterException;
use Milvasoft\Milvaion\Worker\RetryRequest;
use Milvasoft\Milvaion\Worker\WorkerOptions;
use PHPUnit\Framework\TestCase;
use Psr\Log\NullLogger;

final class FakePublisher implements Publisher
{
    /** @var list<array{exchange: string, key: string, body: array<string, mixed>, raw: string, correlationId: ?string, headers: ?array<string, mixed>}> */
    public array $messages = [];
    public int $failNext = 0;

    public function publish(string $exchange, string $routingKey, string $body, bool $persistent = true, ?string $correlationId = null, ?array $headers = null): void
    {
        if ($this->failNext > 0) {
            $this->failNext--;

            throw new \RuntimeException('publish failed');
        }

        $this->messages[] = ['exchange' => $exchange, 'key' => $routingKey, 'body' => json_decode($body, true), 'raw' => $body, 'correlationId' => $correlationId, 'headers' => $headers];
    }

    /** @return list<array<string, mixed>> */
    public function to(string $queue): array
    {
        return array_values(array_filter($this->messages, fn ($m) => $m['exchange'] === '' && $m['key'] === $queue));
    }

    /** @return list<int> */
    public function statuses(): array
    {
        return array_map(fn ($m) => $m['body']['status'], $this->to(Protocol::QUEUE_STATUS_UPDATES));
    }

    /** @return array<string, mixed> */
    public function lastStatus(): array
    {
        $all = $this->to(Protocol::QUEUE_STATUS_UPDATES);

        return end($all)['body'];
    }

    /** @return list<array<string, mixed>> */
    public function logs(): array
    {
        $logs = [];

        foreach ($this->to(Protocol::QUEUE_WORKER_LOGS) as $m) {
            foreach ($m['body']['logs'] as $entry) {
                $logs[] = $entry['log'];
            }
        }

        return $logs;
    }

    /** @return list<array<string, mixed>> */
    public function deadLettered(): array
    {
        return array_values(array_filter($this->messages, fn ($m) => $m['exchange'] === Protocol::EXCHANGE_DEAD_LETTER));
    }
}

final class ProcessorTest extends TestCase
{
    private const OCCURRENCE_ID = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';
    private const JOB_ID = '0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99';

    private FakePublisher $pub;
    private CancellationRegistry $cancellations;
    private Processor $processor;
    private ResolvedJob $consumer;
    /** @var list<float> */
    private array $sleeps = [];
    /** @var list<string> */
    private array $settled = [];

    /** @param array<string, callable> $handlers */
    private function setUpProcessor(array $handlers, ?JobOptions $jobOptions = null, bool $isolate = false, float $shutdownTimeout = 30): void
    {
        $options = new WorkerOptions(workerId: 'php-worker', redisUrl: null, jobHeartbeatInterval: 0, shutdownTimeout: $shutdownTimeout, instanceId: 'php-worker-abcd1234');
        $this->pub = new FakePublisher();
        $logs = new LogBuffer(fn (string $batch) => $this->pub->publish('', Protocol::QUEUE_WORKER_LOGS, $batch), new NullLogger());
        $this->cancellations = new CancellationRegistry();
        $jobs = [];

        foreach ($handlers as $name => $handler) {
            $jobs[strtolower($name)] = $this->consumer = ($jobOptions ?? new JobOptions())->resolve($name, \Closure::fromCallable($handler), $options);
        }

        $this->processor = new Processor($options, $jobs, $this->pub, $logs, $this->cancellations, new NullLogger());
        $this->processor->isolate = $isolate;
        $this->processor->sleep = function (float $seconds): bool {
            $this->sleeps[] = $seconds;

            return $this->processor->stopRequested();
        };
    }

    /**
     * @param array<string, mixed> $job
     * @param array<string, mixed> $headers
     */
    private function delivery(array $job = [], array $headers = [], bool $redelivered = false, ?string $body = null): Delivery
    {
        $this->settled = [];
        $body ??= Protocol::json(array_merge(['id' => self::JOB_ID, 'displayName' => 'Test', 'jobNameInWorker' => 'TestJob', 'jobData' => null], $job));

        return new Delivery(
            $body,
            array_merge(['OccurrenceId' => self::OCCURRENCE_ID], $headers),
            null,
            $redelivered,
            function (): void {
                $this->settled[] = 'ack';
            },
            function (bool $requeue): void {
                $this->settled[] = $requeue ? 'requeue' : 'nack';
            },
        );
    }

    private function handle(Delivery $delivery): void
    {
        $this->processor->handle($delivery, $this->consumer);
    }

    /** @return list<int> */
    private static function codes(JobStatus ...$statuses): array
    {
        return array_map(fn (JobStatus $s) => $s->value, $statuses);
    }

    public function testCompletesAndReportsResult(): void
    {
        $this->setUpProcessor(['TestJob' => function (JobContext $ctx) {
            $ctx->log('working', ['to' => $ctx->data()['to']]);

            return ['sent' => true];
        }]);

        $this->handle($this->delivery(['jobData' => '{"to":"a@b.c"}']));

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Completed), $this->pub->statuses());

        $final = $this->pub->lastStatus();
        $this->assertSame([self::OCCURRENCE_ID, self::JOB_ID, 'php-worker', 'php-worker-abcd1234'],
            [$final['occurrenceId'], $final['jobId'], $final['workerId'], $final['instanceId']]);
        $this->assertSame('{"sent":true}', $final['result']);
        $this->assertNotNull($final['startTime']);
        $this->assertNotNull($final['endTime']);
        $this->assertIsInt($final['durationMs']);

        $log = array_values(array_filter($this->pub->logs(), fn ($l) => $l['message'] === 'working'))[0];
        $this->assertSame(['Information', 'UserCode', ['to' => 'a@b.c']], [$log['level'], $log['category'], $log['data']]);
    }

    public function testDefaultResultMessage(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => null]);
        $this->handle($this->delivery());

        $this->assertSame('Job TestJob completed successfully', $this->pub->lastStatus()['result']);
    }

    public function testRetriesWithBackoff(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('boom')], new JobOptions(maxRetries: 3, baseRetryDelay: 5));
        $delivery = $this->delivery([], ['x-retry-count' => 1]);

        $this->handle($delivery);

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame([10.0], $this->sleeps);
        $this->assertSame(self::codes(JobStatus::Running), $this->pub->statuses());

        $retries = $this->pub->to($this->consumer->queue);
        $this->assertCount(1, $retries);
        $this->assertSame($delivery->body, $retries[0]['raw']);
        $this->assertSame(self::OCCURRENCE_ID, $retries[0]['correlationId']);
        $this->assertSame(['CorrelationId' => self::OCCURRENCE_ID, 'x-retry-count' => 2, 'MaxRetries' => 3], $retries[0]['headers']);
        $this->assertNotEmpty(array_filter($this->pub->logs(), fn ($l) => $l['category'] === 'Retry' && str_contains($l['message'], 'attempt 2/3')));
        $this->assertSame([], $this->pub->deadLettered());
    }

    public function testDeadLettersAfterRetries(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('still broken')], new JobOptions(maxRetries: 3));

        $this->handle($this->delivery([], ['x-retry-count' => 3]));

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Failed), $this->pub->statuses());
        $this->assertStringStartsWith("Max retries (3) exceeded. Original exception: Type: RuntimeException\nMessage: still broken", $this->pub->lastStatus()['exception']);

        $dlq = $this->pub->deadLettered();
        $this->assertCount(1, $dlq);
        $this->assertSame(Protocol::DEAD_LETTER_ROUTING_KEY, $dlq[0]['key']);
        $this->assertSame([self::JOB_ID, JobStatus::Failed->value], [$dlq[0]['body']['id'], $dlq[0]['body']['status']]);
        $this->assertSame(['CorrelationId' => self::OCCURRENCE_ID, 'x-retry-count' => 3, 'MaxRetries' => 3], $dlq[0]['headers']);
    }

    public function testPermanentFailureSkipsRetries(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new PermanentJobException('bad input')]);

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame([], $this->sleeps);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Failed), $this->pub->statuses());

        $dlq = $this->pub->deadLettered();
        $this->assertStringStartsWith('[PERMANENT FAILURE] Type: Milvasoft\Milvaion\Worker\PermanentJobException', $dlq[0]['body']['exception']);
        $this->assertSame(0, $dlq[0]['headers']['MaxRetries']);
    }

    public function testRetryAfterEndsAsFailedWithoutRetryOrDeadLetter(): void
    {
        $at = new \DateTimeImmutable('2026-11-15T11:00:00+03:00');
        $this->setUpProcessor(['TestJob' => fn () => throw new RetryAfterException($at, 'YouTube quota exhausted')], new JobOptions(maxRetries: 3));

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame([], $this->sleeps);
        $this->assertSame([], $this->pub->to($this->consumer->queue));
        $this->assertSame([], $this->pub->deadLettered());
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Failed), $this->pub->statuses());

        $final = $this->pub->lastStatus();
        $this->assertSame('2026-11-15T08:00:00.000Z', $final['retryAfter']);
        $this->assertStringStartsWith('[RETRY AFTER 2026-11-15T08:00:00.000Z] Type: Milvasoft\Milvaion\Worker\RetryAfterException', $final['exception']);
        $this->assertStringContainsString('YouTube quota exhausted', $final['exception']);
        $this->assertNotNull($final['startTime']);
        $this->assertNotNull($final['endTime']);
        $this->assertIsInt($final['durationMs']);

        $log = array_values(array_filter($this->pub->logs(), fn ($l) => $l['category'] === 'Retry'));
        $this->assertCount(1, $log);
        $this->assertSame('Warning', $log[0]['level']);
        $this->assertStringStartsWith('⏰ Job asked to be retried at 2026-11-15T08:00:00.000Z.', $log[0]['message']);
        $this->assertSame(['RetryAfter' => '2026-11-15T08:00:00.000Z'], $log[0]['data']);
    }

    public function testOtherFailuresOmitRetryAfter(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new PermanentJobException('bad input')]);
        $this->handle($this->delivery());

        $this->assertArrayNotHasKey('retryAfter', $this->pub->lastStatus());
    }

    public function testWrappedRetryAfterIsAnOrdinaryFailure(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('outer', 0, new RetryAfterException(new \DateTimeImmutable('+1 hour')))], new JobOptions(maxRetries: 3));
        $this->handle($this->delivery());

        $this->assertSame([5.0], $this->sleeps);
        $this->assertCount(1, $this->pub->to($this->consumer->queue));
    }

    public function testRetryAtIsConvertedToUtcAndHasADefaultMessage(): void
    {
        $e = new RetryAfterException(new \DateTime('2026-11-15 11:00:00', new \DateTimeZone('Europe/Istanbul')));

        $this->assertSame('UTC', $e->getRetryAt()->getTimezone()->getName());
        $this->assertSame('2026-11-15T08:00:00+00:00', $e->getRetryAt()->format(DATE_ATOM));
        $this->assertSame('Job asked to be retried at 2026-11-15T08:00:00.000Z.', $e->getMessage());
    }

    public function testInvalidJobDataIsPermanent(): void
    {
        $this->setUpProcessor(['TestJob' => fn (JobContext $ctx) => $ctx->data()]);
        $this->handle($this->delivery(['jobData' => '{not json']));

        $this->assertStringContainsString('Job data is not valid JSON', $this->pub->deadLettered()[0]['body']['exception']);
    }

    public function testTimesOutAJobThatChecksItsContext(): void
    {
        $this->setUpProcessor(['TestJob' => function (JobContext $ctx) {
            while (true) {
                usleep(20_000);
                $ctx->throwIfAborted();
            }
        }]);

        $this->handle($this->delivery(['executionTimeoutSeconds' => 0.6]));

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::TimedOut), $this->pub->statuses());
        $this->assertStringContainsString('exceeded maximum timeout of 0.6 seconds', $this->pub->lastStatus()['exception']);
    }

    public function testReportsALateFinishAsTimedOut(): void
    {
        $this->setUpProcessor(['TestJob' => function () {
            usleep(300_000);

            return 'too late';
        }]);

        $this->handle($this->delivery(['executionTimeoutSeconds' => 0.1]));

        $this->assertSame(self::codes(JobStatus::Running, JobStatus::TimedOut), $this->pub->statuses());
    }

    public function testCancelsRunningJob(): void
    {
        $calls = 0;
        $this->setUpProcessor(['TestJob' => function (JobContext $ctx) {
            while (!$ctx->isAborted()) {
                usleep(20_000);
            }

            return 'never reported';
        }]);
        $this->processor->housekeeping = function () use (&$calls): void {
            if (++$calls === 2) {
                $this->cancellations->cancel(strtoupper(self::OCCURRENCE_ID), 'Cancelled by user');
            }
        };

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Cancelled), $this->pub->statuses());
    }

    public function testShutdownReturnsJobToQueue(): void
    {
        $this->setUpProcessor(['TestJob' => function (JobContext $ctx) {
            $this->cancellations->abortAll(AbortReason::Shutdown, 'stopping');
            $ctx->throwIfAborted();
        }]);

        $this->handle($this->delivery());

        $this->assertSame(['requeue'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running), $this->pub->statuses());
    }

    public function testShutdownDuringRetryDelayRequeues(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('boom')]);
        $this->processor->requestStop();

        $this->handle($this->delivery());

        $this->assertSame(['requeue'], $this->settled);
        $this->assertSame([], $this->pub->to($this->consumer->queue));
    }

    public function testUnknownJobIsDeadLettered(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => 'ok']);
        $this->handle($this->delivery(['jobNameInWorker' => 'OtherJob']));

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Failed), $this->pub->statuses());
        $this->assertStringStartsWith('[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob', $this->pub->deadLettered()[0]['body']['exception']);
    }

    public function testJobNamesAreCaseInsensitive(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => 'done']);
        $this->handle($this->delivery(['jobNameInWorker' => 'testjob']));

        $this->assertSame('done', $this->pub->lastStatus()['result']);
    }

    public function testRejectsBodyThatIsNotAJob(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => 'ok']);
        $this->handle($this->delivery(body: 'not json'));

        $this->assertSame(['nack'], $this->settled);
        $this->assertSame([], $this->pub->messages);
    }

    public function testReportsRedelivery(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => 'ok']);
        $this->handle($this->delivery(redelivered: true));

        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Running, JobStatus::Completed), $this->pub->statuses());
        $this->assertNotEmpty(array_filter($this->pub->logs(), fn ($l) => $l['category'] === 'Redelivery'));
    }

    public function testRetriesRefusedFinalStatus(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => 'ok']);
        $this->pub->failNext = 1;
        $this->handle($this->delivery());

        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Completed), $this->pub->statuses());
    }

    public function testJobAbortedExceptionFromTheJobItself(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new JobAbortedException(AbortReason::Cancelled, 'stop')]);
        $this->handle($this->delivery());

        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Cancelled), $this->pub->statuses());
    }

    private function requirePcntl(): void
    {
        if (!function_exists('pcntl_fork') || !function_exists('posix_kill')) {
            $this->markTestSkipped('Needs ext-pcntl and ext-posix.');
        }
    }

    public function testIsolatedJobCompletesWithLogs(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => function (JobContext $ctx) {
            $ctx->log('from the job process', ['pid' => getmypid()]);

            return ['parentPid' => getmypid()];
        }], isolate: true);

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Completed), $this->pub->statuses());
        $this->assertNotSame(getmypid(), json_decode($this->pub->lastStatus()['result'], true)['parentPid'], 'the job runs in another process');
        $this->assertNotEmpty(array_filter($this->pub->logs(), fn ($l) => $l['message'] === 'from the job process'));
    }

    public function testIsolatedJobIsKilledOnTimeout(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => function () {
            sleep(60);

            return 'never';
        }], isolate: true);

        $started = microtime(true);
        $this->handle($this->delivery(['executionTimeoutSeconds' => 1]));

        $this->assertLessThan(5, microtime(true) - $started);
        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::TimedOut), $this->pub->statuses());
    }

    public function testIsolatedJobIsKilledOnCancel(): void
    {
        $this->requirePcntl();
        $calls = 0;
        $this->setUpProcessor(['TestJob' => function () {
            sleep(60);

            return 'never';
        }], isolate: true);
        $this->processor->housekeeping = function () use (&$calls): void {
            if (++$calls === 2) {
                $this->cancellations->cancel(self::OCCURRENCE_ID, 'Cancelled by user');
            }
        };

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Cancelled), $this->pub->statuses());
    }

    public function testIsolatedPermanentFailureKeepsItsType(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => fn () => throw new PermanentJobException('bad input', 0, new \LogicException('cause'))], isolate: true);

        $this->handle($this->delivery());

        $dlq = $this->pub->deadLettered();
        $this->assertCount(1, $dlq);
        $this->assertStringStartsWith('[PERMANENT FAILURE] Type: Milvasoft\Milvaion\Worker\PermanentJobException', $dlq[0]['body']['exception']);
        $this->assertStringContainsString('Inner Exception:', $dlq[0]['body']['exception']);
        $this->assertSame([], $this->sleeps);
    }

    public function testIsolatedRetryAfterKeepsItsTime(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => fn () => throw new RetryAfterException(new \DateTimeImmutable('2026-11-15T11:00:00Z'), 'quota')], isolate: true);

        $this->handle($this->delivery());

        $this->assertSame(['ack'], $this->settled);
        $this->assertSame([], $this->pub->deadLettered());
        $this->assertSame([], $this->sleeps);
        $final = $this->pub->lastStatus();
        $this->assertSame('2026-11-15T11:00:00.000Z', $final['retryAfter']);
        $this->assertStringStartsWith('[RETRY AFTER 2026-11-15T11:00:00.000Z] Type: Milvasoft\Milvaion\Worker\RetryAfterException', $final['exception']);
    }

    public function testIsolatedJobThatExitsIsAFailure(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => function () {
            exit(3);
        }], new JobOptions(maxRetries: 0), isolate: true);

        $this->handle($this->delivery());

        $this->assertSame(self::codes(JobStatus::Running, JobStatus::Failed), $this->pub->statuses());
        $this->assertStringContainsString('The job process exited with code 3', $this->pub->lastStatus()['exception']);
    }

    /** A delivery as the Kafka transport makes it: retries and rejects go through the broker. */
    private function kafkaDelivery(array $headers = [], ?string $body = null, bool $retryFails = false): Delivery
    {
        $plain = $this->delivery([], $headers, false, $body);

        return new Delivery($plain->body, $plain->headers, null, false, $plain->ack, $plain->nack,
            function (string $reason): void {
                $this->settled[] = "reject: {$reason}";
            },
            function (RetryRequest $request) use ($retryFails): void {
                if ($retryFails) {
                    throw new \RuntimeException('broker down');
                }

                $this->settled[] = $request;
            },
            'milvaion.jobs.php-worker');
    }

    public function testKafkaRetryIsHandedToTheBrokerWithoutWaiting(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('boom')], new JobOptions(maxRetries: 3, baseRetryDelay: 5));
        $before = microtime(true);

        $this->processor->handle($this->kafkaDelivery(['x-retry-count' => '1']));

        $this->assertSame([], $this->sleeps);
        $this->assertCount(2, $this->settled);
        [$request, $ack] = $this->settled;
        $this->assertInstanceOf(RetryRequest::class, $request);
        $this->assertSame('ack', $ack);
        $this->assertSame([self::JOB_ID, self::OCCURRENCE_ID, 2, 3], [$request->jobId, $request->occurrenceId, $request->retryCount, $request->maxRetries]);
        $this->assertEqualsWithDelta($before + 10, $request->notBefore, 1.0);
        $this->assertSame(self::codes(JobStatus::Running), $this->pub->statuses());
        $this->assertSame([], $this->pub->to($this->consumer->queue));
    }

    public function testKafkaRetryThatCannotBeProducedIsRejected(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => throw new \RuntimeException('boom')], new JobOptions(maxRetries: 3));

        $this->processor->handle($this->kafkaDelivery(retryFails: true));

        $this->assertSame(['reject: the job could not be scheduled for retry: broker down'], $this->settled);
    }

    public function testKafkaInvalidRecordIsRejectedWithReason(): void
    {
        $this->setUpProcessor(['TestJob' => fn () => null]);

        $this->processor->handle($this->kafkaDelivery([], 'not json'));
        $this->assertSame(['reject: the message is not a valid job'], $this->settled);

        $this->processor->handle($this->kafkaDelivery(['OccurrenceId' => 'nope']));
        $this->assertSame(['reject: the message has no occurrence id'], $this->settled);
    }

    public function testHeartbeatsWhileWaitingForTheNextAttempt(): void
    {
        $options = new WorkerOptions(workerId: 'php-worker', redisUrl: null, jobHeartbeatInterval: 0.05, instanceId: 'php-worker-abcd1234');
        $pub = new FakePublisher();
        $logs = new LogBuffer(fn (string $batch) => $pub->publish('', Protocol::QUEUE_WORKER_LOGS, $batch), new NullLogger());
        $processor = new Processor($options, [], $pub, $logs, new CancellationRegistry(), new NullLogger());
        $run = $processor->newRun(['id' => self::JOB_ID, 'jobNameInWorker' => 'TestJob'], self::OCCURRENCE_ID, 1);

        $processor->heartbeatUntil($run, microtime(true) + 0.3);

        for ($i = 0; $i < 10; $i++) {
            usleep(60_000);
            $processor->heartbeatWaiting();
        }

        $beats = count($pub->to(Protocol::QUEUE_STATUS_UPDATES));
        $this->assertGreaterThanOrEqual(2, $beats);
        $this->assertLessThanOrEqual(6, $beats);
        $this->assertSame(JobStatus::Running->value, $pub->lastStatus()['status']);

        $processor->heartbeatWaiting();
        $this->assertCount($beats, $pub->to(Protocol::QUEUE_STATUS_UPDATES), 'no heartbeats once the attempt is due');
    }

    public function testRetryDelayDoubles(): void
    {
        $this->assertSame([5.0, 10.0, 20.0, 40.0], array_map(fn (int $n) => Processor::retryDelay(5, $n), [0, 1, 2, 3]));
    }

    public function testIsolatedJobIsReturnedToTheQueueOnShutdown(): void
    {
        $this->requirePcntl();
        $this->setUpProcessor(['TestJob' => function () {
            sleep(60);
        }], isolate: true, shutdownTimeout: 0.2);
        $this->processor->housekeeping = fn () => $this->processor->requestStop();

        $this->handle($this->delivery());

        $this->assertSame(['requeue'], $this->settled);
        $this->assertSame(self::codes(JobStatus::Running), $this->pub->statuses());
    }
}
