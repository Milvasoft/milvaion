package com.milvasoft.milvaion.worker;

import java.time.Duration;
import java.util.Locale;

/** Configuration of one job type. Unset fields use the defaults. */
public final class JobOptions {
    private String routingPattern;
    private String consumerId;
    private Integer maxParallelJobs;
    private Duration executionTimeout;
    private int maxRetries = 3;
    private Duration baseRetryDelay = Duration.ofSeconds(5);
    private Object dataSchema;
    private Object resultSchema;

    /** Default: "{workerId}.{name without Job}.*". */
    public JobOptions routingPattern(String routingPattern) {
        this.routingPattern = routingPattern;
        return this;
    }

    /** Shown in the dashboard. Default: "{name without Job}-consumer". */
    public JobOptions consumerId(String consumerId) {
        this.consumerId = consumerId;
        return this;
    }

    /** Jobs of this type that may run at once on this instance (prefetch). Default: min(10, worker max). */
    public JobOptions maxParallelJobs(int maxParallelJobs) {
        this.maxParallelJobs = maxParallelJobs;
        return this;
    }

    /** A timeout set on the job in the dashboard wins. {@link Duration#ZERO} disables it. Default: the worker default. */
    public JobOptions executionTimeout(Duration executionTimeout) {
        this.executionTimeout = executionTimeout;
        return this;
    }

    /** Retries after the first failure before the job goes to the dead letter queue. Default: 3. */
    public JobOptions maxRetries(int maxRetries) {
        this.maxRetries = maxRetries;
        return this;
    }

    /** Retry n waits baseRetryDelay × 2^(n-1). Default: 5 s. */
    public JobOptions baseRetryDelay(Duration baseRetryDelay) {
        this.baseRetryDelay = baseRetryDelay;
        return this;
    }

    /** JSON Schema of the job data, as a JSON string, a Map or any Jackson-serializable value. The dashboard builds the job data form from it. */
    public JobOptions dataSchema(Object dataSchema) {
        this.dataSchema = dataSchema;
        return this;
    }

    /** JSON Schema of the job result. */
    public JobOptions resultSchema(Object resultSchema) {
        this.resultSchema = resultSchema;
        return this;
    }

    ResolvedJob resolve(String name, JobHandler handler, WorkerOptions worker) {
        int parallel = maxParallelJobs != null ? maxParallelJobs : Math.min(10, worker.maxParallelJobs());

        if (parallel < 1 || parallel > 65535)
            throw new IllegalArgumentException("maxParallelJobs of job '" + name + "' must be between 1 and 65535, got " + parallel);

        String pattern = routingPattern == null || routingPattern.isBlank() ? generateRoutingPattern(name, worker.workerId()) : routingPattern.trim();

        return new ResolvedJob(
                name,
                handler,
                pattern,
                queueNameForPattern(pattern),
                consumerId != null ? consumerId : trimJobSuffix(name).toLowerCase(Locale.ROOT) + "-consumer",
                parallel,
                executionTimeout != null ? executionTimeout : worker.executionTimeout(),
                Math.max(0, maxRetries),
                baseRetryDelay == null || baseRetryDelay.isNegative() ? Duration.ZERO : baseRetryDelay,
                Json.schema(dataSchema),
                Json.schema(resultSchema));
    }

    /**
     * The routing pattern a job gets when it does not set one, identical to the .NET SDK:
     * "SendEmailJob" in worker "Mail-Worker" becomes "mail-worker.sendemail.*".
     */
    public static String generateRoutingPattern(String jobName, String workerId) {
        return workerId.toLowerCase(Locale.ROOT) + "." + trimJobSuffix(jobName).toLowerCase(Locale.ROOT) + ".*";
    }

    /** The queue a job type is consumed from. */
    public static String queueNameForPattern(String pattern) {
        return Protocol.QUEUE_JOBS + "." + pattern.replace("*", "wildcard").replace("#", "all");
    }

    private static String trimJobSuffix(String name) {
        return name.length() >= 3 && name.substring(name.length() - 3).equalsIgnoreCase("job") ? name.substring(0, name.length() - 3) : name;
    }
}

record ResolvedJob(String name, JobHandler handler, String routingPattern, String queue, String consumerId, int maxParallelJobs,
                   Duration executionTimeout, int maxRetries, Duration baseRetryDelay, String dataSchema, String resultSchema) {
}
