import amqp, { type Channel, type ChannelModel, type ConfirmChannel, type ConsumeMessage, type RecoveringChannelModel } from 'amqplib';
import os from 'node:os';
import { CancellationListener, CancellationRegistry } from './cancellation.ts';
import { LogBuffer } from './log-buffer.ts';
import { localIpAddress, ResourceSampler } from './metrics.ts';
import { resolveJobOptions, resolveWorkerOptions, type JobOptions, type Logger, type ResolvedWorkerOptions, type WorkerOptions } from './options.ts';
import { JobProcessor, type JobHandler, type Publisher, type PublishOptions, type RegisteredJob } from './processor.ts';
import {
  DeadLetterRoutingKey,
  Exchanges,
  Queues,
  SdkVersion,
  type WorkerDiscoveryRequest,
  type WorkerHeartbeatMessage,
  type WorkerMetadata,
} from './protocol.ts';
import { queueNameForPattern } from './routing.ts';

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
  private readonly publisher = new AmqpPublisher();
  private readonly sampler = new ResourceSampler();
  private readonly logs: LogBuffer;
  private readonly processor: JobProcessor;
  private connection: RecoveringChannelModel | null = null;
  private consumers: Array<{ channel: Channel; consumerTag: string }> = [];
  private cancellationListener: CancellationListener | null = null;
  private heartbeatTimer: NodeJS.Timeout | null = null;
  private lastImmediateHeartbeat = 0;
  private pendingHeartbeat: NodeJS.Timeout | null = null;
  private state: State = 'idle';
  private stopPromise: Promise<void> | null = null;
  private lastConnectError: Error | null = null;
  private readonly closedModels = new WeakSet<ChannelModel>();

  constructor(options: WorkerOptions) {
    this.options = resolveWorkerOptions(options);
    this.logger = this.options.logger;
    this.logs = new LogBuffer(batch => this.publisher.sendToQueue(Queues.workerLogs, batch, { persistent: true }), this.logger);
    this.processor = new JobProcessor({
      options: this.options,
      jobs: this.jobs,
      publisher: this.publisher,
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

  /** Connects, registers this instance and starts consuming. Rejects if RabbitMQ cannot be reached in time. */
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

      await this.connect();
    } catch (error) {
      this.state = 'stopped';
      await this.cancellationListener?.stop();
      await this.connection?.close().catch(() => undefined);
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

  private async connect(): Promise<void> {
    const connection = await amqp.connect(withHeartbeat(this.options.rabbitmqUrl, this.options.amqpHeartbeatSeconds), {
      clientProperties: { connection_name: this.options.instanceId },
      recovery: {
        initialDelay: 1000,
        maxDelay: 10_000,
        waitForConnect: false,
        setup: (model: ChannelModel) => this.setup(model),
      },
    });

    this.connection = connection;

    connection.on('error', error => this.logger.warn(`RabbitMQ connection error: ${error.message}`));
    connection.on('connect-failed', error => {
      this.lastConnectError = error;
      this.logger.warn(`Could not connect to RabbitMQ: ${error.message}`);
    });
    connection.on('disconnect', error => {
      this.publisher.detach();
      this.consumers = [];
      this.logger.warn(`Lost the RabbitMQ connection (${error.message}). Reconnecting...`);
    });
    connection.on('connect', () => this.logger.info('Connected to RabbitMQ.'));

    const timeoutMessage = () => `Could not connect to RabbitMQ within ${this.options.startupTimeoutSeconds}s${this.lastConnectError ? `: ${this.lastConnectError.message}` : '.'}`;

    await withTimeout(connection.waitForConnect(), this.options.startupTimeoutSeconds * 1000, timeoutMessage);
  }

  /** Runs on every (re)connection: declares the topology, starts the consumers and registers. */
  private async setup(model: ChannelModel): Promise<void> {
    const queueArgs = this.options.queueType === 'quorum' ? { 'x-queue-type': 'quorum' } : undefined;
    const publishChannel = await model.createConfirmChannel();

    model.once('close', () => this.closedModels.add(model));

    publishChannel.on('error', error => this.logger.warn(`RabbitMQ publish channel error: ${error.message}`));
    publishChannel.on('close', () => this.onChannelLost(model, publishChannel, 'publish'));

    for (const queue of [Queues.statusUpdates, Queues.workerLogs, Queues.workerHeartbeat, Queues.workerRegistration])
      await publishChannel.assertQueue(queue, { durable: true, arguments: queueArgs });

    await publishChannel.assertExchange(Exchanges.jobs, 'topic', { durable: true });
    await publishChannel.assertExchange(Exchanges.deadLetter, 'direct', { durable: true });
    await publishChannel.assertQueue(Queues.failedOccurrences, { durable: true, arguments: queueArgs });
    await publishChannel.bindQueue(Queues.failedOccurrences, Exchanges.deadLetter, DeadLetterRoutingKey);

    // Before consuming, so the first delivery can already report its status.
    this.publisher.attach(publishChannel);

    const consumers: Array<{ channel: Channel; consumerTag: string }> = [];

    for (const job of this.jobs.values()) {
      const channel = await model.createChannel();

      channel.on('error', error => this.logger.warn(`RabbitMQ channel error on ${job.queue}: ${error.message}`));
      channel.on('close', () => this.onChannelLost(model, channel, job.queue));

      await channel.prefetch(job.options.maxParallelJobs);
      await channel.assertQueue(job.queue, {
        durable: true,
        arguments: { 'x-dead-letter-exchange': Exchanges.deadLetter, 'x-dead-letter-routing-key': DeadLetterRoutingKey, ...queueArgs },
      });
      await channel.bindQueue(job.queue, Exchanges.jobs, job.options.routingPattern);

      // A reconnect while stopping restores publishing for running jobs, but takes no new ones.
      if (this.state === 'stopping' || this.state === 'stopped')
        continue;

      const { consumerTag } = await channel.consume(job.queue, message => this.onMessage(model, channel, job, message), { noAck: false });

      consumers.push({ channel, consumerTag });
      this.logger.info(`Consuming ${job.name} from ${job.queue} (pattern ${job.options.routingPattern}, prefetch ${job.options.maxParallelJobs}).`);
    }

    this.consumers = consumers;

    await this.publisher.sendToQueue(Queues.workerRegistration, this.buildRegistration(), { persistent: true });
    await this.sendHeartbeat(false);

    this.logger.info(`Registered ${this.options.workerId} (instance ${this.options.instanceId}).`);
  }

  private onMessage(model: ChannelModel, channel: Channel, job: RegisteredJob, message: ConsumeMessage | null): void {
    if (message === null) {
      // The broker cancelled the consumer, e.g. because the queue was deleted. Reconnect to redeclare it.
      this.logger.warn(`RabbitMQ cancelled the consumer of ${job.queue}. Reconnecting to restore it.`);
      void model.close().catch(() => undefined);
      return;
    }

    void this.processor.handle({
      body: message.content,
      headers: message.properties.headers,
      correlationId: message.properties.correlationId,
      redelivered: message.fields.redelivered,
      ack: () => channel.ack(message),
      nack: requeue => channel.nack(message, false, requeue),
    }, job);
  }

  /**
   * A channel can close while its connection stays up (for example after a protocol error), and
   * automatic recovery only reacts to connection loss. Closing the connection makes it rebuild everything.
   * Channels also close when the connection drops; that case is left to recovery.
   */
  private onChannelLost(model: ChannelModel, channel: Channel | ConfirmChannel, name: string): void {
    setTimeout(() => {
      if (this.state === 'stopping' || this.state === 'stopped' || this.closedModels.has(model))
        return;

      const owned = channel === this.publisher.current || this.consumers.some(c => c.channel === channel);

      if (!owned)
        return;

      this.logger.warn(`RabbitMQ channel ${name} closed while the connection stayed up. Reconnecting.`);
      this.publisher.detach();
      void model.close().catch(() => undefined);
    }, 250).unref();
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
      await this.publisher.sendToQueue(Queues.workerHeartbeat, heartbeat, { persistent: false });
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

    for (const { channel, consumerTag } of this.consumers)
      await channel.cancel(consumerTag).catch(() => undefined);

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
    await this.connection?.close().catch(() => undefined);

    this.publisher.detach();
    this.state = 'stopped';
    this.logger.info(`Worker instance ${this.options.instanceId} stopped.`);
  }
}

/** Publishes on the current confirm channel and resolves once the broker has accepted the message. */
class AmqpPublisher implements Publisher {
  private channel: ConfirmChannel | null = null;

  get current(): ConfirmChannel | null {
    return this.channel;
  }

  attach(channel: ConfirmChannel): void {
    this.channel = channel;
  }

  detach(): void {
    this.channel = null;
  }

  sendToQueue(queue: string, body: Buffer | object, options?: PublishOptions): Promise<void> {
    return this.publish('', queue, body, options);
  }

  publish(exchange: string, routingKey: string, body: Buffer | object, options: PublishOptions = {}): Promise<void> {
    const channel = this.channel;

    if (!channel)
      return Promise.reject(new Error('Not connected to RabbitMQ.'));

    const content = Buffer.isBuffer(body) ? body : Buffer.from(JSON.stringify(body), 'utf8');

    return new Promise((resolve, reject) => {
      try {
        channel.publish(exchange, routingKey, content, {
          persistent: options.persistent ?? true,
          contentType: 'application/json',
          correlationId: options.correlationId,
          headers: toAmqpHeaders(options.headers),
        }, error => (error ? reject(error) : resolve()));
      } catch (error) {
        reject(error);
      }
    });
  }
}

/** Integers are sent as 32-bit ints, the type the .NET side expects for retry counts. */
function toAmqpHeaders(headers: Record<string, string | number> | undefined): Record<string, unknown> | undefined {
  if (!headers)
    return undefined;

  return Object.fromEntries(Object.entries(headers).map(([key, value]) =>
    [key, typeof value === 'number' && Number.isInteger(value) ? { '!': 'int', value } : value]));
}

/** Adds the AMQP heartbeat to the URL unless it already sets one. */
function withHeartbeat(url: string, seconds: number): string {
  const parsed = new URL(url);

  if (!parsed.searchParams.has('heartbeat'))
    parsed.searchParams.set('heartbeat', String(seconds));

  return parsed.toString();
}

function withTimeout<T>(promise: Promise<T>, ms: number, message: string | (() => string)): Promise<T> {
  let timer: NodeJS.Timeout | undefined;

  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(typeof message === 'function' ? message() : message)), ms);
  });

  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
