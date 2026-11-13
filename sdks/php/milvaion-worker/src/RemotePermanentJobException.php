<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** A PermanentJobException raised in a job process. @internal */
final class RemotePermanentJobException extends PermanentJobException implements RemoteFailure
{
    public function __construct(string $message, private readonly string $type, private readonly string $formatted, ?string $previous)
    {
        parent::__construct($message, 0, $previous === null ? null : new \RuntimeException($previous));
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
