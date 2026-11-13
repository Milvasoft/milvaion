package com.milvasoft.milvaion.worker;

/**
 * A failure that retrying cannot fix, such as invalid job data. A job that throws it is not
 * retried and goes straight to the dead letter queue.
 */
public class PermanentJobException extends RuntimeException {
    public PermanentJobException(String message) {
        super(message);
    }

    public PermanentJobException(String message, Throwable cause) {
        super(message, cause);
    }
}
