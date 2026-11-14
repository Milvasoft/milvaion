import os from 'node:os';
import { CancellationListener, CancellationRegistry } from './cancellation.ts';
import { KafkaTransport } from './kafka/kafka-transport.ts';
import { LogBuffer } from './log-buffer.ts';
import { localIpAddress, ResourceSampler } from './metrics.ts';
import { resolveJobOptions, resolveWorkerOptions, type JobOptions, type Logger, type ResolvedWorkerOptions, type WorkerOptions } from './options.ts';
import { JobProcessor, type JobHandler, type RegisteredJob } from './processor.ts';
import { SdkVersion, type WorkerDiscoveryRequest, type WorkerHeartbeatMessage, type WorkerMetadata } from './protocol.ts';
import { RabbitMqTransport } from './rabbitmq-transport.ts';
import { queueNameForPattern } from './routing.ts';
import { withTimeout, type TransportHooks, type WorkerTransport } from './transport.ts';

type State = 'idle' | 'starting' | 'running' | 'stopping' | 'stopped';

/**
 * A Milvaion worker: register jobs with `addJob`, then `start()`. Call `stop()` on SIGTERM so
 * running jobs can finish and the dashboard sees the instance leave.
 */
export class MilvaionWorker {
  private readonly options: ResolvedWorkerOptions;
  private readonly logger: Logger;
  private readonly jobs = new Map<string, RegisteredJob>();
  private readonly cancellations = new CancellationRegistry();
  private readonly sampler = new ResourceSampler();
  private readonly transport: WorkerTransport;
  private readonly logs: LogBuffer;
  private readonly processor: JobProcessor;
  private cancellationListener: CancellationListener | null = null;
  private heartbeatTimer: NodeJS.Timeout | null = null;
  private lastImmediateHeartbeat = 0;
  private pendingHeartbeat: NodeJS.Timeout | null = null;
  private state: State = 'idle';
  private stopPromise: Promise<void> | null = null;

  constructor(options: WorkerOptions) {
    this.options = resolveWorkerOptions(options);
    this.logger = this.options.logger;

    const hooks: TransportHooks = {
      handle: (delivery, source) => this.processor.handle(delivery, source),
      onReady: () => this.register(),
      isStopping: () => this.state === 'stopping' || this.state === 'stopped',
    };

    this.transport = this.options.transport === 'kafka'
      ? new KafkaTransport(this.options, hooks)
      : new RabbitMqTransport(this.options, this.jobs, hooks);
    this.logs = new LogBuffer(batch => this.transport.publishLogs(batch), this.logger);
    this.processor = new JobProcessor({
      options: this.options,
      jobs: this.jobs,
      output: this.transport.output,
      logs: this.logs,
      cancellations: this.cancellations,
      onJobCountChanged: () => this.sendImmediateHeartbeat(),
    });
  }

  get workerId(): string {
    return this.options.workerId;
  }

  get instanceId(): string {
    return this.options.instanceId;
  }

  /** Number of jobs this instance is running now. */
  get runningJobs(): number {
    return this.processor.running;
  }

  /**
   * Registers a job under the name the dashboard dispatches it by (`JobNameInWorker`).
   * The handler's return value becomes the occurrence result; throw `PermanentJobError` to skip retries.
   */
  addJob<TData = any, TResult = unknown>(name: string, handler: JobHandler<TData, TResult>, options?: JobOptions): this {
    if (this.state !== 'idle')
      throw new Error('Jobs must be added before start().');

    if (!name?.trim())
      throw new Error('Job name is required.');

    const key = name.trim().toLowerCase();

    if (this.jobs.has(key))
      throw new Error(`Job '${name}' is already registered. Job names must be unique within a worker (case-insensitive).`);

    const resolved = resolveJobOptions(name.trim(), options, this.options);

    this.jobs.set(key, { name: name.trim(), handler: handler as JobHandler, options: resolved, queue: queueNameForPattern(resolved.routingPattern) });

    return this;
  }

  /** Connects, registers this instance and starts consuming. Rejects if the broker cannot be reached in time. */
  async start(): Promise<void> {
    if (this.state !== 'idle')
      throw new Error(`Worker cannot be started from state '${this.state}'.`);

    if (this.jobs.size === 0)
      throw new Error('Add at least one job with addJob() before start().');

    this.state = 'starting';
    this.logger.info(`Starting worker ${this.options.workerId} (instance ${this.options.instanceId}) with ${this.jobs.size} job type(s).`);

    try {
      if (this.options.redis) {
        this.cancellationListener = new CancellationListener(this.options.redis, this.cancellations, this.logger);
        await withTimeout(this.cancellationListener.start(), this.options.startupTimeoutSeconds * 1000, 'Timed out connecting to Redis.');
      } else {
        this.logger.warn('Redis is disabled: jobs cannot be cancelled from the dashboard.');
      }

      await this.transport.connect();
    } catch (error) {
      this.state = 'stopped';
      await this.cancellationListener?.stop();
      await this.transport.close().catch(() => undefined);
      throw error;
    }

    this.heartbeatTimer = setInterval(() => void this.sendHeartbeat(false), this.options.heartbeatIntervalSeconds * 1000);
    this.state = 'running';
  }

  /**
   * Stops taking new jobs, lets running ones finish for up to `shutdownTimeoutSeconds`, returns
   * the rest to the queue, and disconnects. Safe to call more than once.
   */
  stop(): Promise<void> {
    if (this.state === 'idle' || this.state === 'stopped')
      return Promise.resolve();

    this.stopPromise ??= this.shutdown();

    return this.stopPromise;
  }

  /** Runs once the transport is ready, after every (re)connection. */
  private async register(): Promise<void> {
    await this.transport.publishRegistration(this.buildRegistration());
    await this.sendHeartbeat(false);

    this.logger.info(`Registered ${this.options.workerId} (instance ${this.options.instanceId}).`);
  }

  private buildRegistration(): WorkerDiscoveryRequest {
    const jobs = [...this.jobs.values()];
    const metadata: WorkerMetadata = {
      IsExternal: false,
      ExternalScheduler: null,
      ProcessorCount: os.availableParallelism(),
      OSVersion: `${os.type()} ${os.release()}`,
      RuntimeVersion: `Node.js ${process.version}`,
      HeartbeatInterval: this.options.heartbeatIntervalSeconds,
      Sdk: 'node',
      SdkVersion,
      JobConfigs: jobs.map(job => ({
        JobType: job.name,
        ConsumerId: job.options.consumerId,
        MaxParallelJobs: job.options.maxParallelJobs,
        ExecutionTimeoutSeconds: job.options.executionTimeoutSeconds,
      })),
    };

    const schemas = (pick: (job: RegisteredJob) => object | undefined) =>
      Object.fromEntries(jobs.flatMap(job => {
        const schema = pick(job);

        return schema ? [[job.name, JSON.stringify(schema)]] : [];
      }));

    return {
      workerId: this.options.workerId,
      instanceId: this.options.instanceId,
      displayName: `${this.options.workerId} (${this.options.instanceId})`,
      hostName: os.hostname(),
      ipAddress: localIpAddress(),
      routingPatterns: Object.fromEntries(jobs.map(job => [job.name, job.options.routingPattern])),
      jobDataDefinitions: schemas(job => job.options.dataSchema),
      jobResultDefinitions: schemas(job => job.options.resultSchema),
      jobTypes: jobs.map(job => job.name),
      maxParallelJobs: this.options.maxParallelJobs,
      version: this.options.version,
      metadata: JSON.stringify(metadata),
    };
  }

  private async sendHeartbeat(isStopping: boolean): Promise<void> {
    const heartbeat: WorkerHeartbeatMessage = {
      workerId: this.options.workerId,
      instanceId: this.options.instanceId,
      currentJobs: isStopping ? 0 : this.processor.running,
      memoryBytes: this.sampler.memoryBytes(),
      cpuUsagePercent: this.sampler.cpuUsagePercent(),
      timestamp: new Date().toISOString(),
      isStopping,
    };

    try {
      await this.transport.publishHeartbeat(heartbeat);
    } catch (error) {
      this.logger.debug(`Heartbeat failed; will retry on the next interval: ${(error as Error).message}`);
    }
  }

  /** Reports a changed job count at most once a second; the last change is always sent. */
  private sendImmediateHeartbeat(): void {
    if (this.state !== 'running' && this.state !== 'stopping')
      return;

    if (this.pendingHeartbeat)
      return;

    const wait = Math.max(0, this.lastImmediateHeartbeat + 1000 - Date.now());

    this.pendingHeartbeat = setTimeout(() => {
      this.pendingHeartbeat = null;
      this.lastImmediateHeartbeat = Date.now();
      void this.sendHeartbeat(false);
    }, wait);

    this.pendingHeartbeat.unref();
  }

  private async shutdown(): Promise<void> {
    this.state = 'stopping';
    this.logger.info(`Stopping worker instance ${this.options.instanceId}...`);

    await this.transport.stopConsuming();

    this.processor.beginShutdown();

    const finished = await this.processor.waitForIdle(this.options.shutdownTimeoutSeconds * 1000);

    if (!finished) {
      this.logger.warn(`${this.processor.running} job(s) still running after ${this.options.shutdownTimeoutSeconds}s. Returning them to the queue.`);
      this.cancellations.abortAll('shutdown', 'Worker is shutting down.');
      await this.processor.waitForIdle(10_000);
    }

    if (this.heartbeatTimer)
      clearInterval(this.heartbeatTimer);

    if (this.pendingHeartbeat)
      clearTimeout(this.pendingHeartbeat);

    await this.sendHeartbeat(true);
    await this.logs.flush().catch(error => this.logger.warn('Some job logs could not be sent before shutdown.', error));
    this.logs.dispose();

    await this.cancellationListener?.stop();
    await this.transport.close().catch(error => this.logger.warn('Error while disconnecting from the broker.', error));

    this.state = 'stopped';
    this.logger.info(`Worker instance ${this.options.instanceId} stopped.`);
  }
}
