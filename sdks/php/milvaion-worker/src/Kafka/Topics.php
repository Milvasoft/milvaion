<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * Topic and consumer group names of one worker (docs/githubdocs/KAFKA-TRANSPORT.md).
 *
 * @internal
 */
final class Topics
{
    public readonly string $jobs;
    public readonly string $retry;
    public readonly string $status;
    public readonly string $logs;
    public readonly string $heartbeats;
    public readonly string $registrations;
    public readonly string $dlq;
    public readonly string $group;

    public function __construct(string $prefix, string $workerId)
    {
        $worker = self::workerSegment($workerId);

        $this->jobs = "{$prefix}milvaion.jobs.{$worker}";
        $this->retry = "{$prefix}milvaion.jobs.{$worker}.retry";
        $this->status = "{$prefix}milvaion.status";
        $this->logs = "{$prefix}milvaion.logs";
        $this->heartbeats = "{$prefix}milvaion.heartbeats";
        $this->registrations = "{$prefix}milvaion.registrations";
        $this->dlq = "{$prefix}milvaion.dlq";
        $this->group = "{$prefix}milvaion.worker.{$worker}";
    }

    /** The topics a worker creates at startup: its own two and the shared ones it writes to. @return list<string> */
    public function all(): array
    {
        return [$this->jobs, $this->retry, $this->status, $this->logs, $this->heartbeats, $this->registrations, $this->dlq];
    }

    /**
     * The worker id as it appears in topic and group names: lower case, with every character
     * outside a-z 0-9 . _ - replaced by "_".
     */
    public static function workerSegment(string $workerId): string
    {
        $lower = strtolower($workerId);

        // One "_" per character; bytes when the id is not valid UTF-8.
        return preg_replace('/[^a-z0-9._-]/u', '_', $lower) ?? preg_replace('/[^a-z0-9._-]/', '_', $lower);
    }
}
