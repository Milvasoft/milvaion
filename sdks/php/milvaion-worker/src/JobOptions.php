<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Configuration of one job type. */
final class JobOptions
{
    /**
     * @param string|null $routingPattern Default: "{workerId}.{name without Job}.*".
     * @param string|null $consumerId Shown in the dashboard. Default: "{name without Job}-consumer".
     * @param float|null $executionTimeout Seconds. A timeout set on the job in the dashboard wins. 0 disables it. Default: the worker default.
     * @param int $maxRetries Retries after the first failure before the job goes to the dead letter queue.
     * @param float $baseRetryDelay Retry n waits baseRetryDelay * 2^(n-1) seconds.
     * @param array<string, mixed>|string|null $dataSchema JSON Schema of the job data; the dashboard builds the job data form from it.
     * @param array<string, mixed>|string|null $resultSchema JSON Schema of the job result.
     */
    public function __construct(
        public readonly ?string $routingPattern = null,
        public readonly ?string $consumerId = null,
        public readonly ?float $executionTimeout = null,
        public readonly int $maxRetries = 3,
        public readonly float $baseRetryDelay = 5,
        public readonly array|string|null $dataSchema = null,
        public readonly array|string|null $resultSchema = null,
    ) {
    }

    /** @internal */
    public function resolve(string $name, \Closure $handler, WorkerOptions $worker): ResolvedJob
    {
        $pattern = $this->routingPattern !== null && trim($this->routingPattern) !== ''
            ? trim($this->routingPattern)
            : self::generateRoutingPattern($name, $worker->workerId);

        return new ResolvedJob(
            name: $name,
            handler: $handler,
            routingPattern: $pattern,
            queue: self::queueNameForPattern($pattern),
            consumerId: $this->consumerId ?? strtolower(self::trimJobSuffix($name)) . '-consumer',
            executionTimeout: $this->executionTimeout ?? $worker->executionTimeout,
            maxRetries: max(0, $this->maxRetries),
            baseRetryDelay: max(0.0, $this->baseRetryDelay),
            dataSchema: self::schema($this->dataSchema),
            resultSchema: self::schema($this->resultSchema),
        );
    }

    /**
     * The routing pattern a job gets when it does not set one, identical to the .NET SDK:
     * "SendEmailJob" in worker "Mail-Worker" becomes "mail-worker.sendemail.*".
     */
    public static function generateRoutingPattern(string $jobName, string $workerId): string
    {
        return strtolower($workerId) . '.' . strtolower(self::trimJobSuffix($jobName)) . '.*';
    }

    /** The queue a job type is consumed from. */
    public static function queueNameForPattern(string $pattern): string
    {
        return Protocol::QUEUE_JOBS . '.' . str_replace(['*', '#'], ['wildcard', 'all'], $pattern);
    }

    private static function trimJobSuffix(string $name): string
    {
        return preg_replace('/job$/i', '', $name) ?? $name;
    }

    /** @param array<string, mixed>|string|null $schema */
    private static function schema(array|string|null $schema): ?string
    {
        return $schema === null || is_string($schema) ? $schema : Protocol::json($schema);
    }
}
