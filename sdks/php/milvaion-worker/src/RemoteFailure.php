<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** An exception raised in a job process and carried back to the worker. @internal */
interface RemoteFailure
{
    /** Class name of the original exception. */
    public function remoteType(): string;

    /** The original exception, formatted the way Errors::format() does. */
    public function formatted(): string;
}
