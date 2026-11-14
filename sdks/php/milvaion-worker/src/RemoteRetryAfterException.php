<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** A RetryAfterException raised in a job process. @internal */
final class RemoteRetryAfterException extends RetryAfterException implements RemoteFailure
{
    public function __construct(\DateTimeInterface $retryAt, string $message, private readonly string $type, private readonly string $formatted, ?string $previous)
    {
        parent::__construct($retryAt, $message, $previous === null ? null : new \RuntimeException($previous));
    }

    public function remoteType(): string
    {
        return $this->type;
    }

    public function formatted(): string
    {
        return $this->formatted;
    }
}
