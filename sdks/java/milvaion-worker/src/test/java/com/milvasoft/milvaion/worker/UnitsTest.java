package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class UnitsTest {
    static final String GUID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00";

    @Test
    void routingMatchesDotNetSdk() {
        assertEquals("email-worker-01.sendemail.*", JobOptions.generateRoutingPattern("SendEmailJob", "Email-Worker-01"));
        assertEquals("w.nonparallel.*", JobOptions.generateRoutingPattern("NonParallel", "w"));
        assertEquals("scheduled_jobs_queue.email-worker-01.sendemail.wildcard", JobOptions.queueNameForPattern("email-worker-01.sendemail.*"));
        assertEquals("scheduled_jobs_queue.a.all", JobOptions.queueNameForPattern("a.#"));
        assertTrue(new WorkerOptions().workerId("java-worker").resolve().instanceId().matches("java-worker-[0-9a-f]{8}"));
    }

    @Test
    void parsesOccurrenceIds() {
        assertEquals(GUID, JobProcessor.parseOccurrenceId(Map.of("OccurrenceId", GUID.getBytes(StandardCharsets.UTF_8)), null));
        assertEquals(GUID, JobProcessor.parseOccurrenceId(Map.of("CorrelationId", GUID), null));
        assertEquals(GUID, JobProcessor.parseOccurrenceId(Map.of(), GUID));
        assertEquals(GUID, JobProcessor.parseOccurrenceId(Map.of("OccurrenceId", "not-a-guid"), GUID));
        assertNull(JobProcessor.parseOccurrenceId(null, null));
    }

    @Test
    void parsesRetryCounts() {
        assertEquals(2, JobProcessor.parseRetryCount(Map.of("x-retry-count", (byte) 2)));
        assertEquals(3, JobProcessor.parseRetryCount(Map.of("x-retry-count", 3L)));
        assertEquals(4, JobProcessor.parseRetryCount(Map.of("x-retry-count", new byte[]{4, 0, 0, 0})));
        assertEquals(5, JobProcessor.parseRetryCount(Map.of("x-retry-count", "5")));
        assertEquals(0, JobProcessor.parseRetryCount(Map.of()));
        assertEquals(0, JobProcessor.parseRetryCount(null));
    }

    @Test
    void formatsErrorsWithCauses() {
        String text = Errors.format(new IllegalStateException("outer", new IllegalArgumentException("inner")));

        assertTrue(text.startsWith("Type: java.lang.IllegalStateException\nMessage: outer\nStackTrace: "));
        assertTrue(text.contains("Inner Exception:\nType: java.lang.IllegalArgumentException\nMessage: inner"));
    }

    @Test
    void logBufferKeepsOrderAfterFailedSend() throws Exception {
        List<String> sent = new ArrayList<>();
        boolean[] fail = {true};
        LogBuffer buffer = new LogBuffer(batch -> {
            if (fail[0]) {
                fail[0] = false;
                throw new IllegalStateException("broker down");
            }

            batch.get("logs").forEach(entry -> sent.add(entry.get("log").get("message").asText()));
        }, 1000, 3_600_000, 10_000);

        buffer.add("o", "i", log("1"));
        buffer.add("o", "i", log("2"));
        assertThrows(IllegalStateException.class, buffer::flush);
        buffer.add("o", "i", log("3"));
        buffer.flush();
        buffer.close();

        assertEquals(List.of("1", "2", "3"), sent);
    }

    @Test
    void logBufferDropsOldestBeyondLimit() throws Exception {
        List<String> sent = new ArrayList<>();
        LogBuffer buffer = new LogBuffer(batch -> batch.get("logs").forEach(entry -> sent.add(entry.get("log").get("message").asText())), 1000, 3_600_000, 2);

        for (String m : List.of("1", "2", "3")) buffer.add("o", "i", log(m));

        buffer.flush();
        buffer.close();

        assertEquals(List.of("2", "3"), sent);
    }

    private static ObjectNode log(String message) {
        return JobContext.log("Information", message, null, "UserCode", null);
    }

    @Test
    void appliesDefaults() {
        WorkerOptions options = new WorkerOptions().workerId(" report-worker ").resolve();

        assertEquals("report-worker", options.workerId());
        assertEquals("Milvaion:JobScheduler:cancellation_channel", options.cancellationChannel());
        assertEquals(Duration.ofHours(1), options.executionTimeout());

        ResolvedJob job = new JobOptions().resolve("SendEmailJob", ctx -> null, options);

        assertEquals("report-worker.sendemail.*", job.routingPattern());
        assertEquals("sendemail-consumer", job.consumerId());
        assertEquals(3, job.maxRetries());
        assertEquals(Duration.ofSeconds(5), job.baseRetryDelay());

        assertThrows(IllegalArgumentException.class, () -> new WorkerOptions().resolve());
        assertThrows(IllegalArgumentException.class, () -> new WorkerOptions().workerId("w").queueType("lazy").resolve());
    }

    @Test
    void readsEnvironment() {
        WorkerOptions options = WorkerOptions.fromEnv(Map.of(
                "MILVAION_WORKER_ID", "env-worker",
                "MILVAION_RABBITMQ_QUEUE_TYPE", "Quorum",
                "MILVAION_REDIS_URL", "redis://redis:6379",
                "MILVAION_REDIS_KEY_PREFIX", "X:",
                "MILVAION_MAX_PARALLEL_JOBS", "4")).resolve();

        assertEquals("env-worker", options.workerId());
        assertTrue(options.quorum());
        assertEquals(4, options.maxParallelJobs());
        assertEquals("X:cancellation_channel", options.cancellationChannel());
        assertNull(WorkerOptions.fromEnv(Map.of("MILVAION_WORKER_ID", "w", "MILVAION_REDIS_URL", "false")).redisUri());
    }

    @Test
    void rejectsDuplicateJobNames() {
        MilvaionWorker worker = new MilvaionWorker(new WorkerOptions().workerId("w").redisUri(null));
        worker.job("TestJob", ctx -> null);

        assertThrows(IllegalArgumentException.class, () -> worker.job("testjob", ctx -> null));
    }

    @Test
    void registrationMetadataIsPascalCase() {
        MilvaionWorker worker = new MilvaionWorker(new WorkerOptions().workerId("w").redisUri(null));
        worker.job("GreetJob", ctx -> null, new JobOptions().dataSchema(Map.of("type", "object")));

        ObjectNode registration = worker.buildRegistration();
        JsonNode metadata = Json.parse(registration.get("metadata").asText().getBytes(StandardCharsets.UTF_8));

        assertEquals("GreetJob", metadata.get("JobConfigs").get(0).get("JobType").asText());
        assertEquals("greet-consumer", metadata.get("JobConfigs").get(0).get("ConsumerId").asText());
        assertEquals("{\"type\":\"object\"}", registration.get("jobDataDefinitions").get("GreetJob").asText());
        assertEquals("w.greet.*", registration.get("routingPatterns").get("GreetJob").asText());
    }
}
