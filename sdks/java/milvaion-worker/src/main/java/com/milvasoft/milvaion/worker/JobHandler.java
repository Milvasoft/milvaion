package com.milvasoft.milvaion.worker;

/**
 * Runs one job execution. The return value becomes the occurrence result: a String is stored
 * as-is and anything else as JSON. Throw to fail the attempt and have it retried, or throw a
 * {@link PermanentJobException} to skip retries, or {@link RetryAfterException} to ask to run again at a given time.
 *
 * <p>The handler runs on its own thread, which is interrupted when the job is cancelled, times
 * out or the worker shuts down. The occurrence is settled as soon as that happens.
 */
@FunctionalInterface
public interface JobHandler {
    Object handle(JobContext context) throws Exception;
}
