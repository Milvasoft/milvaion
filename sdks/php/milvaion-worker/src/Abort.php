<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Abort state of one execution. @internal */
final class Abort
{
    public ?AbortReason $reason = null;
    public string $message = '';

    /** Returns false if the job was already aborted. */
    public function trigger(AbortReason $reason, string $message): bool
    {
        if ($this->reason !== null) {
            return false;
        }

        $this->reason = $reason;
        $this->message = $message;

        return true;
    }
}
