<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** What a job handler receives. */
final class JobContext
{
    private mixed $data = null;
    private bool $parsed = false;

    /**
     * @param array<string, mixed> $job
     * @param \Closure(array<string, mixed>): void $sink
     * @param \Closure(): void $tick Runs the worker's housekeeping when signals are not available.
     * @internal
     */
    public function __construct(
        public readonly string $occurrenceId,
        public readonly array $job,
        public readonly int $retryCount,
        public readonly string $instanceId,
        private readonly Abort $abort,
        private readonly \Closure $sink,
        private readonly \Closure $tick,
    ) {
    }

    public function jobId(): string
    {
        return (string) $this->job['id'];
    }

    /** The name the job is registered under (JobNameInWorker). */
    public function jobName(): string
    {
        return (string) $this->job['jobNameInWorker'];
    }

    /** Job data as the raw JSON string, or null. */
    public function rawData(): ?string
    {
        $data = $this->job['jobData'] ?? null;

        return $data === null ? null : (string) $data;
    }

    /**
     * Job data decoded to arrays, or null when the job has none. Invalid JSON fails the job permanently.
     */
    public function data(): mixed
    {
        if (!$this->parsed) {
            $raw = trim($this->rawData() ?? '');

            try {
                $this->data = $raw === '' ? null : json_decode($raw, true, 512, JSON_THROW_ON_ERROR);
            } catch (\JsonException $e) {
                throw new PermanentJobException('Job data is not valid JSON.', 0, $e);
            }

            $this->parsed = true;
        }

        return $this->data;
    }

    /**
     * True once the job has been cancelled, has timed out or the worker is shutting down.
     *
     * With ext-pcntl the job is interrupted automatically; without it, long jobs should call this
     * (or throwIfAborted) regularly so cancellation and timeouts take effect.
     */
    public function isAborted(): bool
    {
        ($this->tick)();

        return $this->abort->reason !== null;
    }

    public function abortReason(): ?AbortReason
    {
        return $this->abort->reason;
    }

    /** Throws JobAbortedException if the job has been aborted. */
    public function throwIfAborted(): void
    {
        ($this->tick)();

        if ($this->abort->reason !== null) {
            throw new JobAbortedException($this->abort->reason, $this->abort->message);
        }
    }

    /** Writes an Information entry to the occurrence log. @param array<string, mixed>|null $data */
    public function log(string $message, ?array $data = null): void
    {
        $this->write('Information', $message, $data);
    }

    /** @param array<string, mixed>|null $data */
    public function debug(string $message, ?array $data = null): void
    {
        $this->write('Debug', $message, $data);
    }

    /** @param array<string, mixed>|null $data */
    public function warn(string $message, ?array $data = null): void
    {
        $this->write('Warning', $message, $data);
    }

    /** Writes an Error entry, with the details of $error when given. @param array<string, mixed>|null $data */
    public function error(string $message, ?\Throwable $error = null, ?array $data = null): void
    {
        $details = $data ?? [];
        $type = null;

        if ($error !== null) {
            $type = $error::class;
            $details['ExceptionType'] = $type;
            $details['StackTrace'] = Errors::format($error);
            $details['InnerException'] = $error->getPrevious()?->getMessage();
        }

        $this->write('Error', $message, $details, 'UserDefined', $type);
    }

    /** @param array<string, mixed>|null $data */
    private function write(string $level, string $message, ?array $data, string $category = 'UserCode', ?string $exceptionType = null): void
    {
        ($this->sink)(self::entry($level, $message, $data, $category, $exceptionType));
        ($this->tick)();
    }

    /**
     * @param array<string, mixed>|null $data
     * @return array<string, mixed>
     * @internal
     */
    public static function entry(string $level, string $message, ?array $data, string $category, ?string $exceptionType = null): array
    {
        return [
            'timestamp' => Protocol::now(),
            'level' => $level,
            'message' => $message,
            'data' => $data === null ? null : (object) $data,
            'category' => $category,
            'exceptionType' => $exceptionType,
        ];
    }
}
