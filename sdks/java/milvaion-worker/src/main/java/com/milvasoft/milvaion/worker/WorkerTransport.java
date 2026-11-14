package com.milvasoft.milvaion.worker;

import java.util.Map;

/** The broker side of a worker: RabbitMQ (default) or Kafka. */
interface WorkerTransport {
    interface Hooks {
        /** Publishes the registration and a heartbeat. Called once consuming has started. */
        void register() throws Exception;

        /** Registers again after a reconnect, asynchronously and without throwing. */
        void reregister();
    }

    /** What the job processor publishes through. */
    JobTransport jobs();

    /** Connects, prepares topics or queues, starts consuming and registers. Throws if the broker cannot be reached in time. */
    void start(Map<String, ResolvedJob> jobs, JobProcessor processor, Hooks hooks) throws Exception;

    void publishRegistration(byte[] body) throws Exception;

    void publishHeartbeat(byte[] body) throws Exception;

    /** {@code key} is the occurrence id of the first entry of the batch. */
    void publishLogs(String key, byte[] body) throws Exception;

    /** Stops taking new jobs. Running jobs can still report their outcome. */
    void stopConsuming();

    /** Commits what has finished and disconnects. */
    void close();

    /** Human readable broker name for log messages. */
    String name();
}
