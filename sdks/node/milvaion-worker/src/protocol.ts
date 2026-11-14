/**
 * Wire-level names and message shapes shared with the Milvaion API.
 * See docs/githubdocs/WORKER-PROTOCOL.md for the full protocol.
 */

export const Exchanges = {
  jobs: 'jobs.topic',
  deadLetter: 'dlx_scheduled_jobs',
} as const;

export const DeadLetterRoutingKey = 'failed_jobs';

/** Version of this SDK, reported to the dashboard. Kept equal to package.json by a test. */
export const SdkVersion = '1.1.0';

export const Queues = {
  jobs: 'scheduled_jobs_queue',
  workerLogs: 'worker_logs_queue',
  workerHeartbeat: 'worker_heartbeat_queue',
  workerRegistration: 'worker_registration_queue',
  statusUpdates: 'job_status_updates_queue',
  failedOccurrences: 'failed_jobs_queue',
} as const;

/** Numeric values of the API's JobOccurrenceStatus enum. */
export const JobOccurrenceStatus = {
  Queued: 0,
  Running: 1,
  Completed: 2,
  Failed: 3,
  Cancelled: 4,
  TimedOut: 5,
  Unknown: 6,
  Skipped: 7,
} as const;

export type JobOccurrenceStatus = (typeof JobOccurrenceStatus)[keyof typeof JobOccurrenceStatus];

export type LogLevel = 'Debug' | 'Information' | 'Warning' | 'Error';

/** Body of a job delivery. The API sends more properties than these; they are kept as-is. */
export interface ScheduledJobMessage {
  id: string;
  displayName?: string | null;
  description?: string | null;
  tags?: string | null;
  jobNameInWorker: string;
  /** JSON string of the job data, or null. */
  jobData?: string | null;
  executeAt?: string | null;
  cronExpression?: string | null;
  workerId?: string | null;
  routingPattern?: string | null;
  executionTimeoutSeconds?: number | null;
  zombieTimeoutMinutes?: number | null;
  version?: number;
  [key: string]: unknown;
}

export interface OccurrenceLog {
  timestamp: string;
  level: LogLevel;
  message: string;
  data?: Record<string, unknown> | null;
  category?: string | null;
  exceptionType?: string | null;
}

export interface WorkerLogMessage {
  occurrenceId: string;
  /** Instance id of the worker that wrote the log. */
  workerId: string;
  log: OccurrenceLog;
  messageTimestamp: string;
}

export interface WorkerLogBatchMessage {
  logs: WorkerLogMessage[];
  batchTimestamp: string;
  count: number;
}

export interface JobStatusUpdateMessage {
  occurrenceId: string;
  jobId: string;
  workerId: string;
  instanceId: string;
  status: JobOccurrenceStatus;
  startTime?: string | null;
  endTime?: string | null;
  durationMs?: number | null;
  result?: string | null;
  exception?: string | null;
  messageTimestamp: string;
}

export interface WorkerHeartbeatMessage {
  workerId: string;
  instanceId: string;
  currentJobs: number;
  memoryBytes: number;
  cpuUsagePercent: number;
  timestamp: string;
  isStopping: boolean;
}

export interface WorkerDiscoveryRequest {
  workerId: string;
  instanceId: string;
  displayName: string;
  hostName: string;
  ipAddress: string;
  routingPatterns: Record<string, string>;
  jobDataDefinitions: Record<string, string>;
  jobResultDefinitions: Record<string, string>;
  jobTypes: string[];
  maxParallelJobs: number;
  version: string;
  /** JSON string. The API reads it case-sensitively, so its properties are PascalCase. */
  metadata: string;
}

export interface WorkerMetadata {
  IsExternal: boolean;
  ExternalScheduler: string | null;
  ProcessorCount: number;
  OSVersion: string;
  RuntimeVersion: string;
  HeartbeatInterval: number;
  Sdk: string;
  SdkVersion: string;
  JobConfigs: Array<{
    JobType: string;
    ConsumerId: string;
    MaxParallelJobs: number;
    ExecutionTimeoutSeconds: number;
  }>;
}

export interface DlqJobMessage {
  id: string;
  displayName?: string | null;
  jobNameInWorker: string;
  jobData?: string | null;
  executeAt?: string | null;
  status: JobOccurrenceStatus;
  exception: string;
}

/** Message published by the API on the Redis cancellation channel. */
export interface CancellationRequest {
  CorrelationId?: string;
  JobId?: string;
  OccurrenceId?: string;
  Reason?: string;
}
