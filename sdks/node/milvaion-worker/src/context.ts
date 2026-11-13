import { JobAbortedError, PermanentJobError } from './errors.ts';
import type { LogLevel, OccurrenceLog, ScheduledJobMessage } from './protocol.ts';

/** What a job handler receives. */
export interface JobContext<TData = unknown> {
  /** Id of this execution. Stable across retries and redeliveries, so it works as an idempotency key. */
  readonly occurrenceId: string;
  readonly jobId: string;
  readonly jobName: string;
  /** The scheduled job as sent by the API. */
  readonly job: ScheduledJobMessage;
  /** Parsed job data, or `undefined` when the job has none. Invalid JSON fails the job permanently. */
  readonly data: TData;
  /** Job data as the raw JSON string. */
  readonly rawData: string | null;
  /** 0 on the first attempt, then 1, 2, ... on retries. */
  readonly retryCount: number;
  /** Instance id of this worker process. */
  readonly instanceId: string;
  /**
   * Aborted when the job is cancelled from the dashboard, times out, or the worker shuts down.
   * Pass it to fetch/streams and check it in loops; `signal.reason` is a `JobAbortedError`.
   */
  readonly signal: AbortSignal;
  /** Throws the abort reason if the job has been aborted. */
  throwIfAborted(): void;

  /** Writes an Information log to the occurrence. */
  log(message: string, data?: Record<string, unknown>): void;
  debug(message: string, data?: Record<string, unknown>): void;
  warn(message: string, data?: Record<string, unknown>): void;
  error(message: string, error?: unknown, data?: Record<string, unknown>): void;
}

export type LogSink = (log: OccurrenceLog) => void;

export function createJobContext<TData>(args: {
  occurrenceId: string;
  job: ScheduledJobMessage;
  retryCount: number;
  instanceId: string;
  signal: AbortSignal;
  sink: LogSink;
}): JobContext<TData> {
  const { job, signal, sink } = args;
  let parsed = false;
  let data: TData;

  const write = (level: LogLevel, message: string, extra?: Record<string, unknown> | null, category = 'UserCode', exceptionType?: string) =>
    sink({
      timestamp: new Date().toISOString(),
      level,
      message,
      data: extra ?? null,
      category,
      exceptionType: exceptionType ?? null,
    });

  return {
    occurrenceId: args.occurrenceId,
    jobId: job.id,
    jobName: job.jobNameInWorker,
    job,
    rawData: job.jobData ?? null,
    retryCount: args.retryCount,
    instanceId: args.instanceId,
    signal,

    get data(): TData {
      if (!parsed) {
        data = parseJobData<TData>(job.jobData);
        parsed = true;
      }

      return data;
    },

    throwIfAborted() {
      if (signal.aborted)
        throw signal.reason instanceof Error ? signal.reason : new JobAbortedError('cancelled', 'Job was aborted.');
    },

    log: (message, extra) => write('Information', message, extra),
    debug: (message, extra) => write('Debug', message, extra),
    warn: (message, extra) => write('Warning', message, extra),

    error(message, error, extra) {
      const details: Record<string, unknown> = { ...extra };
      let exceptionType: string | undefined;

      if (error instanceof Error) {
        exceptionType = error.name;
        details.ExceptionType = error.name;
        details.StackTrace = error.stack;
        details.InnerException = error.cause instanceof Error ? error.cause.message : error.cause === undefined ? null : String(error.cause);
      } else if (error !== undefined) {
        details.Error = String(error);
      }

      write('Error', message, details, 'UserDefined', exceptionType);
    },
  };
}

function parseJobData<TData>(jobData: string | null | undefined): TData {
  if (jobData === null || jobData === undefined || jobData.trim() === '')
    return undefined as TData;

  try {
    return JSON.parse(jobData) as TData;
  } catch (error) {
    throw new PermanentJobError('Job data is not valid JSON.', { cause: error });
  }
}
