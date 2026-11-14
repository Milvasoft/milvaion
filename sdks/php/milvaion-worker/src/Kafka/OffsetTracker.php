<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * Follows the records of one partition that this instance has taken, in offset order, and knows
 * how far the partition may be committed: up to the first record that is not finished, because a
 * Kafka commit covers every record before it.
 *
 * @internal
 */
final class OffsetTracker
{
    /** @var array<int, bool> offset => finished, in ascending offset order */
    private array $pending = [];
    private int $last = -1;
    private int $position = -1;

    /** Registers a record taken from the partition. A repeated or older offset returns false and is ignored. */
    public function add(int $offset): bool
    {
        if ($offset <= $this->last) {
            return false;
        }

        $this->last = $offset;
        $this->pending[$offset] = false;

        return true;
    }

    /** Marks a record finished. Returns false for an offset the tracker does not hold. */
    public function done(int $offset): bool
    {
        if (!isset($this->pending[$offset])) {
            return false;
        }

        $this->pending[$offset] = true;

        while ($this->pending !== [] && $this->pending[$first = array_key_first($this->pending)]) {
            $this->position = $first + 1;
            unset($this->pending[$first]);
        }

        return true;
    }

    /** The offset to commit: one past the end of the contiguous finished range. null until a record has finished. */
    public function commitPosition(): ?int
    {
        return $this->position >= 0 ? $this->position : null;
    }

    /** Taken records that cannot be committed yet. */
    public function outstanding(): int
    {
        return count($this->pending);
    }
}
