import type { CancellationRegistry } from './cancellation.ts';
import { createJobContext, type JobContext } from './context.ts';
import { formatError, JobAbortedError, PermanentJobError, type AbortKind } from './errors.ts';
import type { LogBuffer } from './log-buffer.ts';
import type { Logger, ResolvedJobOptions, ResolvedWorkerOptions } from './options.ts';
import {
  DeadLetterRoutingKey,
  Exchanges,
  JobOccurrenceStatus,
  type DlqJobMessage,
  type JobStatusUpdateMessage,
  type LogLevel,
  type OccurrenceLog,
  type ScheduledJobMessage,
  Queues,
} from './protocol.ts';

export type JobHandler<TData = any, TResult = unknown> = (ctx: JobContext<TData>) => TResult | Promise<TResult>;

export interface RegisteredJob {
  name: string;
  handler: JobHandler;
  options: ResolvedJobOptions;
  queue: string;
}

export interface PublishOptions {
  persistent?: boolean;
  correlationId?: string;
  headers?: Record<string, string | number>;
}

/** Publishing side of the broker, with publisher confirms. */
export interface Publisher {
  sendToQueue(queue: string, body: Buffer | object, options?: PublishOptions): Promise<void>;
  publish(exchange: string, routingKey: string, body: Buffer | object, options?: PublishOptions): Promise<void>;
}

/** One message taken from a job queue. */
export interface Delivery {
  body: Buffer;
  headers?: Record<string, unknown>;
  correlationId?: string;
  redelivered: boolean;
  ack(): void;
  nack(requeue: boolean): void;
}

export interface ProcessorDeps {
  options: ResolvedWorkerOptions;
  jobs: ReadonlyMap<string, RegisteredJob>;
  publisher: Publisher;
  logs: LogBuffer;
  cancellations: CancellationRegistry;
  /** Called whenever the number of running jobs changes. */
  onJobCountChanged?: () => void;
  /** Overridable for tests. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
}

type Outcome =
  | { kind: 'completed'; result: string }
  | { kind: 'failed'; error: unknown; permanent: boolean }
  | { kind: 'aborted'; reason: AbortKind; message: string };

const GuidPattern = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/** Runs job deliveries and reports their progress to the API, following the worker protocol. */
export class JobProcessor {
  private readonly deps: ProcessorDeps;
  private readonly logger: Logger;
  private readonly sleep: (ms: number, signal: AbortSignal) => Promise<void>;
  private readonly shutdown = new AbortController();
  private runningCount = 0;
  private idleWaiters: Array<() => void> = [];

  constructor(deps: ProcessorDeps) {
    this.deps = deps;
    this.logger = deps.options.logger;
    this.sleep = deps.sleep ?? abortableSleep;
  }

  get running(): number {
    return this.runningCount;
  }

  /** Makes jobs waiting out a retry delay give their message back to the queue right away. */
  beginShutdown(): void {
    this.shutdown.abort();
  }

  /** Resolves once no job is running, or after `timeoutMs`. Returns whether all jobs finished. */
  waitForIdle(timeoutMs: number): Promise<boolean> {
    if (this.runningCount === 0)
      return Promise.resolve(true);

    return new Promise(resolve => {
      const timer = setTimeout(() => resolve(false), Math.max(0, timeoutMs));

      this.idleWaiters.push(() => {
        clearTimeout(timer);
        resolve(true);
      });
    });
  }

  async handle(delivery: Delivery, consumer: RegisteredJob): Promise<void> {
    this.runningCount++;
    this.deps.onJobCountChanged?.();

    try {
      await this.process(delivery, consumer);
    } catch (error) {
      this.logger.error('Unexpected error while processing a job delivery.', error);
      safe(() => delivery.nack(false), this.logger);
    } finally {
      this.runningCount--;
      this.deps.onJobCountChanged?.();

      if (this.runningCount === 0) {
        const waiters = this.idleWaiters;

        this.idleWaiters = [];
        waiters.forEach(resolve => resolve());
      }
    }
  }

  private async process(delivery: Delivery, consumer: RegisteredJob): Promise<void> {
    const job = parseJob(delivery.body);

    if (!job) {
      this.logger.error(`Received a message on ${consumer.queue} that is not a valid job. Sending it to the dead letter queue.`);
      safe(() => delivery.nack(false), this.logger);
      return;
    }

    const occurrenceId = parseOccurrenceId(delivery.headers, delivery.correlationId);

    if (!occurrenceId) {
      this.logger.error(`Job ${job.id} arrived without an occurrence id. Sending it to the dead letter queue.`);
      safe(() => delivery.nack(false), this.logger);
      return;
    }

    const run = new JobRun(this.deps, job, occurrenceId, parseRetryCount(delivery.headers));

    if (delivery.redelivered) {
      this.logger.warn(`Job ${job.id} (occurrence ${occurrenceId}) was redelivered; the previous attempt may have crashed.`);
      await run.status(JobOccurrenceStatus.Running, { startTime: new Date() });
      run.log('Warning', '⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.',
        { Reason: 'RabbitMQ redelivery detected', WorkerId: this.deps.options.instanceId }, 'Redelivery');
    }

    const controller = new AbortController();

    this.deps.cancellations.register(occurrenceId, controller);

    try {
      await this.execute(delivery, consumer, run, controller);
    } finally {
      this.deps.cancellations.unregister(occurrenceId);
    }
  }

  private async execute(delivery: Delivery, consumer: RegisteredJob, run: JobRun, controller: AbortController): Promise<void> {
    const { job } = run;
    const startTime = new Date();

    await run.status(JobOccurrenceStatus.Running, { startTime });
    await run.flushLogs();

    const registered = this.deps.jobs.get(job.jobNameInWorker.toLowerCase());

    if (!registered) {
      const exception = `No IJob implementation registered for type: ${job.jobNameInWorker}`;

      this.logger.error(exception);

      await run.status(JobOccurrenceStatus.Failed, { endTime: new Date(), exception });
      await this.deadLetter(delivery, run, `[PERMANENT FAILURE] ${exception}`, run.retryCount, 0);

      return;
    }

    const timeoutSeconds = job.executionTimeoutSeconds ?? registered.options.executionTimeoutSeconds;
    const outcome = await this.runHandler(registered, run, controller, timeoutSeconds);
    const endTime = new Date();
    const durationMs = endTime.getTime() - startTime.getTime();

    switch (outcome.kind) {
      case 'completed':
        await run.flushLogs();
        await run.status(JobOccurrenceStatus.Completed, { startTime, endTime, durationMs, result: outcome.result });
        safe(() => delivery.ack(), this.logger);
        return;

      case 'aborted':
        return this.finishAborted(delivery, run, outcome, { startTime, endTime, durationMs, timeoutSeconds });

      case 'failed':
        return this.finishFailed(delivery, consumer, registered, run, outcome, { startTime, endTime, durationMs });
    }
  }

  private async runHandler(registered: RegisteredJob, run: JobRun, controller: AbortController, timeoutSeconds: number): Promise<Outcome> {
    const { signal } = controller;
    const options = this.deps.options;

    const timeout = timeoutSeconds > 0
      ? setTimeout(() => controller.abort(new JobAbortedError('timeout', `Job execution exceeded timeout limit of ${timeoutSeconds} seconds`)), timeoutSeconds * 1000)
      : undefined;

    const heartbeat = options.jobHeartbeatIntervalSeconds > 0
      ? setInterval(() => void run.status(JobOccurrenceStatus.Running, {}, false), options.jobHeartbeatIntervalSeconds * 1000)
      : undefined;

    let onAbort: (() => void) | undefined;

    // A handler that ignores its signal keeps running in the background, but the job is settled
    // as soon as it is aborted so its slot and message are released.
    const aborted = new Promise<never>((_, reject) => {
      onAbort = () => reject(signal.reason);

      if (signal.aborted)
        onAbort();
      else
        signal.addEventListener('abort', onAbort, { once: true });
    });

    aborted.catch(() => undefined);

    try {
      const ctx = createJobContext({
        occurrenceId: run.occurrenceId,
        job: run.job,
        retryCount: run.retryCount,
        instanceId: options.instanceId,
        signal,
        sink: log => run.addLog(log),
      });

      const value = await Promise.race([Promise.resolve().then(() => registered.handler(ctx)), aborted]);

      return { kind: 'completed', result: serializeResult(value, run.job.jobNameInWorker) };
    } catch (error) {
      if (signal.aborted) {
        const reason = signal.reason;

        return reason instanceof JobAbortedError
          ? { kind: 'aborted', reason: reason.kind, message: reason.message }
          : { kind: 'aborted', reason: 'cancelled', message: 'Job execution was cancelled' };
      }

      return { kind: 'failed', error, permanent: error instanceof PermanentJobError };
    } finally {
      clearTimeout(timeout);
      clearInterval(heartbeat);

      if (onAbort)
        signal.removeEventListener('abort', onAbort);
    }
  }

  private async finishAborted(delivery: Delivery, run: JobRun, outcome: Extract<Outcome, { kind: 'aborted' }>,
    timing: { startTime: Date; endTime: Date; durationMs: number; timeoutSeconds: number }): Promise<void> {
    const { startTime, endTime, durationMs, timeoutSeconds } = timing;

    if (outcome.reason === 'shutdown') {
      // Nobody cancelled this job; it goes back to the queue and runs again.
      this.logger.warn(`Job ${run.job.id} (occurrence ${run.occurrenceId}) was interrupted by worker shutdown. Returning it to the queue.`);
      await run.flushLogs();
      safe(() => delivery.nack(true), this.logger);
      return;
    }

    if (outcome.reason === 'timeout') {
      run.log('Error', `Job execution exceeded timeout limit of ${timeoutSeconds} seconds`, null, 'UserDefined');
      await run.flushLogs();
      await run.status(JobOccurrenceStatus.TimedOut, {
        startTime, endTime, durationMs,
        exception: `Job execution exceeded maximum timeout of ${timeoutSeconds} seconds (${durationMs}ms elapsed)`,
      });
    } else {
      run.log('Warning', 'Job execution was cancelled', { Reason: outcome.message });
      await run.flushLogs();
      await run.status(JobOccurrenceStatus.Cancelled, { startTime, endTime, durationMs, result: 'Job cancelled by user or system' });
    }

    safe(() => delivery.ack(), this.logger);
  }

  private async finishFailed(delivery: Delivery, consumer: RegisteredJob, registered: RegisteredJob, run: JobRun,
    outcome: Extract<Outcome, { kind: 'failed' }>, timing: { startTime: Date; endTime: Date; durationMs: number }): Promise<void> {
    const { error, permanent } = outcome;
    const message = error instanceof Error ? error.message : String(error);
    const exception = formatError(error);

    if (permanent) {
      run.logError(`Permanent job failure (will not retry): ${message}`, error);
      run.log('Error', '❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).',
        { FailureType: 'Permanent', FinalStatus: 'MovedToDLQ' }, 'Retry');
      await run.flushLogs();
      await run.status(JobOccurrenceStatus.Failed, { ...timing, exception });
      await this.deadLetter(delivery, run, `[PERMANENT FAILURE] ${exception}`, run.retryCount, 0);
      return;
    }

    run.logError(`Job execution failed: ${message}`, error);

    const { maxRetries, baseRetryDelaySeconds } = registered.options;

    if (run.retryCount < maxRetries) {
      const delaySeconds = 2 ** run.retryCount * baseRetryDelaySeconds;

      run.log('Warning', `⚠️ Job failed (attempt ${run.retryCount + 1}/${maxRetries}). Retrying in ${delaySeconds}s...`, {
        RetryAttempt: run.retryCount + 1,
        MaxRetries: maxRetries,
        DelaySeconds: delaySeconds,
        NextRetryTime: new Date(Date.now() + delaySeconds * 1000).toISOString(),
      }, 'Retry');
      await run.flushLogs();

      return this.retry(delivery, consumer, run, delaySeconds, maxRetries);
    }

    const finalException = `Max retries (${maxRetries}) exceeded. Original exception: ${exception}`;

    run.log('Error', `❌ Job failed after ${maxRetries} retries. Moving to Dead Letter Queue (DLQ).`,
      { TotalRetries: maxRetries, FinalStatus: 'MovedToDLQ' }, 'Retry');
    await run.flushLogs();
    await run.status(JobOccurrenceStatus.Failed, { ...timing, endTime: new Date(), exception: finalException });
    await this.deadLetter(delivery, run, finalException, maxRetries, maxRetries);
  }

  /**
   * Waits out the backoff, then republishes the job with a higher retry count and acks the original.
   * The original stays unacked meanwhile, so a crash during the wait gets it redelivered.
   */
  private async retry(delivery: Delivery, consumer: RegisteredJob, run: JobRun, delaySeconds: number, maxRetries: number): Promise<void> {
    const heartbeatMs = this.deps.options.jobHeartbeatIntervalSeconds * 1000;
    let remainingMs = delaySeconds * 1000;

    try {
      while (remainingMs > 0) {
        const step = heartbeatMs > 0 ? Math.min(remainingMs, heartbeatMs) : remainingMs;

        await this.sleep(step, this.shutdown.signal);
        remainingMs -= step;

        if (remainingMs > 0 && heartbeatMs > 0)
          await run.status(JobOccurrenceStatus.Running, {}, false);
      }
    } catch {
      this.logger.warn(`Worker is stopping during the retry delay of job ${run.job.id}. Returning it to the queue.`);
      safe(() => delivery.nack(true), this.logger);
      return;
    }

    try {
      await this.deps.publisher.sendToQueue(consumer.queue, delivery.body, {
        persistent: true,
        correlationId: run.occurrenceId,
        headers: { CorrelationId: run.occurrenceId, 'x-retry-count': run.retryCount + 1, MaxRetries: maxRetries },
      });
    } catch (error) {
      this.logger.error(`Failed to republish job ${run.job.id} for retry. Dead-lettering the message.`, error);
      safe(() => delivery.nack(false), this.logger);
      return;
    }

    safe(() => delivery.ack(), this.logger);
  }

  private async deadLetter(delivery: Delivery, run: JobRun, exception: string, retryCount: number, maxRetries: number): Promise<void> {
    const { job } = run;
    const message: DlqJobMessage = {
      id: job.id,
      displayName: job.displayName ?? null,
      jobNameInWorker: job.jobNameInWorker,
      jobData: job.jobData ?? null,
      executeAt: job.executeAt ?? null,
      status: JobOccurrenceStatus.Failed,
      exception,
    };

    try {
      await this.deps.publisher.publish(Exchanges.deadLetter, DeadLetterRoutingKey, message, {
        persistent: true,
        correlationId: run.occurrenceId,
        headers: { CorrelationId: run.occurrenceId, 'x-retry-count': retryCount, MaxRetries: maxRetries },
      });
    } catch (error) {
      // The queue dead-letters a rejected message itself, just without the exception details.
      this.logger.error(`Failed to publish job ${job.id} to the dead letter queue. Rejecting the message instead.`, error);
      safe(() => delivery.nack(false), this.logger);
      return;
    }

    safe(() => delivery.ack(), this.logger);
  }
}

/** State and reporting helpers of one job execution. */
class JobRun {
  readonly job: ScheduledJobMessage;
  readonly occurrenceId: string;
  readonly retryCount: number;
  private readonly deps: ProcessorDeps;

  constructor(deps: ProcessorDeps, job: ScheduledJobMessage, occurrenceId: string, retryCount: number) {
    this.deps = deps;
    this.job = job;
    this.occurrenceId = occurrenceId;
    this.retryCount = retryCount;
  }

  addLog(log: OccurrenceLog): void {
    this.deps.logs.add(this.occurrenceId, this.deps.options.instanceId, log);
  }

  log(level: LogLevel, message: string, data: Record<string, unknown> | null = null, category = 'UserCode'): void {
    this.addLog({ timestamp: new Date().toISOString(), level, message, data, category, exceptionType: null });
  }

  logError(message: string, error: unknown): void {
    const isError = error instanceof Error;

    this.addLog({
      timestamp: new Date().toISOString(),
      level: 'Error',
      message,
      data: isError ? { ExceptionType: error.name, StackTrace: error.stack, InnerException: error.cause instanceof Error ? error.cause.message : null } : {},
      category: 'UserDefined',
      exceptionType: isError ? error.name : null,
    });
  }

  async flushLogs(): Promise<void> {
    try {
      await this.deps.logs.flush();
    } catch (error) {
      this.deps.options.logger.warn('Failed to flush job logs; they stay buffered and will be retried.', error);
    }
  }

  /**
   * Publishes a status update. Final statuses are retried a few times because losing one leaves
   * the occurrence running until the zombie detector gives up on it.
   */
  async status(status: JobOccurrenceStatus,
    fields: { startTime?: Date; endTime?: Date; durationMs?: number; result?: string; exception?: string },
    retry = true): Promise<void> {
    const { options, publisher } = this.deps;
    const message: JobStatusUpdateMessage = {
      occurrenceId: this.occurrenceId,
      jobId: this.job.id,
      workerId: options.workerId,
      instanceId: options.instanceId,
      status,
      startTime: fields.startTime?.toISOString() ?? null,
      endTime: fields.endTime?.toISOString() ?? null,
      durationMs: fields.durationMs ?? null,
      result: fields.result ?? null,
      exception: fields.exception ?? null,
      messageTimestamp: new Date().toISOString(),
    };

    const delays = retry ? [500, 2000, 5000] : [];

    for (let attempt = 0; ; attempt++) {
      try {
        await publisher.sendToQueue(Queues.statusUpdates, message, { persistent: true });
        return;
      } catch (error) {
        if (attempt >= delays.length) {
          options.logger.warn(`Failed to publish status ${status} for occurrence ${this.occurrenceId}.`, error);
          return;
        }

        await new Promise(resolve => setTimeout(resolve, delays[attempt]));
      }
    }
  }
}

export function parseJob(body: Buffer): ScheduledJobMessage | null {
  try {
    const job = JSON.parse(body.toString('utf8')) as ScheduledJobMessage;

    if (!job || typeof job !== 'object' || typeof job.id !== 'string' || typeof job.jobNameInWorker !== 'string')
      return null;

    return job;
  } catch {
    return null;
  }
}

/** Reads a header that may arrive as bytes or as a string. */
export function headerString(value: unknown): string | undefined {
  if (typeof value === 'string')
    return value;

  if (Buffer.isBuffer(value))
    return value.toString('utf8');

  return undefined;
}

/** Occurrence id from the OccurrenceId header, the CorrelationId header or the correlation id property. */
export function parseOccurrenceId(headers: Record<string, unknown> | undefined, correlationId: string | undefined): string | undefined {
  const candidates = [headerString(headers?.OccurrenceId), headerString(headers?.CorrelationId), correlationId];

  return candidates.find(candidate => candidate !== undefined && GuidPattern.test(candidate.trim()))?.trim();
}

export function parseRetryCount(headers: Record<string, unknown> | undefined): number {
  const value = headers?.['x-retry-count'];

  if (typeof value === 'number' && Number.isFinite(value))
    return Math.max(0, Math.trunc(value));

  if (typeof value === 'bigint')
    return Math.max(0, Number(value));

  // The .NET SDK reads a 4-byte value as a little-endian int.
  if (Buffer.isBuffer(value) && value.length >= 4)
    return Math.max(0, value.readInt32LE(0));

  if (typeof value === 'string' && /^\d+$/.test(value))
    return Number(value);

  return 0;
}

function serializeResult(value: unknown, jobName: string): string {
  if (value === undefined || value === null)
    return `Job ${jobName} completed successfully`;

  return typeof value === 'string' ? value : JSON.stringify(value);
}

function safe(action: () => void, logger: Logger): void {
  try {
    action();
  } catch (error) {
    // An ack or nack on a channel that has closed: the broker redelivers the message.
    logger.warn('Could not settle a message; the broker will redeliver it.', error);
  }
}

function abortableSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted)
      return reject(signal.reason);

    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, ms);

    const onAbort = () => {
      clearTimeout(timer);
      reject(signal.reason);
    };

    signal.addEventListener('abort', onAbort, { once: true });
  });
}
