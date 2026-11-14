package com.milvasoft.milvaion.worker;

import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.Objects;

/**
 * Asks Milvaion to run the job again at a specific time instead of retrying now, for example when
 * an API quota resets at 11:00 UTC. The worker does not retry it locally and does not send it to
 * the dead letter queue: the occurrence ends as {@code Failed} with the requested time attached.
 * Milvaion reruns workflow steps at that time; a standalone job is not retried.
 */
public class RetryAfterException extends RuntimeException {
    private final Instant retryAt;

    public RetryAfterException(Instant retryAt, String message) {
        this(retryAt, message, null);
    }

    public RetryAfterException(Instant retryAt, String message, Throwable cause) {
        super(message == null || message.isEmpty() ? defaultMessage(retryAt) : message, cause);
        this.retryAt = retryAt;
    }

    /** The UTC time the job asked to run again at. */
    public Instant getRetryAt() {
        return retryAt;
    }

    private static String defaultMessage(Instant retryAt) {
        return "Job asked to be retried at " + Objects.requireNonNull(retryAt, "retryAt").truncatedTo(ChronoUnit.MILLIS) + ".";
    }
}
