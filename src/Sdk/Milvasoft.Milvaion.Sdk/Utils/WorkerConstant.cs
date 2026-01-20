namespace Milvasoft.Milvaion.Sdk.Utils;

public static class WorkerConstant
{
    public const string DeadLetterRoutingKey = "failed_jobs";
    public const string DeadLetterExchangeName = "dlx_scheduled_jobs";
    public const string ExchangeName = "jobs.topic";

    public static class Queues
    {
        public const string Jobs = "scheduled_jobs_queue";
        public const string WorkerLogs = "worker_logs_queue";
        public const string WorkerHeartbeat = "worker_heartbeat_queue";
        public const string WorkerRegistration = "worker_registration_queue";
        public const string StatusUpdates = "job_status_updates_queue";
        public const string FailedOccurrences = "failed_jobs_queue";
    }
}
