import amqp, { type Channel, type ChannelModel, type ConfirmChannel, type ConsumeMessage, type RecoveringChannelModel } from 'amqplib';
import type { Logger, ResolvedWorkerOptions } from './options.ts';
import { RabbitMqJobOutput, type JobOutput, type Publisher, type PublishOptions, type RegisteredJob } from './processor.ts';
import {
  DeadLetterRoutingKey,
  Exchanges,
  Queues,
  type WorkerDiscoveryRequest,
  type WorkerHeartbeatMessage,
  type WorkerLogBatchMessage,
} from './protocol.ts';
import { withTimeout, type TransportHooks, type WorkerTransport } from './transport.ts';

/** RabbitMQ: one queue per job type, bound to the jobs topic exchange, with automatic reconnects. */
export class RabbitMqTransport implements WorkerTransport {
  readonly output: JobOutput;
  private readonly options: ResolvedWorkerOptions;
  private readonly jobs: ReadonlyMap<string, RegisteredJob>;
  private readonly hooks: TransportHooks;
  private readonly logger: Logger;
  private readonly publisher = new AmqpPublisher();
  private readonly closedModels = new WeakSet<ChannelModel>();
  private connection: RecoveringChannelModel | null = null;
  private consumers: Array<{ channel: Channel; consumerTag: string }> = [];
  private lastConnectError: Error | null = null;

  constructor(options: ResolvedWorkerOptions, jobs: ReadonlyMap<string, RegisteredJob>, hooks: TransportHooks) {
    this.options = options;
    this.jobs = jobs;
    this.hooks = hooks;
    this.logger = options.logger;
    this.output = new RabbitMqJobOutput(this.publisher);
  }

  publishRegistration(message: WorkerDiscoveryRequest): Promise<void> {
    return this.publisher.sendToQueue(Queues.workerRegistration, message, { persistent: true });
  }

  publishHeartbeat(message: WorkerHeartbeatMessage): Promise<void> {
    return this.publisher.sendToQueue(Queues.workerHeartbeat, message, { persistent: false });
  }

  publishLogs(batch: WorkerLogBatchMessage): Promise<void> {
    return this.publisher.sendToQueue(Queues.workerLogs, batch, { persistent: true });
  }

  async connect(): Promise<void> {
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

  async stopConsuming(): Promise<void> {
    for (const { channel, consumerTag } of this.consumers)
      await channel.cancel(consumerTag).catch(() => undefined);
  }

  async close(): Promise<void> {
    await this.connection?.close().catch(() => undefined);
    this.publisher.detach();
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
      if (this.hooks.isStopping())
        continue;

      const { consumerTag } = await channel.consume(job.queue, message => this.onMessage(model, channel, job, message), { noAck: false });

      consumers.push({ channel, consumerTag });
      this.logger.info(`Consuming ${job.name} from ${job.queue} (pattern ${job.options.routingPattern}, prefetch ${job.options.maxParallelJobs}).`);
    }

    this.consumers = consumers;

    await this.hooks.onReady();
  }

  private onMessage(model: ChannelModel, channel: Channel, job: RegisteredJob, message: ConsumeMessage | null): void {
    if (message === null) {
      // The broker cancelled the consumer, e.g. because the queue was deleted. Reconnect to redeclare it.
      this.logger.warn(`RabbitMQ cancelled the consumer of ${job.queue}. Reconnecting to restore it.`);
      void model.close().catch(() => undefined);
      return;
    }

    void this.hooks.handle({
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
      if (this.hooks.isStopping() || this.closedModels.has(model))
        return;

      const owned = channel === this.publisher.current || this.consumers.some(c => c.channel === channel);

      if (!owned)
        return;

      this.logger.warn(`RabbitMQ channel ${name} closed while the connection stayed up. Reconnecting.`);
      this.publisher.detach();
      void model.close().catch(() => undefined);
    }, 250).unref();
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
