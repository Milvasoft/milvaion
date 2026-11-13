import com.milvasoft.milvaion.worker.JobOptions;
import com.milvasoft.milvaion.worker.MilvaionWorker;
import com.milvasoft.milvaion.worker.PermanentJobException;
import com.milvasoft.milvaion.worker.WorkerOptions;

import java.time.Duration;
import java.util.Map;

/**
 * Sample Milvaion worker. Build the SDK, then run with the SDK and its dependencies on the classpath:
 *
 * <pre>
 * mvn -q package dependency:build-classpath -Dmdep.outputFile=cp.txt
 * MILVAION_WORKER_ID=java-sample-worker MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
 *   MILVAION_REDIS_URL=redis://localhost:6379 \
 *   java -cp "target/milvaion-worker-0.1.0.jar:$(cat cp.txt)" examples/BasicWorker.java
 * </pre>
 */
public class BasicWorker {
    record GreetData(String name, Integer delayMs) {
    }

    public static void main(String[] args) throws Exception {
        WorkerOptions options = WorkerOptions.fromEnv().heartbeatInterval(Duration.ofSeconds(5));

        if (System.getenv("MILVAION_WORKER_ID") == null) options.workerId("java-sample-worker");

        MilvaionWorker worker = new MilvaionWorker(options);

        worker.job("GreetJob", ctx -> {
            GreetData data = ctx.data(GreetData.class);

            if (data == null || data.name() == null || data.name().isBlank())
                throw new PermanentJobException("Job data needs a \"name\".");

            ctx.log("Greeting " + data.name(), Map.of("attempt", ctx.retryCount() + 1));

            // The thread is interrupted when the job is cancelled or times out.
            Thread.sleep(data.delayMs() == null ? 1000 : data.delayMs());

            return Map.of("greeting", "Hello, " + data.name() + "!");
        }, new JobOptions()
                .executionTimeout(Duration.ofMinutes(1))
                .dataSchema(Map.of(
                        "type", "object",
                        "properties", Map.of(
                                "name", Map.of("type", "string", "description", "Who to greet"),
                                "delayMs", Map.of("type", "integer", "description", "How long the job takes")),
                        "required", new String[]{"name"})));

        worker.job("FlakyJob", ctx -> {
            // Fails on the first attempt to show retries in the dashboard.
            if (ctx.retryCount() == 0) throw new IllegalStateException("Simulated transient failure");

            ctx.log("Succeeded on retry");
            return null;
        }, new JobOptions().maxRetries(2).baseRetryDelay(Duration.ofSeconds(2)));

        worker.run(); // blocks; Ctrl+C or SIGTERM shuts down gracefully
    }
}
