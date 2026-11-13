<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Why a running job was aborted. */
enum AbortReason: string
{
    /** Cancelled from the dashboard. */
    case Cancelled = 'cancelled';
    /** Ran past its execution timeout. */
    case TimedOut = 'timed_out';
    /** The worker is shutting down; the job goes back to the queue. */
    case Shutdown = 'shutdown';
}
