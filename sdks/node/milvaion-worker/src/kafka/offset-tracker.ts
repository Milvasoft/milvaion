/**
 * Ordered commit tracking for one partition. Records are dispatched in offset order and may finish
 * in any order; Kafka commits a position, so only the end of the contiguous finished range can be
 * committed. Offsets need not be contiguous (compaction, transaction markers).
 */
export class PartitionOffsetTracker {
  private readonly entries: Array<{ offset: bigint; done: boolean }> = [];
  private head = 0;
  private last: bigint | undefined;
  private committedOffset: bigint | undefined;

  /** Records dispatched and not finished yet. */
  get pending(): number {
    let count = 0;

    for (let i = this.head; i < this.entries.length; i++) {
      if (!this.entries[i]!.done)
        count++;
    }

    return count;
  }

  /** Offset last committed through `markCommitted`. */
  get committed(): bigint | undefined {
    return this.committedOffset;
  }

  /** Registers a dispatched record. Offsets must increase. */
  add(offset: bigint): void {
    if (this.last !== undefined && offset <= this.last)
      throw new Error(`Offset ${offset} is not after the last tracked offset ${this.last}.`);

    this.entries.push({ offset, done: false });
    this.last = offset;
  }

  /** Whether a record at this offset has been dispatched already, e.g. when a rebalance makes it come again. */
  isDispatched(offset: bigint): boolean {
    return this.last !== undefined && offset <= this.last;
  }

  /** Marks a record as finished. Returns false if it is not tracked (or already finished). */
  complete(offset: bigint): boolean {
    let low = this.head;
    let high = this.entries.length - 1;

    while (low <= high) {
      const mid = (low + high) >> 1;
      const entry = this.entries[mid]!;

      if (entry.offset === offset) {
        if (entry.done)
          return false;

        entry.done = true;
        this.compact();

        return true;
      }

      if (entry.offset < offset)
        low = mid + 1;
      else
        high = mid - 1;
    }

    return false;
  }

  /**
   * The offset to commit, which is the next one to consume: the oldest unfinished record, or the
   * one after the last record when all are finished. Undefined before any record was tracked.
   */
  committable(): bigint | undefined {
    const first = this.entries[this.head];

    if (first)
      return first.offset;

    return this.last === undefined ? undefined : this.last + 1n;
  }

  /** The offset to commit if it moved since the last commit, otherwise undefined. */
  pendingCommit(): bigint | undefined {
    const offset = this.committable();

    return offset !== undefined && offset !== this.committedOffset ? offset : undefined;
  }

  markCommitted(offset: bigint): void {
    if (this.committedOffset === undefined || offset > this.committedOffset)
      this.committedOffset = offset;
  }

  private compact(): void {
    while (this.head < this.entries.length && this.entries[this.head]!.done)
      this.head++;

    if (this.head > 1024 && this.head * 2 > this.entries.length) {
      this.entries.splice(0, this.head);
      this.head = 0;
    }
  }
}
