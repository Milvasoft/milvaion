<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * Kafka record headers: UTF-8 strings, numbers as decimal strings.
 *
 * @internal
 */
final class Headers
{
    public const OCCURRENCE_ID = 'OccurrenceId';
    public const CORRELATION_ID = 'CorrelationId';
    public const RETRY_COUNT = 'x-retry-count';
    public const MAX_RETRIES = 'MaxRetries';
    public const NOT_BEFORE = 'x-not-before';
    public const ORIGINAL_TOPIC = 'x-original-topic';
    public const DLQ_REASON = 'x-dlq-reason';

    /**
     * Encodes header values as strings, in a stable order.
     *
     * @param array<string, mixed> $headers
     * @return array<string, string>
     */
    public static function encode(array $headers): array
    {
        $out = [];

        foreach ($headers as $key => $value) {
            $out[(string) $key] = self::value($value);
        }

        ksort($out, SORT_STRING);

        return $out;
    }

    public static function value(mixed $value): string
    {
        return match (true) {
            $value === null => '',
            is_bool($value) => $value ? 'true' : 'false',
            is_int($value), is_string($value) => (string) $value,
            is_float($value) => is_finite($value) && $value == floor($value) ? sprintf('%.0F', $value) : (string) $value,
            $value instanceof \Stringable => (string) $value,
            default => json_encode($value, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) ?: '',
        };
    }

    /**
     * x-not-before as Unix seconds, or null when the header is missing or invalid.
     *
     * @param array<string, mixed> $headers
     */
    public static function notBefore(array $headers): ?float
    {
        $value = trim((string) ($headers[self::NOT_BEFORE] ?? ''));

        return preg_match('/^-?\d+$/', $value) ? ((int) $value) / 1000 : null;
    }

    /** Unix milliseconds of a microtime(true) timestamp, as x-not-before carries it. */
    public static function millis(float $timestamp): string
    {
        return sprintf('%.0F', floor($timestamp * 1000));
    }
}
