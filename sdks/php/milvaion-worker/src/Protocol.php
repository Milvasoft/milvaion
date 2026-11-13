<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/**
 * Wire-level names shared with the Milvaion API.
 * See docs/githubdocs/WORKER-PROTOCOL.md in the repository for the full protocol.
 */
final class Protocol
{
    public const EXCHANGE_JOBS = 'jobs.topic';
    public const EXCHANGE_DEAD_LETTER = 'dlx_scheduled_jobs';
    public const DEAD_LETTER_ROUTING_KEY = 'failed_jobs';

    public const QUEUE_JOBS = 'scheduled_jobs_queue';
    public const QUEUE_WORKER_LOGS = 'worker_logs_queue';
    public const QUEUE_WORKER_HEARTBEAT = 'worker_heartbeat_queue';
    public const QUEUE_WORKER_REGISTRATION = 'worker_registration_queue';
    public const QUEUE_STATUS_UPDATES = 'job_status_updates_queue';
    public const QUEUE_FAILED_OCCURRENCES = 'failed_jobs_queue';

    /** Current time as the API expects it: ISO 8601 UTC with milliseconds. */
    public static function now(): string
    {
        return self::time(microtime(true));
    }

    public static function time(?float $timestamp): ?string
    {
        if ($timestamp === null) {
            return null;
        }

        return \DateTimeImmutable::createFromFormat('U.u', sprintf('%.6F', $timestamp))
            ->setTimezone(new \DateTimeZone('UTC'))
            ->format('Y-m-d\TH:i:s.v\Z');
    }

    /** JSON with unescaped slashes and unicode, as the other SDKs send. */
    public static function json(mixed $value): string
    {
        return json_encode($value, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR | JSON_PRESERVE_ZERO_FRACTION);
    }
}
