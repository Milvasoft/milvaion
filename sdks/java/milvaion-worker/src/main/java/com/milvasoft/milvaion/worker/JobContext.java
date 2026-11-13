package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ObjectNode;

import java.util.Map;
import java.util.function.Consumer;

/** What a job handler receives. */
public final class JobContext {
    private final String occurrenceId;
    private final JsonNode job;
    private final int retryCount;
    private final String instanceId;
    private final Abort abort;
    private final Consumer<ObjectNode> sink;

    JobContext(String occurrenceId, JsonNode job, int retryCount, String instanceId, Abort abort, Consumer<ObjectNode> sink) {
        this.occurrenceId = occurrenceId;
        this.job = job;
        this.retryCount = retryCount;
        this.instanceId = instanceId;
        this.abort = abort;
        this.sink = sink;
    }

    /** Id of this execution. Stable across retries and redeliveries, so it works as an idempotency key. */
    public String occurrenceId() { return occurrenceId; }

    public String jobId() { return job.path("id").asText(); }

    /** The name the job is registered under (JobNameInWorker). */
    public String jobName() { return job.path("jobNameInWorker").asText(); }

    /** 0 on the first attempt, then 1, 2, ... */
    public int retryCount() { return retryCount; }

    /** Instance id of this worker process. */
    public String instanceId() { return instanceId; }

    /** The scheduled job as the API sent it. */
    public JsonNode job() { return job; }

    /** Job data as the raw JSON string, or {@code null}. */
    public String rawData() {
        JsonNode data = job.get("jobData");
        return data == null || data.isNull() ? null : data.asText();
    }

    /** Job data as a tree, or {@code null}. Data that is not valid JSON fails the job permanently. */
    public JsonNode data() {
        return data(JsonNode.class);
    }

    /** Job data bound to {@code type}, or {@code null}. Data that does not match fails the job permanently. */
    public <T> T data(Class<T> type) {
        String raw = rawData();

        if (raw == null || raw.isBlank()) return null;

        try {
            return Json.MAPPER.readValue(raw, type);
        } catch (Exception e) {
            throw new PermanentJobException("Job data does not match " + type.getSimpleName() + ": " + e.getMessage(), e);
        }
    }

    /** Job data bound to a generic type, e.g. {@code new TypeReference<List<Item>>() {}}. */
    public <T> T data(TypeReference<T> type) {
        String raw = rawData();

        if (raw == null || raw.isBlank()) return null;

        try {
            return Json.MAPPER.readValue(raw, type);
        } catch (Exception e) {
            throw new PermanentJobException("Job data does not match " + type.getType().getTypeName() + ": " + e.getMessage(), e);
        }
    }

    /** True once the job has been cancelled, has timed out or the worker is shutting down. */
    public boolean isAborted() { return abort.reason() != null; }

    public JobAbortedException.Reason abortReason() { return abort.reason(); }

    /** Throws {@link JobAbortedException} if the job has been aborted. Call it in long loops. */
    public void throwIfAborted() {
        JobAbortedException.Reason reason = abort.reason();

        if (reason != null) throw new JobAbortedException(reason, abort.message());
    }

    /** Writes an Information entry to the occurrence log. */
    public void log(String message) { write("Information", message, null, "UserCode", null); }

    public void log(String message, Map<String, ?> data) { write("Information", message, data, "UserCode", null); }

    public void debug(String message) { write("Debug", message, null, "UserCode", null); }

    public void warn(String message) { write("Warning", message, null, "UserCode", null); }

    public void warn(String message, Map<String, ?> data) { write("Warning", message, data, "UserCode", null); }

    public void error(String message) { error(message, null); }

    /** Writes an Error entry, with the details of {@code error} when it is not null. */
    public void error(String message, Throwable error) {
        ObjectNode data = Json.object();
        String type = null;

        if (error != null) {
            type = error.getClass().getName();
            data.put("ExceptionType", type);
            data.put("StackTrace", Errors.format(error));
            data.put("InnerException", error.getCause() == null ? null : error.getCause().getMessage());
        }

        write("Error", message, data, "UserDefined", type);
    }

    private void write(String level, String message, Object data, String category, String exceptionType) {
        sink.accept(log(level, message, data, category, exceptionType));
    }

    static ObjectNode log(String level, String message, Object data, String category, String exceptionType) {
        ObjectNode log = Json.object();
        log.put("timestamp", Json.now());
        log.put("level", level);
        log.put("message", message);
        log.set("data", data == null ? null : Json.MAPPER.valueToTree(data));
        log.put("category", category);
        log.put("exceptionType", exceptionType);

        return log;
    }
}
