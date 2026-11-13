package com.milvasoft.milvaion.worker;

/**
 * Wire-level names shared with the Milvaion API.
 * See docs/githubdocs/WORKER-PROTOCOL.md in the repository for the full protocol.
 */
final class Protocol {
    static final String EXCHANGE_JOBS = "jobs.topic";
    static final String EXCHANGE_DEAD_LETTER = "dlx_scheduled_jobs";
    static final String DEAD_LETTER_ROUTING_KEY = "failed_jobs";

    static final String QUEUE_JOBS = "scheduled_jobs_queue";
    static final String QUEUE_WORKER_LOGS = "worker_logs_queue";
    static final String QUEUE_WORKER_HEARTBEAT = "worker_heartbeat_queue";
    static final String QUEUE_WORKER_REGISTRATION = "worker_registration_queue";
    static final String QUEUE_STATUS_UPDATES = "job_status_updates_queue";
    static final String QUEUE_FAILED_OCCURRENCES = "failed_jobs_queue";

    private Protocol() {
    }
}
