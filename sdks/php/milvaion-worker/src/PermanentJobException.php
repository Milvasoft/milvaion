<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/**
 * A failure that retrying cannot fix, such as invalid job data. A job that throws it is not
 * retried and goes straight to the dead letter queue.
 */
class PermanentJobException extends \RuntimeException
{
}
