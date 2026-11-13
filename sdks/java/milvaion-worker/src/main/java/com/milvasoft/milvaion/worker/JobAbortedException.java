package com.milvasoft.milvaion.worker;

/** Thrown by {@link JobContext#throwIfAborted()} once the job has been aborted. */
public class JobAbortedException extends RuntimeException {
    /** Why a running job was aborted. */
    public enum Reason {
        /** Cancelled from the dashboard. */
        CANCELLED,
        /** Ran past its execution timeout. */
        TIMED_OUT,
        /** The worker is shutting down; the job goes back to the queue. */
        SHUTDOWN
    }

    private final Reason reason;

    public JobAbortedException(Reason reason, String message) {
        super(message);
        this.reason = reason;
    }

    public Reason reason() {
        return reason;
    }
}
