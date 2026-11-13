<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Thrown into a running job, or by JobContext::throwIfAborted(), once the job has been aborted. */
class JobAbortedException extends \RuntimeException
{
    public function __construct(public readonly AbortReason $reason, string $message)
    {
        parent::__construct($message);
    }
}
