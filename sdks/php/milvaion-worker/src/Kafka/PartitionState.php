<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * What this instance holds of one assigned partition.
 *
 * @internal
 */
final class PartitionState
{
    public readonly OffsetTracker $tracker;
    /** @var list<Record> taken from the consumer, not dispatched yet, in offset order */
    public array $backlog = [];
    public bool $paused = false;
    /** Offset and occurrence id of the head of a retry partition that is not due yet. */
    public ?int $heldOffset = null;
    public ?string $heldOccurrenceId = null;
    public int $committed = -1;

    public function __construct(public readonly string $topic, public readonly int $partition, public readonly bool $retry)
    {
        $this->tracker = new OffsetTracker();
    }

    public static function key(string $topic, int $partition): string
    {
        return "{$topic}|{$partition}";
    }
}
