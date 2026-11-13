<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\LoggerInterface;

/**
 * Runs job deliveries and reports their progress to the API, following the worker protocol.
 *
 * PHP runs one job at a time. With ext-pcntl the job runs in a forked child, and this process keeps
 * the heartbeats going, watches for cancellation and the timeout, and kills the child to stop it.
 * Without it, the job runs in this process and tick() does that work whenever the job calls into
 * its context.
 *
 * @internal
 */
final class Processor
{
    private const GUID = '/^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i';

    /** Runs connection-level housekeeping (Redis polling, AMQP heartbeats). */
    public ?\Closure $housekeeping = null;
    /** Sleeps for the given seconds; returns true if the worker started stopping meanwhile. */
    public \Closure $sleep;
    /** Run each job in a forked child process (needs ext-pcntl and ext-posix). */
    public bool $isolate = false;

    private ?float $stopRequestedAt = null;
    private int $running = 0;
    private ?float $lastTick = null;
    private ?float $lastFlush = null;
    private bool $inTick = false;

    /** @var array{run: JobRun, abort: Abort, deadline: ?float, nextBeat: ?float}|null */
    private ?array $current = null;

    /** @param array<string, ResolvedJob> $jobs keyed by lowercase name */
    public function __construct(
        private readonly WorkerOptions $options,
        private array $jobs,
        private readonly Publisher $publisher,
        private readonly LogBuffer $logs,
        private readonly CancellationRegistry $cancellations,
        private readonly LoggerInterface $logger,
    ) {
        $this->sleep = function (float $seconds): bool {
            $until = microtime(true) + $seconds;

            while (($left = $until - microtime(true)) > 0 && $this->stopRequestedAt === null) {
                usleep((int) (min($left, 0.2) * 1_000_000));

                if ($this->housekeeping !== null) {
                    ($this->housekeeping)();
                }
            }

            return $this->stopRequestedAt !== null;
        };
    }

    /** @param array<string, ResolvedJob> $jobs keyed by lowercase name */
    public function setJobs(array $jobs): void
    {
        $this->jobs = $jobs;
    }

    public function running(): int
    {
        return $this->running;
    }

    public function requestStop(): void
    {
        $this->stopRequestedAt ??= microtime(true);
    }

    public function stopRequested(): bool
    {
        return $this->stopRequestedAt !== null;
    }

    public function handle(Delivery $delivery, ResolvedJob $consumer): void
    {
        $this->running++;

        try {
            $this->process($delivery, $consumer);
        } catch (\Throwable $e) {
            $this->logger->error('Unexpected error while processing a job delivery: ' . $e->getMessage());
            $this->settle(fn () => ($delivery->nack)(false));
        } finally {
            $this->running--;
        }
    }

    private function process(Delivery $delivery, ResolvedJob $consumer): void
    {
        $job = self::parseJob($delivery->body);

        if ($job === null) {
            $this->logger->error("Received a message on {$consumer->queue} that is not a valid job. Sending it to the dead letter queue.");
            $this->settle(fn () => ($delivery->nack)(false));

            return;
        }

        $occurrenceId = self::parseOccurrenceId($delivery->headers, $delivery->correlationId);

        if ($occurrenceId === null) {
            $this->logger->error("Job {$job['id']} arrived without an occurrence id. Sending it to the dead letter queue.");
            $this->settle(fn () => ($delivery->nack)(false));

            return;
        }

        $run = new JobRun($job, $occurrenceId, self::parseRetryCount($delivery->headers), $this->options, $this->publisher, $this->logs, $this->logger);

        if ($delivery->redelivered) {
            $this->logger->warning("Job {$run->jobId()} (occurrence {$occurrenceId}) was redelivered; the previous attempt may have crashed.");
            $run->status(JobStatus::Running, start: microtime(true));
            $run->log('Warning', '⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.',
                ['Reason' => 'RabbitMQ redelivery detected', 'WorkerId' => $this->options->instanceId], 'Redelivery');
        }

        $abort = new Abort();
        $this->cancellations->register($occurrenceId, $abort);

        try {
            $this->execute($delivery, $consumer, $run, $abort);
        } finally {
            $this->cancellations->unregister($occurrenceId);
        }
    }

    private function execute(Delivery $delivery, ResolvedJob $consumer, JobRun $run, Abort $abort): void
    {
        $start = microtime(true);

        $run->status(JobStatus::Running, start: $start);
        $run->flushLogs();

        $registered = $this->jobs[strtolower($run->jobName())] ?? null;

        if ($registered === null) {
            $exception = 'No IJob implementation registered for type: ' . $run->jobName();
            $this->logger->error($exception);
            $run->status(JobStatus::Failed, end: microtime(true), exception: $exception);
            $this->deadLetter($delivery, $run, "[PERMANENT FAILURE] {$exception}", $run->retryCount, 0);

            return;
        }

        $timeout = isset($run->job['executionTimeoutSeconds']) && is_numeric($run->job['executionTimeoutSeconds'])
            ? (float) $run->job['executionTimeoutSeconds']
            : $registered->executionTimeout;

        [$result, $error, $reason, $message] = $this->runHandler($registered, $run, $abort, $timeout, $start);
        $end = microtime(true);

        if ($reason !== null) {
            $this->finishAborted($delivery, $run, $reason, $message, $start, $end, $timeout);
        } elseif ($error !== null) {
            $this->finishFailed($delivery, $consumer, $registered, $run, $error, $start, $end);
        } else {
            $run->flushLogs();
            $run->status(JobStatus::Completed, start: $start, end: $end, result: $result);
            $this->settle(fn () => ($delivery->ack)());
        }
    }

    /** @return array{0: ?string, 1: ?\Throwable, 2: ?AbortReason, 3: string} */
    private function runHandler(ResolvedJob $registered, JobRun $run, Abort $abort, float $timeout, float $start): array
    {
        $interval = $this->options->jobHeartbeatInterval;

        $this->current = [
            'run' => $run,
            'abort' => $abort,
            'deadline' => $timeout > 0 ? $start + $timeout : null,
            'nextBeat' => $interval > 0 ? $start + $interval : null,
        ];

        try {
            $outcome = $this->isolate ? $this->runIsolated($registered, $run, $abort) : $this->runInProcess($registered, $run, $abort);
        } finally {
            $this->current = null;
        }

        // A job that stopped on its own after being aborted (it checked isAborted) is reported as aborted.
        if ($outcome[2] === null && $abort->reason !== null) {
            $outcome = [null, null, $abort->reason, $abort->message];
        }

        // A job that cannot be interrupted is still reported as timed out when it finishes late.
        if ($outcome[2] === null && $timeout > 0 && microtime(true) - $start > $timeout) {
            $abort->trigger(AbortReason::TimedOut, 'Job execution exceeded timeout limit of ' . self::seconds($timeout) . ' seconds');
            $outcome = [null, null, AbortReason::TimedOut, $abort->message];
        }

        return $outcome;
    }

    /**
     * Runs the handler in this process. It cannot be interrupted; cancellation and timeouts take
     * effect when it calls into its context.
     *
     * @return array{0: ?string, 1: ?\Throwable, 2: ?AbortReason, 3: string}
     */
    private function runInProcess(ResolvedJob $registered, JobRun $run, Abort $abort): array
    {
        $context = new JobContext(
            $run->occurrenceId,
            $run->job,
            $run->retryCount,
            $this->options->instanceId,
            $abort,
            fn (array $log) => $this->logs->add($run->occurrenceId, $this->options->instanceId, $log),
            fn () => $this->tick(),
        );

        try {
            return [self::serializeResult(($registered->handler)($context), $run->jobName()), null, null, ''];
        } catch (JobAbortedException $e) {
            return [null, null, $e->reason, $e->getMessage()];
        } catch (\Throwable $e) {
            return $abort->reason !== null ? [null, null, $abort->reason, $abort->message] : [null, $e, null, ''];
        }
    }

    /**
     * Runs the handler in a forked child process, so this process stays free to send heartbeats,
     * watch for cancellation and enforce the timeout, and can stop the job by killing the child.
     * The child sends its logs and its outcome back over a socket pair.
     *
     * @return array{0: ?string, 1: ?\Throwable, 2: ?AbortReason, 3: string}
     */
    private function runIsolated(ResolvedJob $registered, JobRun $run, Abort $abort): array
    {
        $pair = stream_socket_pair(STREAM_PF_UNIX, STREAM_SOCK_STREAM, STREAM_IPPROTO_IP);

        if ($pair === false) {
            return [null, new \RuntimeException('Could not create a socket pair for the job process.'), null, ''];
        }

        $pid = pcntl_fork();

        if ($pid === -1) {
            return [null, new \RuntimeException('Could not fork a process for the job.'), null, ''];
        }

        if ($pid === 0) {
            fclose($pair[0]);
            $this->runChild($registered, $run, $pair[1]);
        }

        fclose($pair[1]);
        stream_set_blocking($pair[0], false);

        $buffer = '';
        $outcome = null;
        $exited = false;

        while (true) {
            $chunk = fread($pair[0], 65536);

            if (is_string($chunk) && $chunk !== '') {
                $buffer .= $chunk;

                while (($newline = strpos($buffer, "\n")) !== false) {
                    $message = json_decode(substr($buffer, 0, $newline), true);
                    $buffer = substr($buffer, $newline + 1);

                    if (isset($message['log'])) {
                        $this->logs->add($run->occurrenceId, $this->options->instanceId, $message['log']);
                    } elseif (is_array($message)) {
                        $outcome = $message;
                    }
                }
            }

            if ($outcome !== null) {
                break;
            }

            if ($exited) {
                break;
            }

            if (pcntl_waitpid($pid, $status, WNOHANG) !== 0) {
                // Read what the child wrote before it went away, then stop.
                $exited = true;

                continue;
            }

            $this->tick();

            if ($abort->reason !== null) {
                posix_kill($pid, SIGKILL);
                pcntl_waitpid($pid, $status);
                fclose($pair[0]);

                return [null, null, $abort->reason, $abort->message];
            }

            usleep(20_000);
        }

        if (!$exited) {
            pcntl_waitpid($pid, $status);
        }

        fclose($pair[0]);

        if ($outcome === null) {
            $how = pcntl_wifsignaled($status) ? 'was killed by signal ' . pcntl_wtermsig($status) : 'exited with code ' . pcntl_wexitstatus($status);

            return [null, new \RuntimeException("The job process {$how} before reporting a result."), null, ''];
        }

        return match (true) {
            isset($outcome['abort']) => [null, null, AbortReason::from($outcome['abort']), (string) ($outcome['message'] ?? '')],
            isset($outcome['error']) => [null, RemoteJobException::fromArray($outcome['error']), null, ''],
            default => [(string) $outcome['result'], null, null, ''],
        };
    }

    /**
     * The job process: runs the handler, reports logs and the outcome, and ends without running
     * the parent's destructors, which would close connections the parent still uses.
     *
     * @param resource $socket
     */
    private function runChild(ResolvedJob $registered, JobRun $run, $socket): never
    {
        // The worker decides when a job stops; Ctrl+C reaching the whole process group must not kill it.
        pcntl_signal(SIGINT, SIG_IGN);
        pcntl_signal(SIGTERM, SIG_DFL);

        $write = static function (array $message) use ($socket): void {
            $line = Protocol::json($message) . "\n";

            for ($written = 0; $written < strlen($line);) {
                $result = fwrite($socket, substr($line, $written));

                if ($result === false || $result === 0) {
                    return;
                }

                $written += $result;
            }
        };

        $context = new JobContext(
            $run->occurrenceId,
            $run->job,
            $run->retryCount,
            $this->options->instanceId,
            new Abort(),
            static fn (array $log) => $write(['log' => $log]),
            static fn () => null,
        );

        try {
            $write(['result' => self::serializeResult(($registered->handler)($context), $run->jobName())]);
        } catch (JobAbortedException $e) {
            $write(['abort' => $e->reason->value, 'message' => $e->getMessage()]);
        } catch (\Throwable $e) {
            $write(['error' => RemoteJobException::toArray($e)]);
        }

        fflush($socket);
        posix_kill(posix_getpid(), SIGKILL);

        exit(0); // not reached
    }

    /** Housekeeping while a job runs: cancellation, timeout, shutdown, job heartbeats and log flushing. */
    public function tick(): void
    {
        $current = $this->current;

        if ($current === null || $this->inTick) {
            return;
        }

        $this->inTick = true;

        try {
            $this->runTick($current);
        } finally {
            $this->inTick = false;
        }
    }

    public function hasRunningJob(): bool
    {
        return $this->current !== null;
    }

    /** @param array{run: JobRun, abort: Abort, deadline: ?float, nextBeat: ?float} $current */
    private function runTick(array $current): void
    {
        $now = microtime(true);
        $abort = $current['abort'];

        if ($this->lastTick === null || $now - $this->lastTick >= 0.5) {
            $this->lastTick = $now;

            if ($this->housekeeping !== null) {
                try {
                    ($this->housekeeping)();
                } catch (\Throwable $e) {
                    $this->logger->debug('Housekeeping failed: ' . $e->getMessage());
                }
            }

            if ($current['deadline'] !== null && $now >= $current['deadline']) {
                $abort->trigger(AbortReason::TimedOut, 'Job execution exceeded its timeout');
            }

            if ($this->stopRequestedAt !== null && $now - $this->stopRequestedAt >= $this->options->shutdownTimeout) {
                $abort->trigger(AbortReason::Shutdown, 'Worker is shutting down.');
            }

            if ($current['nextBeat'] !== null && $now >= $current['nextBeat'] && $abort->reason === null) {
                $this->current['nextBeat'] = $now + $this->options->jobHeartbeatInterval;
                $current['run']->status(JobStatus::Running, retry: false);
            }

            if ($this->logs->pending() > 0 && ($this->lastFlush === null || $now - $this->lastFlush >= 0.5)) {
                $this->lastFlush = $now;
                $current['run']->flushLogs();
            }
        }
    }

    private function finishAborted(Delivery $delivery, JobRun $run, AbortReason $reason, string $message, float $start, float $end, float $timeout): void
    {
        if ($reason === AbortReason::Shutdown) {
            // Nobody cancelled this job; it goes back to the queue and runs again.
            $this->logger->warning("Job {$run->jobId()} (occurrence {$run->occurrenceId}) was interrupted by worker shutdown. Returning it to the queue.");
            $run->flushLogs();
            $this->settle(fn () => ($delivery->nack)(true));

            return;
        }

        if ($reason === AbortReason::TimedOut) {
            $seconds = self::seconds($timeout);
            $run->log('Error', "Job execution exceeded timeout limit of {$seconds} seconds", null, 'UserDefined');
            $run->flushLogs();
            $elapsed = (int) round(($end - $start) * 1000);
            $run->status(JobStatus::TimedOut, start: $start, end: $end,
                exception: "Job execution exceeded maximum timeout of {$seconds} seconds ({$elapsed}ms elapsed)");
        } else {
            $run->log('Warning', 'Job execution was cancelled', ['Reason' => $message]);
            $run->flushLogs();
            $run->status(JobStatus::Cancelled, start: $start, end: $end, result: 'Job cancelled by user or system');
        }

        $this->settle(fn () => ($delivery->ack)());
    }

    private function finishFailed(Delivery $delivery, ResolvedJob $consumer, ResolvedJob $registered, JobRun $run, \Throwable $error, float $start, float $end): void
    {
        $exception = Errors::format($error);

        if ($error instanceof PermanentJobException) {
            $run->logError('Permanent job failure (will not retry): ' . $error->getMessage(), $error);
            $run->log('Error', '❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).',
                ['FailureType' => 'Permanent', 'FinalStatus' => 'MovedToDLQ'], 'Retry');
            $run->flushLogs();
            $run->status(JobStatus::Failed, start: $start, end: $end, exception: $exception);
            $this->deadLetter($delivery, $run, "[PERMANENT FAILURE] {$exception}", $run->retryCount, 0);

            return;
        }

        $run->logError('Job execution failed: ' . $error->getMessage(), $error);

        if ($run->retryCount < $registered->maxRetries) {
            $delay = $registered->baseRetryDelay * (2 ** $run->retryCount);
            $attempt = $run->retryCount + 1;

            $run->log('Warning', "⚠️ Job failed (attempt {$attempt}/{$registered->maxRetries}). Retrying in " . self::seconds($delay) . 's...', [
                'RetryAttempt' => $attempt,
                'MaxRetries' => $registered->maxRetries,
                'DelaySeconds' => $delay,
                'NextRetryTime' => Protocol::time(microtime(true) + $delay),
            ], 'Retry');
            $run->flushLogs();
            $this->retry($delivery, $consumer, $run, $delay, $registered->maxRetries);

            return;
        }

        $final = "Max retries ({$registered->maxRetries}) exceeded. Original exception: {$exception}";

        $run->log('Error', "❌ Job failed after {$registered->maxRetries} retries. Moving to Dead Letter Queue (DLQ).",
            ['TotalRetries' => $registered->maxRetries, 'FinalStatus' => 'MovedToDLQ'], 'Retry');
        $run->flushLogs();
        $run->status(JobStatus::Failed, start: $start, end: microtime(true), exception: $final);
        $this->deadLetter($delivery, $run, $final, $registered->maxRetries, $registered->maxRetries);
    }

    /**
     * Waits out the backoff, republishes the job with a higher retry count and acks the original.
     * The original stays unacked meanwhile, so a crash during the wait gets it redelivered.
     */
    private function retry(Delivery $delivery, ResolvedJob $consumer, JobRun $run, float $delay, int $maxRetries): void
    {
        $interval = $this->options->jobHeartbeatInterval;
        $remaining = $delay;

        while ($remaining > 0) {
            $step = $interval > 0 ? min($remaining, $interval) : $remaining;

            if ($this->stopRequestedAt !== null || ($this->sleep)($step)) {
                $this->logger->warning("Worker is stopping during the retry delay of job {$run->jobId()}. Returning it to the queue.");
                $this->settle(fn () => ($delivery->nack)(true));

                return;
            }

            $remaining -= $step;

            if ($remaining > 0 && $interval > 0) {
                $run->status(JobStatus::Running, retry: false);
            }
        }

        try {
            $this->publisher->publish('', $consumer->queue, $delivery->body, true, $run->occurrenceId,
                ['CorrelationId' => $run->occurrenceId, 'x-retry-count' => $run->retryCount + 1, 'MaxRetries' => $maxRetries]);
        } catch (\Throwable $e) {
            $this->logger->error("Failed to republish job {$run->jobId()} for retry. Dead-lettering the message: {$e->getMessage()}");
            $this->settle(fn () => ($delivery->nack)(false));

            return;
        }

        $this->settle(fn () => ($delivery->ack)());
    }

    private function deadLetter(Delivery $delivery, JobRun $run, string $exception, int $retryCount, int $maxRetries): void
    {
        $body = Protocol::json([
            'id' => $run->jobId(),
            'displayName' => $run->job['displayName'] ?? null,
            'jobNameInWorker' => $run->jobName(),
            'jobData' => $run->job['jobData'] ?? null,
            'executeAt' => $run->job['executeAt'] ?? null,
            'status' => JobStatus::Failed->value,
            'exception' => $exception,
        ]);

        try {
            $this->publisher->publish(Protocol::EXCHANGE_DEAD_LETTER, Protocol::DEAD_LETTER_ROUTING_KEY, $body, true, $run->occurrenceId,
                ['CorrelationId' => $run->occurrenceId, 'x-retry-count' => $retryCount, 'MaxRetries' => $maxRetries]);
        } catch (\Throwable $e) {
            // The queue dead-letters a rejected message itself, just without the exception details.
            $this->logger->error("Failed to publish job {$run->jobId()} to the dead letter queue. Rejecting the message instead: {$e->getMessage()}");
            $this->settle(fn () => ($delivery->nack)(false));

            return;
        }

        $this->settle(fn () => ($delivery->ack)());
    }

    private function settle(\Closure $action): void
    {
        try {
            $action();
        } catch (\Throwable $e) {
            // An ack or nack on a channel that has closed: the broker redelivers the message.
            $this->logger->warning('Could not settle a message; the broker will redeliver it: ' . $e->getMessage());
        }
    }

    /** @return array<string, mixed>|null */
    public static function parseJob(string $body): ?array
    {
        try {
            $job = json_decode($body, true, 512, JSON_THROW_ON_ERROR);
        } catch (\JsonException) {
            return null;
        }

        if (!is_array($job) || !is_string($job['id'] ?? null) || !is_string($job['jobNameInWorker'] ?? null)) {
            return null;
        }

        return $job;
    }

    /**
     * Occurrence id from the OccurrenceId header, the CorrelationId header or the correlation id property.
     *
     * @param array<string, mixed> $headers
     */
    public static function parseOccurrenceId(array $headers, ?string $correlationId): ?string
    {
        foreach ([$headers['OccurrenceId'] ?? null, $headers['CorrelationId'] ?? null, $correlationId] as $candidate) {
            if (is_string($candidate) && preg_match(self::GUID, trim($candidate))) {
                return trim($candidate);
            }
        }

        return null;
    }

    /** @param array<string, mixed> $headers */
    public static function parseRetryCount(array $headers): int
    {
        $value = $headers['x-retry-count'] ?? null;

        if (is_int($value)) {
            return max(0, $value);
        }

        if (is_string($value) && ctype_digit(trim($value))) {
            return (int) $value;
        }

        // The .NET SDK reads a 4-byte value as a little-endian int.
        if (is_string($value) && strlen($value) === 4) {
            return max(0, unpack('l', $value)[1]);
        }

        return 0;
    }

    public static function serializeResult(mixed $value, string $jobName): string
    {
        return match (true) {
            $value === null => "Job {$jobName} completed successfully",
            is_string($value) => $value,
            default => Protocol::json($value),
        };
    }

    public static function seconds(float $value): string
    {
        return $value == floor($value) ? (string) (int) $value : rtrim(rtrim(sprintf('%.3F', $value), '0'), '.');
    }
}
