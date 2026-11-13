<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** One message taken from a job queue. @internal */
final class Delivery
{
    /**
     * @param array<string, mixed> $headers
     * @param \Closure(): void $ack
     * @param \Closure(bool): void $nack
     */
    public function __construct(
        public readonly string $body,
        public readonly array $headers,
        public readonly ?string $correlationId,
        public readonly bool $redelivered,
        public readonly \Closure $ack,
        public readonly \Closure $nack,
    ) {
    }
}
