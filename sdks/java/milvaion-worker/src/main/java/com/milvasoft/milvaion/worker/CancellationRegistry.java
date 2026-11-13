package com.milvasoft.milvaion.worker;

import java.util.List;
import java.util.Locale;
import java.util.concurrent.ConcurrentHashMap;

/** Abort handles of the jobs running on this instance, keyed by occurrence id. */
final class CancellationRegistry {
    private final ConcurrentHashMap<String, Abort> running = new ConcurrentHashMap<>();

    void register(String occurrenceId, Abort abort) {
        running.put(occurrenceId.toLowerCase(Locale.ROOT), abort);
    }

    void unregister(String occurrenceId) {
        running.remove(occurrenceId.toLowerCase(Locale.ROOT));
    }

    /** Returns false when the job is not running on this instance. */
    boolean cancel(String occurrenceId, String reason) {
        Abort abort = running.get(occurrenceId.toLowerCase(Locale.ROOT));

        if (abort == null) return false;

        abort.trigger(JobAbortedException.Reason.CANCELLED, reason == null || reason.isBlank() ? "Job was cancelled." : "Job was cancelled: " + reason);

        return true;
    }

    void abortAll(JobAbortedException.Reason reason, String message) {
        List.copyOf(running.values()).forEach(abort -> abort.trigger(reason, message));
    }
}
