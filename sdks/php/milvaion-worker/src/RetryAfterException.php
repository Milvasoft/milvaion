<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/**
 * Asks Milvaion to run the job again at a given time instead of retrying now, for example when
 * an API quota resets at a known time. The worker does not retry it and does not dead-letter it:
 * the occurrence ends as Failed with the requested time attached. Milvaion reruns workflow steps
 * at that time; a standalone job ends there.
 */
class RetryAfterException extends \RuntimeException
{
    private readonly \DateTimeImmutable $retryAt;

    public function __construct(\DateTimeInterface $retryAt, string $message = '', ?\Throwable $previous = null)
    {
        $this->retryAt = \DateTimeImmutable::createFromInterface($retryAt)->setTimezone(new \DateTimeZone('UTC'));

        parent::__construct($message !== '' ? $message : 'Job asked to be retried at ' . $this->retryAtIso() . '.', 0, $previous);
    }

    /** The requested time, in UTC. */
    public function getRetryAt(): \DateTimeImmutable
    {
        return $this->retryAt;
    }

    /** The requested time as ISO 8601 UTC with milliseconds, as sent to Milvaion. @internal */
    public function retryAtIso(): string
    {
        return $this->retryAt->format('Y-m-d\TH:i:s.v\Z');
    }
}
