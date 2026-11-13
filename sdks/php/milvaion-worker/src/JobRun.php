<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\LoggerInterface;

/** State and reporting helpers of one execution. @internal */
final class JobRun
{
    /** @param array<string, mixed> $job */
    public function __construct(
        public readonly array $job,
        public readonly string $occurrenceId,
        public readonly int $retryCount,
        private readonly WorkerOptions $options,
        private readonly Publisher $publisher,
        private readonly LogBuffer $logs,
        private readonly LoggerInterface $logger,
    ) {
    }

    public function jobId(): string
    {
        return (string) $this->job['id'];
    }

    public function jobName(): string
    {
        return (string) $this->job['jobNameInWorker'];
    }

    /** @param array<string, mixed>|null $data */
    public function log(string $level, string $message, ?array $data = null, string $category = 'UserCode'): void
    {
        $this->logs->add($this->occurrenceId, $this->options->instanceId, JobContext::entry($level, $message, $data, $category));
    }

    public function logError(string $message, \Throwable $error): void
    {
        $this->logs->add($this->occurrenceId, $this->options->instanceId, JobContext::entry('Error', $message, [
            'ExceptionType' => Errors::type($error),
            'StackTrace' => Errors::format($error),
            'InnerException' => $error->getPrevious()?->getMessage(),
        ], 'UserDefined', Errors::type($error)));
    }

    public function flushLogs(): void
    {
        try {
            $this->logs->flush();
        } catch (\Throwable $e) {
            $this->logger->warning('Failed to flush job logs; they stay buffered and will be retried: ' . $e->getMessage());
        }
    }

    /**
     * Publishes a status update. Final statuses are retried, because losing one leaves the
     * occurrence running until the zombie detector gives up on it.
     */
    public function status(JobStatus $status, ?float $start = null, ?float $end = null, ?string $result = null, ?string $exception = null, bool $retry = true): void
    {
        $body = Protocol::json([
            'occurrenceId' => $this->occurrenceId,
            'jobId' => $this->jobId(),
            'workerId' => $this->options->workerId,
            'instanceId' => $this->options->instanceId,
            'status' => $status->value,
            'startTime' => Protocol::time($start),
            'endTime' => Protocol::time($end),
            'durationMs' => $start !== null && $end !== null ? (int) round(($end - $start) * 1000) : null,
            'result' => $result,
            'exception' => $exception,
            'messageTimestamp' => Protocol::now(),
        ]);

        $delays = $retry ? [0.5, 2, 5] : [];

        for ($attempt = 0; ; $attempt++) {
            try {
                $this->publisher->publish('', Protocol::QUEUE_STATUS_UPDATES, $body);

                return;
            } catch (\Throwable $e) {
                if ($attempt >= count($delays)) {
                    $this->logger->warning("Failed to publish status {$status->name} for occurrence {$this->occurrenceId}: {$e->getMessage()}");

                    return;
                }

                usleep((int) ($delays[$attempt] * 1_000_000));
            }
        }
    }
}
