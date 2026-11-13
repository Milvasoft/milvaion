<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** @internal */
final class Errors
{
    /** Renders an exception the way the .NET SDK does, so the dashboard shows both alike. */
    public static function format(\Throwable $error): string
    {
        if ($error instanceof RemoteFailure) {
            return $error->formatted();
        }

        $text = sprintf("Type: %s\nMessage: %s\nStackTrace: %s\n", $error::class, $error->getMessage(), $error->getTraceAsString());

        if ($error->getPrevious() !== null) {
            $text .= "\nInner Exception:\n" . self::format($error->getPrevious());
        }

        return $text;
    }

    /** Class name of the exception, or of the original one for an exception carried back from a job process. */
    public static function type(\Throwable $error): string
    {
        return $error instanceof RemoteFailure ? $error->remoteType() : $error::class;
    }
}
