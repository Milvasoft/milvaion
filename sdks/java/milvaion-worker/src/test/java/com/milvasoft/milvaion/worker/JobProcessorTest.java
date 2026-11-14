package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.JsonNode;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class JobProcessorTest {
    static final String OCCURRENCE_ID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00";
    static final String JOB_ID = "0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99";

    record Published(String exchange, String key, JsonNode body, byte[] raw, String correlationId, Map<String, Object> headers) {
    }

    static final class FakePublisher implements Publisher {
        final List<Published> messages = Collections.synchronizedList(new ArrayList<>());
        volatile int failNext;

        @Override
        public synchronized void publish(String exchange, String routingKey, byte[] body, boolean persistent, String correlationId, Map<String, Object> headers) {
            if (failNext > 0) {
                failNext--;
                throw new IllegalStateException("publish failed");
            }

            messages.add(new Published(exchange, routingKey, Json.parse(body), body, correlationId, headers));
        }

        List<Published> to(String queue) {
            synchronized (messages) {
                return messages.stream().filter(m -> m.exchange.isEmpty() && m.key.equals(queue)).toList();
            }
        }

        List<Integer> statuses() {
            return to(Protocol.QUEUE_STATUS_UPDATES).stream().map(m -> m.body.get("status").asInt()).toList();
        }

        JsonNode lastStatus() {
            List<Published> all = to(Protocol.QUEUE_STATUS_UPDATES);
            return all.get(all.size() - 1).body;
        }

        List<JsonNode> logs() {
            List<JsonNode> out = new ArrayList<>();
            to(Protocol.QUEUE_WORKER_LOGS).forEach(m -> m.body.get("logs").forEach(entry -> out.add(entry.get("log"))));
            return out;
        }

        List<Published> deadLettered() {
            synchronized (messages) {
                return messages.stream().filter(m -> m.exchange.equals(Protocol.EXCHANGE_DEAD_LETTER)).toList();
            }
        }
    }

    static final class FakeDelivery {
        final List<String> settled = Collections.synchronizedList(new ArrayList<>());
        final Delivery delivery;

        FakeDelivery(Map<String, Object> job, Map<String, Object> headers, boolean redelivered) {
            Map<String, Object> body = new LinkedHashMap<>();
            body.put("id", JOB_ID);
            body.put("displayName", "Test");
            body.put("jobNameInWorker", "TestJob");
            body.put("jobData", null);
            body.putAll(job);

            Map<String, Object> h = new HashMap<>();
            h.put("OccurrenceId", OCCURRENCE_ID.getBytes(StandardCharsets.UTF_8));
            h.putAll(headers);

            delivery = new Delivery(Json.bytes(body), h, null, redelivered, new Delivery.Settler() {
                @Override
                public void ack() {
                    settled.add("ack");
                }

                @Override
                public void nack(boolean requeue) {
                    settled.add(requeue ? "requeue" : "nack");
                }
            });
        }

        FakeDelivery() {
            this(Map.of(), Map.of(), false);
        }
    }

    final List<JobProcessor> processors = new ArrayList<>();
    final List<LogBuffer> buffers = new ArrayList<>();

    @AfterEach
    void cleanUp() {
        processors.forEach(JobProcessor::close);
        buffers.forEach(LogBuffer::close);
    }

    final class Harness {
        final FakePublisher pub = new FakePublisher();
        final CancellationRegistry cancellations = new CancellationRegistry();
        final List<Duration> sleeps = Collections.synchronizedList(new ArrayList<>());
        final JobProcessor processor;
        final ResolvedJob consumer;

        Harness(Map<String, JobHandler> handlers, JobOptions jobOptions) {
            WorkerOptions options = new WorkerOptions().workerId("java-worker").redisUri(null).jobHeartbeatInterval(Duration.ZERO)
                    .instanceId("java-worker-abcd1234").resolve();
            LogBuffer logs = new LogBuffer(batch -> pub.publish("", Protocol.QUEUE_WORKER_LOGS, Json.bytes(batch), true), 50, 5, 10_000);
            buffers.add(logs);

            Map<String, ResolvedJob> jobs = new LinkedHashMap<>();
            ResolvedJob last = null;

            for (var entry : handlers.entrySet()) {
                last = jobOptions.resolve(entry.getKey(), entry.getValue(), options);
                jobs.put(entry.getKey().toLowerCase(), last);
            }

            consumer = last;
            processor = new JobProcessor(options, jobs, pub, logs, cancellations);
            processor.sleeper = (duration, latch) -> {
                sleeps.add(duration);
                return latch.getCount() == 0;
            };
            processors.add(processor);
        }

        Harness(JobHandler handler) {
            this(Map.of("TestJob", handler), new JobOptions());
        }

        void handle(FakeDelivery delivery) {
            processor.handle(delivery.delivery, consumer);
        }

        CompletableFuture<Void> handleAsync(FakeDelivery delivery) {
            return CompletableFuture.runAsync(() -> handle(delivery));
        }
    }

    static List<Integer> codes(JobStatus... statuses) {
        return java.util.Arrays.stream(statuses).map(JobStatus::code).toList();
    }

    @Test
    void completesAndReportsResult() {
        Harness h = new Harness(ctx -> {
            ctx.log("working", Map.of("to", ctx.data().get("to").asText()));
            return Map.of("sent", true);
        });
        FakeDelivery d = new FakeDelivery(Map.of("jobData", "{\"to\":\"a@b.c\"}"), Map.of(), false);

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertEquals(codes(JobStatus.RUNNING, JobStatus.COMPLETED), h.pub.statuses());

        JsonNode fin = h.pub.lastStatus();
        assertEquals(OCCURRENCE_ID, fin.get("occurrenceId").asText());
        assertEquals(JOB_ID, fin.get("jobId").asText());
        assertEquals("java-worker", fin.get("workerId").asText());
        assertEquals("java-worker-abcd1234", fin.get("instanceId").asText());
        assertEquals("{\"sent\":true}", fin.get("result").asText());
        assertTrue(fin.get("startTime").isTextual() && fin.get("endTime").isTextual() && fin.get("durationMs").isNumber());
        assertTrue(h.pub.logs().stream().anyMatch(l -> l.get("message").asText().equals("working") && l.get("level").asText().equals("Information")
                && l.get("category").asText().equals("UserCode") && l.get("data").get("to").asText().equals("a@b.c")));
    }

    @Test
    void defaultResultMessage() {
        Harness h = new Harness(ctx -> null);
        h.handle(new FakeDelivery());

        assertEquals("Job TestJob completed successfully", h.pub.lastStatus().get("result").asText());
    }

    @Test
    void retriesWithBackoff() {
        Harness h = new Harness(Map.of("TestJob", ctx -> { throw new IllegalStateException("boom"); }),
                new JobOptions().maxRetries(3).baseRetryDelay(Duration.ofSeconds(5)));
        FakeDelivery d = new FakeDelivery(Map.of(), Map.of("x-retry-count", 1), false);

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertEquals(List.of(Duration.ofSeconds(10)), h.sleeps);
        assertEquals(codes(JobStatus.RUNNING), h.pub.statuses());

        List<Published> retries = h.pub.to(h.consumer.queue());
        assertEquals(1, retries.size());
        assertArrayEquals(d.delivery.body(), retries.get(0).raw);
        assertEquals(OCCURRENCE_ID, retries.get(0).correlationId);
        assertEquals(Map.of("CorrelationId", OCCURRENCE_ID, "x-retry-count", 2, "MaxRetries", 3), retries.get(0).headers);
        assertTrue(h.pub.logs().stream().anyMatch(l -> l.get("category").asText().equals("Retry") && l.get("message").asText().contains("attempt 2/3")));
        assertTrue(h.pub.deadLettered().isEmpty());
    }

    @Test
    void deadLettersAfterRetries() {
        Harness h = new Harness(Map.of("TestJob", ctx -> { throw new IllegalStateException("still broken"); }), new JobOptions().maxRetries(3));
        FakeDelivery d = new FakeDelivery(Map.of(), Map.of("x-retry-count", 3), false);

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertEquals(codes(JobStatus.RUNNING, JobStatus.FAILED), h.pub.statuses());
        assertTrue(h.pub.lastStatus().get("exception").asText()
                .startsWith("Max retries (3) exceeded. Original exception: Type: java.lang.IllegalStateException\nMessage: still broken"));

        List<Published> dlq = h.pub.deadLettered();
        assertEquals(1, dlq.size());
        assertEquals(Protocol.DEAD_LETTER_ROUTING_KEY, dlq.get(0).key);
        assertEquals(JOB_ID, dlq.get(0).body.get("id").asText());
        assertEquals(JobStatus.FAILED.code(), dlq.get(0).body.get("status").asInt());
        assertEquals(Map.of("CorrelationId", OCCURRENCE_ID, "x-retry-count", 3, "MaxRetries", 3), dlq.get(0).headers);
    }

    @Test
    void permanentFailureSkipsRetries() {
        Harness h = new Harness(ctx -> { throw new PermanentJobException("bad input"); });
        FakeDelivery d = new FakeDelivery();

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertTrue(h.sleeps.isEmpty());
        assertEquals(codes(JobStatus.RUNNING, JobStatus.FAILED), h.pub.statuses());

        List<Published> dlq = h.pub.deadLettered();
        assertTrue(dlq.get(0).body.get("exception").asText().startsWith("[PERMANENT FAILURE] Type: com.milvasoft.milvaion.worker.PermanentJobException"));
        assertEquals(0, dlq.get(0).headers.get("MaxRetries"));
    }

    @Test
    void retryAfterEndsFailedWithoutRetryOrDeadLetter() {
        java.time.Instant at = java.time.Instant.parse("2026-11-15T11:00:00Z");
        Harness h = new Harness(ctx -> { throw new RetryAfterException(at, "YouTube quota exhausted"); });
        FakeDelivery d = new FakeDelivery();

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertTrue(h.sleeps.isEmpty());
        assertTrue(h.pub.deadLettered().isEmpty());
        assertTrue(h.pub.messages.stream().allMatch(m -> m.exchange().isEmpty()
                && (m.key().equals(Protocol.QUEUE_STATUS_UPDATES) || m.key().equals(Protocol.QUEUE_WORKER_LOGS))), "no republish");
        assertEquals(codes(JobStatus.RUNNING, JobStatus.FAILED), h.pub.statuses());

        JsonNode status = h.pub.lastStatus();
        assertEquals("2026-11-15T11:00:00Z", status.get("retryAfter").asText());
        assertTrue(status.get("exception").asText().startsWith("[RETRY AFTER 2026-11-15T11:00:00Z] Type: com.milvasoft.milvaion.worker.RetryAfterException"));
        assertTrue(status.get("exception").asText().contains("YouTube quota exhausted"));
        assertNotNull(status.get("durationMs"));

        JsonNode warning = h.pub.logs().stream().filter(l -> "Retry".equals(l.path("category").asText())).findFirst().orElseThrow();
        assertEquals("Warning", warning.get("level").asText());
        assertTrue(warning.get("message").asText().startsWith("⏰ Job asked to be retried at 2026-11-15T11:00:00Z."));
        assertEquals("2026-11-15T11:00:00Z", warning.get("data").get("RetryAfter").asText());
    }

    @Test
    void retryAfterDefaultMessageMentionsTheTime() {
        RetryAfterException e = new RetryAfterException(java.time.Instant.parse("2026-11-15T11:00:00Z"), null);
        assertEquals("Job asked to be retried at 2026-11-15T11:00:00Z.", e.getMessage());
        assertEquals(java.time.Instant.parse("2026-11-15T11:00:00Z"), e.getRetryAt());
    }

    @Test
    void wrappedRetryAfterIsAnOrdinaryFailure() {
        Harness h = new Harness(ctx -> { throw new IllegalStateException("outer", new RetryAfterException(java.time.Instant.now(), "inner")); });
        h.handle(new FakeDelivery());

        assertFalse(h.sleeps.isEmpty());
        assertFalse(h.pub.lastStatus().has("retryAfter"));
    }

    @Test
    void invalidJobDataIsPermanent() {
        Harness h = new Harness(ctx -> ctx.data());
        h.handle(new FakeDelivery(Map.of("jobData", "{not json"), Map.of(), false));

        assertTrue(h.pub.deadLettered().get(0).body.get("exception").asText().contains("Job data does not match"));
    }

    @Test
    void timesOutAndInterruptsTheHandler() throws Exception {
        CountDownLatch interrupted = new CountDownLatch(1);
        Harness h = new Harness(ctx -> {
            try {
                Thread.sleep(60_000);
            } catch (InterruptedException e) {
                interrupted.countDown();
                throw e;
            }
            return null;
        });
        FakeDelivery d = new FakeDelivery(Map.of("executionTimeoutSeconds", 0.05), Map.of(), false);

        h.handle(d);

        assertTrue(interrupted.await(2, TimeUnit.SECONDS));
        assertEquals(List.of("ack"), d.settled);
        assertEquals(codes(JobStatus.RUNNING, JobStatus.TIMED_OUT), h.pub.statuses());
        assertTrue(h.pub.lastStatus().get("exception").asText().contains("exceeded maximum timeout of 0.05 seconds"));
    }

    @Test
    void timesOutAHandlerThatIgnoresInterrupts() {
        AtomicBoolean release = new AtomicBoolean();
        Harness h = new Harness(ctx -> {
            long end = System.currentTimeMillis() + 3000;
            while (!release.get() && System.currentTimeMillis() < end) Thread.onSpinWait();
            return null;
        });
        FakeDelivery d = new FakeDelivery(Map.of("executionTimeoutSeconds", 0.05), Map.of(), false);

        h.handle(d);
        release.set(true);

        assertEquals(codes(JobStatus.RUNNING, JobStatus.TIMED_OUT), h.pub.statuses());
    }

    @Test
    void cancelsRunningJob() throws Exception {
        CountDownLatch started = new CountDownLatch(1);
        AtomicReference<JobContext> seen = new AtomicReference<>();
        Harness h = new Harness(ctx -> {
            seen.set(ctx);
            started.countDown();
            Thread.sleep(60_000);
            return null;
        });
        FakeDelivery d = new FakeDelivery();
        CompletableFuture<Void> running = h.handleAsync(d);

        assertTrue(started.await(2, TimeUnit.SECONDS));
        assertTrue(h.cancellations.cancel(OCCURRENCE_ID.toUpperCase(), "Cancelled by user"));
        running.get(2, TimeUnit.SECONDS);

        assertTrue(seen.get().isAborted());
        assertEquals(JobAbortedException.Reason.CANCELLED, seen.get().abortReason());
        assertEquals(List.of("ack"), d.settled);
        assertEquals(codes(JobStatus.RUNNING, JobStatus.CANCELLED), h.pub.statuses());
    }

    @Test
    void shutdownReturnsJobToQueue() throws Exception {
        CountDownLatch started = new CountDownLatch(1);
        Harness h = new Harness(ctx -> {
            started.countDown();
            Thread.sleep(60_000);
            return null;
        });
        FakeDelivery d = new FakeDelivery();
        CompletableFuture<Void> running = h.handleAsync(d);

        assertTrue(started.await(2, TimeUnit.SECONDS));
        h.cancellations.abortAll(JobAbortedException.Reason.SHUTDOWN, "stopping");
        running.get(2, TimeUnit.SECONDS);

        assertEquals(List.of("requeue"), d.settled);
        assertEquals(codes(JobStatus.RUNNING), h.pub.statuses());
    }

    @Test
    void shutdownDuringRetryDelayRequeues() {
        Harness h = new Harness(ctx -> { throw new IllegalStateException("boom"); });
        FakeDelivery d = new FakeDelivery();

        h.processor.beginShutdown();
        h.handle(d);

        assertEquals(List.of("requeue"), d.settled);
        assertTrue(h.pub.to(h.consumer.queue()).isEmpty());
    }

    @Test
    void unknownJobIsDeadLettered() {
        Harness h = new Harness(ctx -> "ok");
        FakeDelivery d = new FakeDelivery(Map.of("jobNameInWorker", "OtherJob"), Map.of(), false);

        h.handle(d);

        assertEquals(List.of("ack"), d.settled);
        assertEquals(codes(JobStatus.RUNNING, JobStatus.FAILED), h.pub.statuses());
        assertTrue(h.pub.deadLettered().get(0).body.get("exception").asText().startsWith("[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob"));
    }

    @Test
    void jobNamesAreCaseInsensitive() {
        Harness h = new Harness(ctx -> "done");
        h.handle(new FakeDelivery(Map.of("jobNameInWorker", "testjob"), Map.of(), false));

        assertEquals("done", h.pub.lastStatus().get("result").asText());
    }

    @Test
    void rejectsBodyThatIsNotAJob() {
        Harness h = new Harness(ctx -> "ok");
        FakeDelivery holder = new FakeDelivery();
        Delivery notAJob = new Delivery("not json".getBytes(StandardCharsets.UTF_8), Map.of(), null, false, holder.delivery.settler());

        h.processor.handle(notAJob, h.consumer);

        assertEquals(List.of("nack"), holder.settled);
        assertTrue(h.pub.messages.isEmpty());
    }

    @Test
    void reportsRedelivery() {
        Harness h = new Harness(ctx -> "ok");
        h.handle(new FakeDelivery(Map.of(), Map.of(), true));

        assertEquals(codes(JobStatus.RUNNING, JobStatus.RUNNING, JobStatus.COMPLETED), h.pub.statuses());
        assertTrue(h.pub.logs().stream().anyMatch(l -> l.get("category").asText().equals("Redelivery")));
    }

    @Test
    void retriesRefusedFinalStatus() {
        Harness h = new Harness(ctx -> "ok");
        h.pub.failNext = 1;
        h.handle(new FakeDelivery());

        assertEquals(codes(JobStatus.RUNNING, JobStatus.COMPLETED), h.pub.statuses());
    }

    @Test
    void waitForIdleTracksRunningJobs() throws Exception {
        CountDownLatch started = new CountDownLatch(1);
        CountDownLatch release = new CountDownLatch(1);
        Harness h = new Harness(ctx -> {
            started.countDown();
            release.await();
            return null;
        });
        CompletableFuture<Void> running = h.handleAsync(new FakeDelivery());

        assertTrue(started.await(2, TimeUnit.SECONDS));
        assertEquals(1, h.processor.running());
        assertFalse(h.processor.waitForIdle(Duration.ofMillis(10)));

        release.countDown();
        running.get(2, TimeUnit.SECONDS);

        assertEquals(0, h.processor.running());
        assertTrue(h.processor.waitForIdle(Duration.ofMillis(10)));
        assertNotNull(h.consumer);
    }
}
