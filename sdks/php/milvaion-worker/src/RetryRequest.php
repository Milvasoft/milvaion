<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** The next attempt of a failed job, handed to a broker that schedules retries itself. @internal */
final class RetryRequest
{
    /**
     * @param int $retryCount Of the next attempt.
     * @param float $notBefore Unix time the attempt is due.
     */
    public function __construct(
        public readonly string $jobId,
        public readonly string $occurrenceId,
        public readonly int $retryCount,
        public readonly int $maxRetries,
        public readonly float $notBefore,
    ) {
    }
}
