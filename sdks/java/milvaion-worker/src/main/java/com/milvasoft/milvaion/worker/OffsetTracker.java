package com.milvasoft.milvaion.worker;

import java.util.Map;
import java.util.TreeMap;

/**
 * Ordered commit tracker of one partition. Kafka commits a position, not a single record, so a record can only be
 * committed once every earlier record of its partition is done. Records are added in the order they were fetched
 * (offsets may have gaps); {@link #commitPosition()} is the offset after the contiguous finished range.
 * Thread-safe: jobs finish on executor threads, commits happen on the poll thread.
 */
final class OffsetTracker {
    private final TreeMap<Long, Boolean> pending = new TreeMap<>();
    private long position = -1;
    private long committed = -1;
    private volatile boolean revoked;

    /** Records a fetched offset as not done yet. */
    synchronized void add(long offset) {
        pending.putIfAbsent(offset, Boolean.FALSE);
    }

    /** Marks an offset done. Ignored once the partition is revoked or for offsets it never saw. */
    synchronized void done(long offset) {
        if (revoked) return;

        pending.computeIfPresent(offset, (key, value) -> Boolean.TRUE);

        while (!pending.isEmpty()) {
            Map.Entry<Long, Boolean> head = pending.firstEntry();

            if (!head.getValue()) break;

            position = head.getKey() + 1;
            pending.pollFirstEntry();
        }
    }

    /** The offset to commit (the next record to read after the finished range), or -1 if nothing finished yet. */
    synchronized long commitPosition() {
        return position;
    }

    /** The position to commit if it moved since the last {@link #markCommitted}, otherwise -1. */
    synchronized long uncommitted() {
        return position > committed ? position : -1;
    }

    synchronized void markCommitted(long offset) {
        committed = Math.max(committed, offset);
    }

    /** Offsets fetched and not done yet. */
    synchronized int outstanding() {
        int count = 0;

        for (boolean finished : pending.values()) if (!finished) count++;

        return count;
    }

    /** After a revoke, offsets of jobs still running here can no longer be committed by this instance. */
    void revoke() {
        revoked = true;
    }

    boolean revoked() {
        return revoked;
    }
}
