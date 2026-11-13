package com.milvasoft.milvaion.worker;

/** The API's JobOccurrenceStatus, sent as a number. */
public enum JobStatus {
    QUEUED(0),
    RUNNING(1),
    COMPLETED(2),
    FAILED(3),
    CANCELLED(4),
    TIMED_OUT(5),
    UNKNOWN(6),
    SKIPPED(7);

    private final int code;

    JobStatus(int code) {
        this.code = code;
    }

    /** The numeric value the API uses. */
    public int code() {
        return code;
    }
}
