package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.rabbitmq.client.LongString;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.regex.Pattern;

/** Runs job deliveries and reports their progress to the API, following the worker protocol. */
final class JobProcessor implements AutoCloseable {
    private static final Logger LOG = LoggerFactory.getLogger(JobProcessor.class);
    private static final Pattern GUID = Pattern.compile("^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$");

    interface Sleeper {
        /** Sleeps; returns true if the worker started stopping meanwhile. */
        boolean sleep(Duration duration, CountDownLatch shutdown) throws InterruptedException;
    }

    private final WorkerOptions options;
    private final Map<String, ResolvedJob> jobs;
    private final JobTransport transport;
    private final LogBuffer logs;
    private final CancellationRegistry cancellations;
    private final ExecutorService handlers = Executors.newCachedThreadPool(r -> LogBuffer.daemon(r, "milvaion-job"));
    private final ScheduledExecutorService timers = Executors.newScheduledThreadPool(1, r -> LogBuffer.daemon(r, "milvaion-job-timer"));
    private final CountDownLatch shutdown = new CountDownLatch(1);
    private final Object idleLock = new Object();
    private int running;
    Runnable onCountChanged = () -> { };
    Sleeper sleeper = (duration, latch) -> latch.await(duration.toMillis(), TimeUnit.MILLISECONDS);

    JobProcessor(WorkerOptions options, Map<String, ResolvedJob> jobs, Publisher publisher, LogBuffer logs, CancellationRegistry cancellations) {
        this(options, jobs, JobTransport.rabbitMq(publisher), logs, cancellations);
    }

    JobProcessor(WorkerOptions options, Map<String, ResolvedJob> jobs, JobTransport transport, LogBuffer logs, CancellationRegistry cancellations) {
        this.options = options;
        this.jobs = jobs;
        this.transport = transport;
        this.logs = logs;
        this.cancellations = cancellations;
    }

    int running() {
        synchronized (idleLock) {
            return running;
        }
    }

    /** Makes jobs waiting out a retry delay give their message back right away. */
    void beginShutdown() {
        shutdown.countDown();
    }

    /** True once no job is running, false after the timeout. */
    boolean waitForIdle(Duration timeout) throws InterruptedException {
        long deadline = System.nanoTime() + timeout.toNanos();

        synchronized (idleLock) {
            while (running > 0) {
                long left = deadline - System.nanoTime();

                if (left <= 0) return false;

                TimeUnit.NANOSECONDS.timedWait(idleLock, left);
            }

            return true;
        }
    }

    void handle(Delivery delivery, ResolvedJob consumer) {
        synchronized (idleLock) {
            running++;
        }

        onCountChanged.run();

        try {
            process(delivery, consumer);
        } catch (Throwable error) {
            LOG.error("Unexpected error while processing a job delivery.", error);
            settle(() -> delivery.settler().reject("Unexpected error while processing the job: " + error));
        } finally {
            synchronized (idleLock) {
                running--;
                idleLock.notifyAll();
            }

            onCountChanged.run();
        }
    }

    private void process(Delivery delivery, ResolvedJob consumer) {
        JsonNode job = parseJob(delivery.body());

        if (job == null) {
            LOG.error("Received a message on {} that is not a valid job. Sending it to the dead letter queue.", consumer == null ? "the job topic" : consumer.queue());
            settle(() -> delivery.settler().reject("The message is not a valid job."));
            return;
        }

        String occurrenceId = parseOccurrenceId(delivery.headers(), delivery.correlationId());

        if (occurrenceId == null) {
            LOG.error("Job {} arrived without an occurrence id. Sending it to the dead letter queue.", job.path("id").asText());
            settle(() -> delivery.settler().reject("The job has no occurrence id."));
            return;
        }

        Run run = new Run(job, occurrenceId, parseRetryCount(delivery.headers()));

        if (delivery.redelivered()) {
            LOG.warn("Job {} (occurrence {}) was redelivered; the previous attempt may have crashed.", run.jobId(), occurrenceId);
            run.status(JobStatus.RUNNING, new Timing(Instant.now()), null, null, true);
            run.log("Warning", "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
                    Map.of("Reason", "RabbitMQ redelivery detected", "WorkerId", options.instanceId()), "Redelivery");
        }

        Abort abort = new Abort();
        cancellations.register(occurrenceId, abort);

        try {
            execute(delivery, consumer, run, abort);
        } finally {
            cancellations.unregister(occurrenceId);
        }
    }

    private void execute(Delivery delivery, ResolvedJob consumer, Run run, Abort abort) {
        Timing timing = new Timing(Instant.now());

        run.status(JobStatus.RUNNING, timing, null, null, true);
        run.flushLogs();

        ResolvedJob registered = jobs.get(run.jobName().toLowerCase(Locale.ROOT));

        if (registered == null) {
            String exception = "No IJob implementation registered for type: " + run.jobName();
            LOG.error(exception);
            run.status(JobStatus.FAILED, Timing.endOnly(Instant.now()), null, exception, true);
            deadLetter(delivery, run, "[PERMANENT FAILURE] " + exception, run.retryCount, 0);
            return;
        }

        JsonNode jobTimeout = run.job.get("executionTimeoutSeconds");
        Duration timeout = jobTimeout != null && jobTimeout.isNumber()
                ? Duration.ofMillis((long) (jobTimeout.asDouble() * 1000))
                : registered.executionTimeout();

        Outcome outcome = runHandler(registered, run, abort, timeout);
        timing.end = Instant.now();

        if (outcome.abort != null) {
            finishAborted(delivery, run, outcome, timing, timeout);
        } else if (outcome.error != null) {
            finishFailed(delivery, consumer, registered, run, outcome.error, timing);
        } else {
            run.flushLogs();
            run.status(JobStatus.COMPLETED, timing, outcome.result, null, true);
            settle(() -> delivery.settler().ack());
        }
    }

    private record Outcome(String result, Throwable error, JobAbortedException.Reason abort, String abortMessage) {
    }

    private Outcome runHandler(ResolvedJob registered, Run run, Abort abort, Duration timeout) {
        CompletableFuture<Outcome> race = new CompletableFuture<>();

        abort.onAbort(() -> race.complete(new Outcome(null, null, abort.reason(), abort.message())));

        ScheduledFuture<?> timer = timeout != null && !timeout.isZero() && !timeout.isNegative()
                ? timers.schedule(() -> abort.trigger(JobAbortedException.Reason.TIMED_OUT,
                        "Job execution exceeded timeout limit of " + seconds(timeout) + " seconds"), timeout.toMillis(), TimeUnit.MILLISECONDS)
                : null;

        Duration heartbeatInterval = options.jobHeartbeatInterval();
        ScheduledFuture<?> heartbeat = heartbeatInterval != null && !heartbeatInterval.isZero() && !heartbeatInterval.isNegative()
                ? timers.scheduleAtFixedRate(() -> handlers.execute(() -> run.status(JobStatus.RUNNING, null, null, null, false)),
                        heartbeatInterval.toMillis(), heartbeatInterval.toMillis(), TimeUnit.MILLISECONDS)
                : null;

        JobContext context = new JobContext(run.occurrenceId, run.job, run.retryCount, options.instanceId(), abort,
                log -> logs.add(run.occurrenceId, options.instanceId(), log));

        // A handler that ignores the interrupt keeps running, but the job is settled as soon as it is
        // aborted so its slot and message are released.
        Future<?> task = handlers.submit(() -> {
            try {
                Object value = registered.handler().handle(context);
                race.complete(new Outcome(serializeResult(value, run.jobName()), null, null, null));
            } catch (Throwable error) {
                race.complete(error instanceof JobAbortedException aborted
                        ? new Outcome(null, null, aborted.reason(), aborted.getMessage())
                        : new Outcome(null, error, null, null));
            }
        });

        try {
            Outcome outcome = race.join();

            if (outcome.error != null && abort.reason() != null) outcome = new Outcome(null, null, abort.reason(), abort.message());

            if (outcome.abort != null) task.cancel(true);

            return outcome;
        } finally {
            if (timer != null) timer.cancel(false);
            if (heartbeat != null) heartbeat.cancel(false);
        }
    }

    private void finishAborted(Delivery delivery, Run run, Outcome outcome, Timing timing, Duration timeout) {
        switch (outcome.abort) {
            case SHUTDOWN -> {
                // Nobody cancelled this job; it goes back to the queue and runs again.
                LOG.warn("Job {} (occurrence {}) was interrupted by worker shutdown. Returning it to the queue.", run.jobId(), run.occurrenceId);
                run.flushLogs();
                settle(() -> delivery.settler().nack(true));
                return;
            }
            case TIMED_OUT -> {
                run.log("Error", "Job execution exceeded timeout limit of " + seconds(timeout) + " seconds", null, "UserDefined");
                run.flushLogs();
                run.status(JobStatus.TIMED_OUT, timing, null,
                        "Job execution exceeded maximum timeout of " + seconds(timeout) + " seconds (" + timing.durationMs() + "ms elapsed)", true);
            }
            case CANCELLED -> {
                run.log("Warning", "Job execution was cancelled", Map.of("Reason", outcome.abortMessage == null ? "" : outcome.abortMessage), "UserCode");
                run.flushLogs();
                run.status(JobStatus.CANCELLED, timing, "Job cancelled by user or system", null, true);
            }
        }

        settle(() -> delivery.settler().ack());
    }

    private void finishFailed(Delivery delivery, ResolvedJob consumer, ResolvedJob registered, Run run, Throwable error, Timing timing) {
        String exception = Errors.format(error);

        if (error instanceof PermanentJobException) {
            run.logError("Permanent job failure (will not retry): " + error.getMessage(), error);
            run.log("Error", "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
                    Map.of("FailureType", "Permanent", "FinalStatus", "MovedToDLQ"), "Retry");
            run.flushLogs();
            run.status(JobStatus.FAILED, timing, null, exception, true);
            deadLetter(delivery, run, "[PERMANENT FAILURE] " + exception, run.retryCount, 0);
            return;
        }

        run.logError("Job execution failed: " + error.getMessage(), error);

        if (run.retryCount < registered.maxRetries()) {
            Duration delay = registered.baseRetryDelay().multipliedBy(1L << Math.min(run.retryCount, 30));

            Map<String, Object> data = new LinkedHashMap<>();
            data.put("RetryAttempt", run.retryCount + 1);
            data.put("MaxRetries", registered.maxRetries());
            data.put("DelaySeconds", delay.toMillis() / 1000.0);
            data.put("NextRetryTime", Json.time(Instant.now().plus(delay)));

            run.log("Warning", "⚠️ Job failed (attempt " + (run.retryCount + 1) + "/" + registered.maxRetries() + "). Retrying in " + seconds(delay) + "s...", data, "Retry");
            run.flushLogs();
            retry(delivery, consumer, run, delay, registered.maxRetries());
            return;
        }

        String fin = "Max retries (" + registered.maxRetries() + ") exceeded. Original exception: " + exception;

        run.log("Error", "❌ Job failed after " + registered.maxRetries() + " retries. Moving to Dead Letter Queue (DLQ).",
                Map.of("TotalRetries", registered.maxRetries(), "FinalStatus", "MovedToDLQ"), "Retry");
        run.flushLogs();
        timing.end = Instant.now();
        run.status(JobStatus.FAILED, timing, null, fin, true);
        deadLetter(delivery, run, fin, registered.maxRetries(), registered.maxRetries());
    }

    /**
     * Waits out the backoff, republishes the job with a higher retry count and acks the original.
     * The original stays unacked meanwhile, so a crash during the wait gets it redelivered.
     * On Kafka the retry is published at once to the delay topic instead (see {@link #retryWithoutWaiting}).
     */
    private void retry(Delivery delivery, ResolvedJob consumer, Run run, Duration delay, int maxRetries) {
        if (!transport.waitsForRetryDelay()) {
            retryWithoutWaiting(delivery, consumer, run, delay, maxRetries);
            return;
        }

        Duration interval = options.jobHeartbeatInterval();
        boolean beat = interval != null && !interval.isZero() && !interval.isNegative();
        Duration remaining = delay;

        try {
            while (!remaining.isZero() && !remaining.isNegative()) {
                Duration step = beat && interval.compareTo(remaining) < 0 ? interval : remaining;

                if (shutdown.getCount() == 0 || sleeper.sleep(step, shutdown)) {
                    LOG.warn("Worker is stopping during the retry delay of job {}. Returning it to the queue.", run.jobId());
                    settle(() -> delivery.settler().nack(true));
                    return;
                }

                remaining = remaining.minus(step);

                if (beat && !remaining.isZero() && !remaining.isNegative()) run.status(JobStatus.RUNNING, null, null, null, false);
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            settle(() -> delivery.settler().nack(true));
            return;
        }

        try {
            transport.publishRetry(delivery, consumer, run.jobId(), run.occurrenceId, run.retryCount + 1, maxRetries, delay);
        } catch (Exception e) {
            LOG.error("Failed to republish job {} for retry. Dead-lettering the message.", run.jobId(), e);
            settle(() -> delivery.settler().reject("Failed to republish the job for retry: " + e.getMessage()));
            return;
        }

        settle(() -> delivery.settler().ack());
    }

    /**
     * Publishes the retry at once with the time it becomes due, then settles the original, freeing the slot.
     * The occurrence stays Running during the backoff, so job heartbeats continue until the retry is due.
     */
    private void retryWithoutWaiting(Delivery delivery, ResolvedJob consumer, Run run, Duration delay, int maxRetries) {
        try {
            transport.publishRetry(delivery, consumer, run.jobId(), run.occurrenceId, run.retryCount + 1, maxRetries, delay);
        } catch (Exception e) {
            LOG.error("Failed to publish job {} for retry. Dead-lettering the message.", run.jobId(), e);
            settle(() -> delivery.settler().reject("Failed to publish the job for retry: " + e.getMessage()));
            return;
        }

        settle(() -> delivery.settler().ack());

        Duration interval = options.jobHeartbeatInterval();

        if (interval == null || interval.isZero() || interval.isNegative() || interval.compareTo(delay) >= 0 || shutdown.getCount() == 0) return;

        long due = System.nanoTime() + delay.toNanos();
        AtomicReference<ScheduledFuture<?>> beat = new AtomicReference<>();

        try {
            beat.set(timers.scheduleAtFixedRate(() -> {
                if (shutdown.getCount() == 0 || System.nanoTime() >= due) {
                    ScheduledFuture<?> self = beat.get();

                    if (self != null) self.cancel(false);

                    return;
                }

                handlers.execute(() -> run.status(JobStatus.RUNNING, null, null, null, false));
            }, interval.toMillis(), interval.toMillis(), TimeUnit.MILLISECONDS));
        } catch (RuntimeException e) {
            // Shutting down
        }
    }

    /**
     * Sends a Running status for a job that waits on the broker side (a Kafka retry record that is not due yet),
     * so the occurrence is not flagged as a zombie. Asynchronous; failures are only logged.
     */
    void sendWaitingHeartbeat(byte[] body, Map<String, Object> headers) {
        JsonNode job = parseJob(body);
        String occurrenceId = parseOccurrenceId(headers, null);

        if (job == null || occurrenceId == null) return;

        Run run = new Run(job, occurrenceId, parseRetryCount(headers));

        try {
            handlers.execute(() -> run.status(JobStatus.RUNNING, null, null, null, false));
        } catch (RuntimeException e) {
            // Shutting down
        }
    }

    private void deadLetter(Delivery delivery, Run run, String exception, int retryCount, int maxRetries) {
        ObjectNode message = Json.object();
        message.put("id", run.jobId());
        message.set("displayName", run.job.get("displayName"));
        message.put("jobNameInWorker", run.jobName());
        message.set("jobData", run.job.get("jobData"));
        message.set("executeAt", run.job.get("executeAt"));
        message.put("status", JobStatus.FAILED.code());
        message.put("exception", exception);

        try {
            transport.publishDeadLetter(run.jobId(), run.occurrenceId, Json.bytes(message), retryCount, maxRetries);
        } catch (Exception e) {
            // The queue dead-letters a rejected message itself, just without the exception details.
            LOG.error("Failed to publish job {} to the dead letter queue. Rejecting the message instead.", run.jobId(), e);
            settle(() -> delivery.settler().reject("Failed to publish the dead letter message: " + e.getMessage()));
            return;
        }

        settle(() -> delivery.settler().ack());
    }

    private interface Action {
        void run() throws Exception;
    }

    private static void settle(Action action) {
        try {
            action.run();
        } catch (Exception e) {
            // An ack or nack on a channel that has closed: the broker redelivers the message.
            LOG.warn("Could not settle a message; the broker will redeliver it: {}", e.getMessage());
        }
    }

    @Override
    public void close() {
        handlers.shutdownNow();
        timers.shutdownNow();
    }

    private static final class Timing {
        Instant start;
        Instant end;

        Timing(Instant start) {
            this.start = start;
        }

        static Timing endOnly(Instant end) {
            Timing timing = new Timing(null);
            timing.end = end;
            return timing;
        }

        long durationMs() {
            return start == null ? 0 : Duration.between(start, end == null ? Instant.now() : end).toMillis();
        }
    }

    /** State and reporting helpers of one execution. */
    private final class Run {
        final JsonNode job;
        final String occurrenceId;
        final int retryCount;

        Run(JsonNode job, String occurrenceId, int retryCount) {
            this.job = job;
            this.occurrenceId = occurrenceId;
            this.retryCount = retryCount;
        }

        String jobId() { return job.path("id").asText(); }

        String jobName() { return job.path("jobNameInWorker").asText(); }

        void log(String level, String message, Map<String, ?> data, String category) {
            logs.add(occurrenceId, options.instanceId(), JobContext.log(level, message, data, category, null));
        }

        void logError(String message, Throwable error) {
            Map<String, Object> data = new LinkedHashMap<>();
            data.put("ExceptionType", error.getClass().getName());
            data.put("StackTrace", Errors.format(error));
            data.put("InnerException", error.getCause() == null ? null : error.getCause().getMessage());
            logs.add(occurrenceId, options.instanceId(), JobContext.log("Error", message, data, "UserDefined", error.getClass().getName()));
        }

        void flushLogs() {
            try {
                logs.flush();
            } catch (Exception e) {
                LOG.warn("Failed to flush job logs; they stay buffered and will be retried: {}", e.getMessage());
            }
        }

        /**
         * Publishes a status update. Final statuses are retried, because losing one leaves the
         * occurrence running until the zombie detector gives up on it.
         */
        void status(JobStatus status, Timing timing, String result, String exception, boolean retry) {
            ObjectNode message = Json.object();
            message.put("occurrenceId", occurrenceId);
            message.put("jobId", jobId());
            message.put("workerId", options.workerId());
            message.put("instanceId", options.instanceId());
            message.put("status", status.code());
            message.put("startTime", timing == null ? null : Json.time(timing.start));
            message.put("endTime", timing == null ? null : Json.time(timing.end));

            if (timing != null && timing.end != null && timing.start != null) message.put("durationMs", timing.durationMs());
            else message.putNull("durationMs");

            message.put("result", result);
            message.put("exception", exception);
            message.put("messageTimestamp", Json.now());

            byte[] body = Json.bytes(message);
            long[] delays = retry ? new long[]{500, 2000, 5000} : new long[0];

            for (int attempt = 0; ; attempt++) {
                try {
                    transport.publishStatus(occurrenceId, body);
                    return;
                } catch (Exception e) {
                    if (attempt >= delays.length) {
                        LOG.warn("Failed to publish status {} for occurrence {}: {}", status, occurrenceId, e.getMessage());
                        return;
                    }

                    try {
                        Thread.sleep(delays[attempt]);
                    } catch (InterruptedException interrupted) {
                        Thread.currentThread().interrupt();
                        return;
                    }
                }
            }
        }
    }

    static JsonNode parseJob(byte[] body) {
        JsonNode job = Json.parse(body);

        if (job == null || !job.isObject() || !job.path("id").isTextual() || !job.path("jobNameInWorker").isTextual()) return null;

        return job;
    }

    static String headerString(Object value) {
        if (value instanceof byte[] bytes) return new String(bytes, StandardCharsets.UTF_8);
        if (value instanceof LongString text) return text.toString();
        if (value instanceof CharSequence text) return text.toString();

        return null;
    }

    /** Occurrence id from the OccurrenceId header, the CorrelationId header or the correlation id property. */
    static String parseOccurrenceId(Map<String, Object> headers, String correlationId) {
        Map<String, Object> h = headers == null ? Map.of() : headers;

        for (String candidate : new String[]{headerString(h.get("OccurrenceId")), headerString(h.get("CorrelationId")), correlationId}) {
            if (candidate != null && GUID.matcher(candidate.trim()).matches()) return candidate.trim();
        }

        return null;
    }

    static int parseRetryCount(Map<String, Object> headers) {
        Object value = headers == null ? null : headers.get("x-retry-count");

        if (value instanceof Number number) return Math.max(0, number.intValue());

        // The .NET SDK reads a 4-byte value as a little-endian int.
        if (value instanceof byte[] bytes && bytes.length >= 4) return Math.max(0, ByteBuffer.wrap(bytes, 0, 4).order(ByteOrder.LITTLE_ENDIAN).getInt());

        String text = headerString(value);

        if (text != null && text.trim().matches("\\d+")) return Integer.parseInt(text.trim());

        return 0;
    }

    static String serializeResult(Object value, String jobName) {
        if (value == null) return "Job " + jobName + " completed successfully";
        if (value instanceof String text) return text;
        if (value instanceof byte[] bytes) return new String(bytes, StandardCharsets.UTF_8);

        try {
            return Json.string(value);
        } catch (RuntimeException e) {
            return String.valueOf(value);
        }
    }

    static String seconds(Duration duration) {
        double value = duration.toMillis() / 1000.0;

        return value == Math.rint(value) ? String.valueOf((long) value) : String.valueOf(value);
    }
}
