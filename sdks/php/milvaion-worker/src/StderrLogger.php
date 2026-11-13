<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\AbstractLogger;
use Psr\Log\LogLevel;

/** Default logger: writes to STDERR at MILVAION_LOG_LEVEL (default "info") and above. */
final class StderrLogger extends AbstractLogger
{
    private const ORDER = [LogLevel::DEBUG => 0, LogLevel::INFO => 1, LogLevel::NOTICE => 2, LogLevel::WARNING => 3,
        LogLevel::ERROR => 4, LogLevel::CRITICAL => 5, LogLevel::ALERT => 6, LogLevel::EMERGENCY => 7];

    private int $minimum;

    public function __construct(?string $level = null)
    {
        $level ??= getenv('MILVAION_LOG_LEVEL') ?: LogLevel::INFO;
        $this->minimum = self::ORDER[strtolower($level)] ?? 1;
    }

    public function log($level, string|\Stringable $message, array $context = []): void
    {
        if ((self::ORDER[$level] ?? 1) < $this->minimum) {
            return;
        }

        fwrite(STDERR, sprintf("%s %s milvaion: %s\n", date('Y-m-d H:i:s'), strtoupper((string) $level), $message));
    }
}
