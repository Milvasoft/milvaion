import {
  Kafka,
  logLevel,
  Partitioners,
  type Consumer,
  type EachBatchPayload,
  type KafkaMessage,
  type LogEntry,
  type Message,
  type Producer,
  type SASLOptions,
} from 'kafkajs';
import type { Logger, ResolvedKafkaOptions, ResolvedWorkerOptions } from '../options.ts';
import { parseJob, type DeadLetterRequest, type Delivery, type JobOutput, type RetryRequest } from '../processor.ts';
import {
  JobOccurrenceStatus,
  type JobStatusUpdateMessage,
  type WorkerDiscoveryRequest,
  type WorkerHeartbeatMessage,
  type WorkerLogBatchMessage,
} from '../protocol.ts';
import { withTimeout, type TransportHooks, type WorkerTransport } from '../transport.ts';
import { decodeKafkaHeaders, encodeKafkaHeaders, KafkaHeaders, kafkaNotBefore, kafkaOccurrenceId, type RawKafkaHeaders } from './headers.ts';
import { PartitionOffsetTracker } from './offset-tracker.ts';
import { kafkaTopicNames, type KafkaTopicNames } from './topics.ts';

const CommitIntervalMs = 500;

/** Consumption state of one assigned partition. */
interface PartitionState {
  readonly key: string;
  readonly topic: string;
  readonly partition: number;
  readonly tracker: PartitionOffsetTracker;
  /** Jobs of this partition running now. */
  running: number;
  /** Why the partition is paused: all slots busy, or the head retry record is not due yet. */
  paused?: 'capacity' | 'hold';
  holdTimer?: NodeJS.Timeout;
  holdHeartbeat?: NodeJS.Timeout;
}

/**
 * Kafka: one job topic and one retry topic per worker, read by a consumer group shared by every
 * instance. See docs/githubdocs/KAFKA-TRANSPORT.md for the wire contract.
 *
 * Records of a partition are dispatched in order but may run in parallel, so offsets are committed
 * through an ordered tracker: only the end of the contiguous finished range of each partition.
 */
export class KafkaTransport implements WorkerTransport {
  readonly output: JobOutput;
  readonly topics: KafkaTopicNames;
  private readonly options: ResolvedWorkerOptions;
  private readonly kafkaOptions: ResolvedKafkaOptions;
  private readonly hooks: TransportHooks;
  private readonly logger: Logger;
  private readonly partitions = new Map<string, PartitionState>();
  private kafka: Kafka | null = null;
  private producer: Producer | null = null;
  private consumer: Consumer | null = null;
  private commitTimer: NodeJS.Timeout | null = null;
  private commitChain: Promise<void> = Promise.resolve();
  private running = 0;
  private stopping = false;
  private closed = false;

  constructor(options: ResolvedWorkerOptions, hooks: TransportHooks) {
    if (!options.kafka)
      throw new Error('Kafka options are missing.');

    this.options = options;
    this.kafkaOptions = options.kafka;
    this.hooks = hooks;
    this.logger = options.logger;
    this.topics = kafkaTopicNames(options.workerId, options.kafka.topicPrefix);
    this.output = {
      retryDelay: 'broker',
      publishStatus: message => this.publishStatus(message),
      publishRetry: request => this.publishRetry(request),
      publishDeadLetter: request => this.publishDeadLetter(request),
    };
  }

  publishRegistration(message: WorkerDiscoveryRequest): Promise<void> {
    return this.send(this.topics.registrations, [{ key: message.instanceId, value: JSON.stringify(message) }]);
  }

  publishHeartbeat(message: WorkerHeartbeatMessage): Promise<void> {
    return this.send(this.topics.heartbeats, [{ key: message.instanceId, value: JSON.stringify(message) }]);
  }

  publishLogs(batch: WorkerLogBatchMessage): Promise<void> {
    return this.send(this.topics.logs, [{ key: batch.logs[0]?.occurrenceId ?? null, value: JSON.stringify(batch) }]);
  }

  async connect(): Promise<void> {
    const { brokers, ssl, sasl } = this.kafkaOptions;

    this.kafka = new Kafka({
      clientId: this.options.instanceId,
      brokers,
      ssl,
      sasl: sasl as SASLOptions | undefined,
      connectionTimeout: 10_000,
      retry: { initialRetryTime: 300, maxRetryTime: 10_000, retries: 10 },
      logLevel: logLevel.INFO,
      logCreator: () => entry => this.log(entry),
    });

    await withTimeout(this.setup(this.kafka), this.options.startupTimeoutSeconds * 1000,
      `Could not connect to Kafka (${brokers.join(', ')}) within ${this.options.startupTimeoutSeconds}s.`);
  }

  async stopConsuming(): Promise<void> {
    this.stopping = true;

    for (const state of this.partitions.values())
      this.clearHold(state);

    try {
      this.consumer?.pause([{ topic: this.topics.jobs }, { topic: this.topics.retry }]);
    } catch (error) {
      this.logger.debug('Could not pause the Kafka consumer.', error);
    }
  }

  async close(): Promise<void> {
    if (this.closed)
      return;

    this.closed = true;
    this.stopping = true;

    if (this.commitTimer)
      clearInterval(this.commitTimer);

    for (const state of this.partitions.values())
      this.clearHold(state);

    await this.commit();
    await this.consumer?.disconnect().catch(error => this.logger.debug('Kafka consumer disconnect failed.', error));
    await this.producer?.disconnect().catch(error => this.logger.debug('Kafka producer disconnect failed.', error));

    this.consumer = null;
    this.producer = null;
  }

  private async setup(kafka: Kafka): Promise<void> {
    if (this.kafkaOptions.autoCreateTopics)
      await this.createTopics(kafka);

    const producer = kafka.producer({ allowAutoTopicCreation: false, createPartitioner: Partitioners.DefaultPartitioner });

    this.producer = producer;
    await producer.connect();

    const consumer = kafka.consumer({
      groupId: this.topics.group,
      allowAutoTopicCreation: false,
      sessionTimeout: 45_000,
      rebalanceTimeout: 60_000,
      heartbeatInterval: 3_000,
      maxWaitTimeInMs: 500,
    });

    this.consumer = consumer;

    consumer.on(consumer.events.GROUP_JOIN, event => this.onAssigned(event.payload.memberAssignment));
    // Commit what has finished before the partitions move; the generation is still valid at this point.
    consumer.on(consumer.events.REBALANCING, () => void this.commit());
    consumer.on(consumer.events.CRASH, event => {
      if (!this.stopping)
        this.logger.warn(`Kafka consumer crashed (${event.payload.error?.message}); restart: ${event.payload.restart}.`);
    });

    await consumer.connect();
    await consumer.subscribe({ topics: [this.topics.jobs, this.topics.retry], fromBeginning: true });

    // Registered before consuming, so the dashboard knows the instance when its first job reports.
    await this.hooks.onReady();

    await consumer.run({
      autoCommit: false,
      eachBatchAutoResolve: false,
      eachBatch: payload => this.onBatch(payload),
    });

    this.commitTimer = setInterval(() => void this.commit(), CommitIntervalMs);
    this.commitTimer.unref();

    this.logger.info(`Consuming ${this.topics.jobs} and ${this.topics.retry} in group ${this.topics.group}.`);
  }

  /** Creates the worker's topics and the shared topics it produces to. A topic that exists is left as it is. */
  private async createTopics(kafka: Kafka): Promise<void> {
    const admin = kafka.admin();
    const { numPartitions, replicationFactor } = this.kafkaOptions;
    const { jobs, retry, status, logs, heartbeats, registrations, dlq } = this.topics;

    await admin.connect();

    try {
      const existing = new Set(await admin.listTopics());
      const missing = [jobs, retry, status, logs, heartbeats, registrations, dlq].filter(topic => !existing.has(topic));

      if (missing.length === 0)
        return;

      try {
        await admin.createTopics({ waitForLeaders: true, topics: missing.map(topic => ({ topic, numPartitions, replicationFactor })) });
        this.logger.info(`Created Kafka topics: ${missing.join(', ')}.`);
      } catch (error) {
        // Another instance or the API created them at the same time.
        if (!isAlreadyExists(error))
          throw error;
      }
    } finally {
      await admin.disconnect().catch(() => undefined);
    }
  }

  private async onBatch({ batch, resolveOffset, heartbeat, isRunning, isStale }: EachBatchPayload): Promise<void> {
    const state = this.partitionState(batch.topic, batch.partition);

    // A batch fetched before the partition was paused. Unresolved records are fetched again on resume.
    if (this.stopping || state.paused)
      return;

    for (const message of batch.messages) {
      if (!isRunning() || isStale() || this.stopping)
        break;

      const offset = BigInt(message.offset);

      // A rebalance that kept this partition makes records come again from the committed offset.
      if (state.tracker.isDispatched(offset)) {
        resolveOffset(message.offset);
        continue;
      }

      const headers = decodeKafkaHeaders(message.headers as RawKafkaHeaders | undefined);

      if (state.topic === this.topics.retry) {
        const notBefore = kafkaNotBefore(headers);

        if (notBefore !== undefined && notBefore > Date.now()) {
          this.hold(state, message, headers, notBefore);
          break;
        }
      }

      if (this.running >= this.options.maxParallelJobs || state.running >= this.kafkaOptions.maxInFlightPerPartition) {
        this.pause(state, 'capacity');
        break;
      }

      state.tracker.add(offset);
      resolveOffset(message.offset);
      this.dispatch(state, message, headers, offset);
    }

    await heartbeat();
  }

  private dispatch(state: PartitionState, message: KafkaMessage, headers: Record<string, string>, offset: bigint): void {
    let finished = false;

    const finish = () => {
      if (!finished) {
        finished = true;
        state.tracker.complete(offset);
      }
    };

    const delivery: Delivery = {
      body: message.value ?? Buffer.alloc(0),
      headers,
      redelivered: false,
      ack: finish,
      nack: async (requeue: boolean, reason?: string) => {
        if (requeue) {
          // Kafka has no requeue: the record is written again to the end of its own topic.
          await this.send(state.topic, [{ key: message.key, value: message.value, headers: message.headers }]);
        } else {
          await this.send(this.topics.dlq, [{
            key: kafkaOccurrenceId(headers) ?? message.key,
            value: message.value,
            headers: { ...message.headers, [KafkaHeaders.originalTopic]: state.topic, [KafkaHeaders.dlqReason]: reason ?? 'Rejected by the worker.' },
          }]);
        }

        finish();
      },
    };

    this.running++;
    state.running++;

    void this.hooks.handle(delivery, { queue: state.topic }).finally(() => {
      this.running--;
      state.running--;
      this.resumeAfterCapacity();
    });
  }

  private pause(state: PartitionState, reason: 'capacity' | 'hold'): void {
    state.paused = reason;
    this.consumer?.pause([{ topic: state.topic, partitions: [state.partition] }]);
  }

  private resume(state: PartitionState): void {
    state.paused = undefined;

    if (!this.stopping && this.partitions.get(state.key) === state)
      this.consumer?.resume([{ topic: state.topic, partitions: [state.partition] }]);
  }

  /** A slot has become free: partitions paused for capacity may fetch again. */
  private resumeAfterCapacity(): void {
    if (this.stopping || this.running >= this.options.maxParallelJobs)
      return;

    for (const state of this.partitions.values()) {
      if (state.paused === 'capacity' && state.running < this.kafkaOptions.maxInFlightPerPartition)
        this.resume(state);
    }
  }

  /**
   * The head record of a retry partition is not due: pause the partition until it is, and keep the
   * occurrence's job heartbeat going meanwhile, since it is still Running.
   */
  private hold(state: PartitionState, message: KafkaMessage, headers: Record<string, string>, notBefore: number): void {
    this.pause(state, 'hold');

    const occurrenceId = kafkaOccurrenceId(headers);
    const job = parseJob(message.value ?? Buffer.alloc(0));
    const heartbeatMs = this.options.jobHeartbeatIntervalSeconds * 1000;

    if (occurrenceId && job && heartbeatMs > 0) {
      state.holdHeartbeat = setInterval(() => void this.publishRunning(occurrenceId, job.id), heartbeatMs);
      state.holdHeartbeat.unref();
    }

    state.holdTimer = setTimeout(() => {
      this.clearHold(state);
      this.resume(state);
    }, Math.max(0, notBefore - Date.now()));
    state.holdTimer.unref();
  }

  private clearHold(state: PartitionState): void {
    clearTimeout(state.holdTimer);
    clearInterval(state.holdHeartbeat);
    state.holdTimer = undefined;
    state.holdHeartbeat = undefined;
  }

  /** Drops the state of partitions this instance no longer owns and re-applies pauses after a rebalance. */
  private onAssigned(assignment: Record<string, number[]>): void {
    const assigned = new Set(Object.entries(assignment).flatMap(([topic, partitions]) => partitions.map(partition => partitionKey(topic, partition))));

    for (const [key, state] of this.partitions) {
      if (!assigned.has(key)) {
        // Jobs still running here finish and report, but their offsets are the new owner's now.
        this.clearHold(state);
        this.partitions.delete(key);
      } else if (state.paused) {
        this.consumer?.pause([{ topic: state.topic, partitions: [state.partition] }]);
      }
    }

    if (this.stopping)
      this.consumer?.pause([{ topic: this.topics.jobs }, { topic: this.topics.retry }]);

    this.logger.debug(`Kafka partitions assigned: ${[...assigned].join(', ') || 'none'}.`);
  }

  private partitionState(topic: string, partition: number): PartitionState {
    const key = partitionKey(topic, partition);
    let state = this.partitions.get(key);

    if (!state) {
      state = { key, topic, partition, tracker: new PartitionOffsetTracker(), running: 0 };
      this.partitions.set(key, state);
    }

    return state;
  }

  /** Commits the end of the finished range of every partition that moved. Commits never overlap. */
  private commit(): Promise<void> {
    this.commitChain = this.commitChain
      .then(() => this.commitNow())
      .catch(error => this.logger.debug(`Kafka offset commit failed; will retry: ${(error as Error).message}`));

    return this.commitChain;
  }

  private async commitNow(): Promise<void> {
    const consumer = this.consumer;

    if (!consumer)
      return;

    const pending = [...this.partitions.values()].flatMap(state => {
      const offset = state.tracker.pendingCommit();

      return offset === undefined ? [] : [{ state, offset }];
    });

    if (pending.length === 0)
      return;

    await consumer.commitOffsets(pending.map(({ state, offset }) => ({ topic: state.topic, partition: state.partition, offset: offset.toString() })));

    for (const { state, offset } of pending)
      state.tracker.markCommitted(offset);
  }

  private publishStatus(message: JobStatusUpdateMessage): Promise<void> {
    return this.send(this.topics.status, [{ key: message.occurrenceId, value: JSON.stringify(message) }]);
  }

  private publishRunning(occurrenceId: string, jobId: string): Promise<void> {
    const now = new Date().toISOString();

    return this.publishStatus({
      occurrenceId,
      jobId,
      workerId: this.options.workerId,
      instanceId: this.options.instanceId,
      status: JobOccurrenceStatus.Running,
      startTime: null,
      endTime: null,
      durationMs: null,
      result: null,
      exception: null,
      messageTimestamp: now,
    }).catch(error => this.logger.debug(`Job heartbeat for occurrence ${occurrenceId} failed.`, error));
  }

  private publishRetry({ delivery, job, occurrenceId, retryCount, maxRetries, notBefore }: RetryRequest): Promise<void> {
    const original = Object.fromEntries(Object.entries(delivery.headers ?? {}).filter((entry): entry is [string, string] => typeof entry[1] === 'string'));

    return this.send(this.topics.retry, [{
      key: job.id,
      value: delivery.body,
      headers: encodeKafkaHeaders({
        ...original,
        [KafkaHeaders.occurrenceId]: occurrenceId,
        [KafkaHeaders.correlationId]: occurrenceId,
        [KafkaHeaders.retryCount]: retryCount,
        [KafkaHeaders.maxRetries]: maxRetries,
        [KafkaHeaders.notBefore]: Math.trunc(notBefore),
      }),
    }]);
  }

  private publishDeadLetter({ message, occurrenceId, retryCount, maxRetries }: DeadLetterRequest): Promise<void> {
    return this.send(this.topics.dlq, [{
      key: occurrenceId,
      value: JSON.stringify(message),
      headers: encodeKafkaHeaders({
        [KafkaHeaders.occurrenceId]: occurrenceId,
        [KafkaHeaders.correlationId]: occurrenceId,
        [KafkaHeaders.retryCount]: retryCount,
        [KafkaHeaders.maxRetries]: maxRetries,
      }),
    }]);
  }

  /** Produces and resolves once every in-sync replica has the records. */
  private async send(topic: string, messages: Message[]): Promise<void> {
    const producer = this.producer;

    if (!producer)
      throw new Error('Not connected to Kafka.');

    await producer.send({ topic, acks: -1, messages });
  }

  private log({ level, log }: LogEntry): void {
    const { message, error, namespace } = log as LogEntry['log'] & { error?: string; namespace?: string };
    const text = `Kafka${namespace ? ` [${namespace}]` : ''}: ${message}${error ? ` (${error})` : ''}`;

    // kafkajs reports rebalances and reconnects as errors; they are expected and retried.
    if (level === logLevel.ERROR || level === logLevel.WARN) {
      if (this.stopping)
        this.logger.debug(text);
      else
        this.logger.warn(text);
    } else {
      this.logger.debug(text);
    }
  }
}

function partitionKey(topic: string, partition: number): string {
  return `${topic}#${partition}`;
}

function isAlreadyExists(error: unknown): boolean {
  const { type, errors } = error as { type?: string; errors?: Array<{ type?: string }> };

  return type === 'TOPIC_ALREADY_EXISTS' || (Array.isArray(errors) && errors.length > 0 && errors.every(e => e.type === 'TOPIC_ALREADY_EXISTS'));
}
