<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\LoggerInterface;

/**
 * Batches occurrence logs. A batch that fails to send is kept and sent first next time, so logs
 * keep their order. Flushed by the worker; PHP has no background thread to do it.
 *
 * @internal
 */
final class LogBuffer
{
    /** @var list<array<string, mixed>> */
    private array $buffer = [];
    /** @var list<array<string, mixed>>|null */
    private ?array $unsent = null;
    private int $dropped = 0;

    /** @param \Closure(string): void $send Receives the JSON of one batch message. */
    public function __construct(
        private readonly \Closure $send,
        private readonly LoggerInterface $logger,
        public readonly int $batchSize = 50,
        public readonly int $maxBuffered = 10_000,
    ) {
    }

    /** @param array<string, mixed> $log */
    public function add(string $occurrenceId, string $instanceId, array $log): void
    {
        if (count($this->buffer) >= $this->maxBuffered) {
            array_shift($this->buffer);

            if (++$this->dropped % 1000 === 1) {
                $this->logger->warning("Log buffer is full; dropped {$this->dropped} oldest log(s) so far.");
            }
        }

        $this->buffer[] = ['occurrenceId' => $occurrenceId, 'workerId' => $instanceId, 'log' => $log, 'messageTimestamp' => Protocol::now()];
    }

    public function pending(): int
    {
        return count($this->buffer) + count($this->unsent ?? []);
    }

    /** Sends everything buffered so far. Throws if a batch cannot be sent; it stays buffered. */
    public function flush(): void
    {
        $perRound = $this->batchSize * 2;
        $rounds = intdiv($this->pending() + $perRound - 1, $perRound) + 1;

        for (; $rounds > 0; $rounds--) {
            $batch = $this->unsent ?? array_splice($this->buffer, 0, $perRound);

            if ($batch === []) {
                return;
            }

            $this->unsent = $batch;
            ($this->send)(Protocol::json(['logs' => $batch, 'batchTimestamp' => Protocol::now(), 'count' => count($batch)]));
            $this->unsent = null;
        }
    }
}
