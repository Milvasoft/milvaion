<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** The API's JobOccurrenceStatus, sent as a number. */
enum JobStatus: int
{
    case Queued = 0;
    case Running = 1;
    case Completed = 2;
    case Failed = 3;
    case Cancelled = 4;
    case TimedOut = 5;
    case Unknown = 6;
    case Skipped = 7;
}
