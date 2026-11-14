<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * A record read from a job or retry topic, copied out of the librdkafka message.
 *
 * @internal
 */
final class Record
{
    /** @param array<string, string> $headers */
    public function __construct(
        public readonly string $topic,
        public readonly int $partition,
        public readonly int $offset,
        public readonly ?string $key,
        public readonly ?string $value,
        public readonly array $headers,
    ) {
    }
}
