<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** One message taken from a job queue or topic. @internal */
final class Delivery
{
    /**
     * @param array<string, mixed> $headers
     * @param \Closure(): void $ack
     * @param \Closure(bool): void $nack nack(true) returns the message so it runs again; nack(false) dead-letters it.
     * @param (\Closure(string): void)|null $reject Dead-letters a message that cannot be processed and says why.
     *        Without it nack(false) is used.
     * @param (\Closure(RetryRequest): void)|null $scheduleRetry Hands a failed job to the broker for a delayed
     *        retry instead of waiting in process (Kafka's retry topic). Returns once the broker has accepted it.
     * @param string|null $source Where the message came from, for logs. Default: the consumer's queue.
     */
    public function __construct(
        public readonly string $body,
        public readonly array $headers,
        public readonly ?string $correlationId,
        public readonly bool $redelivered,
        public readonly \Closure $ack,
        public readonly \Closure $nack,
        public readonly ?\Closure $reject = null,
        public readonly ?\Closure $scheduleRetry = null,
        public readonly ?string $source = null,
    ) {
    }
}
