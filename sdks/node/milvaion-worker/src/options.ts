import os from 'node:os';
import type { ConnectionOptions } from 'node:tls';
import { defaultConsumerId, generateInstanceId, generateRoutingPattern } from './routing.ts';

export interface Logger {
  debug(message: string, ...args: unknown[]): void;
  info(message: string, ...args: unknown[]): void;
  warn(message: string, ...args: unknown[]): void;
  error(message: string, ...args: unknown[]): void;
}

export interface RabbitMqOptions {
  /** Connection URL, e.g. `amqp://guest:guest@localhost:5672/`. Takes precedence over the separate fields. */
  url?: string;
  host?: string;
  port?: number;
  username?: string;
  password?: string;
  vhost?: string;
  /** Must match the API's `MilvaionConfig:RabbitMQ:QueueType`. Default: `classic`. */
  queueType?: 'classic' | 'quorum';
  /** AMQP heartbeat in seconds. Default: 60. */
  heartbeatSeconds?: number;
}

export interface RedisOptions {
  /** Connection URL, e.g. `redis://:password@localhost:6379/0`. Takes precedence over the separate fields. */
  url?: string;
  host?: string;
  port?: number;
  password?: string;
  db?: number;
  /** Must match the API's `MilvaionConfig:Redis:KeyPrefix`. Default: `Milvaion:JobScheduler:`. */
  keyPrefix?: string;
  /** Overrides the cancellation channel name. Default: `{keyPrefix}cancellation_channel`. */
  cancellationChannel?: string;
}

export type TransportType = 'rabbitmq' | 'kafka';

export interface KafkaSaslOptions {
  mechanism: 'plain' | 'scram-sha-256' | 'scram-sha-512';
  username: string;
  password: string;
}

export interface KafkaOptions {
  /** Bootstrap brokers as `host:port`. Default: `['localhost:9092']`. */
  brokers?: string[];
  /** `true` for TLS with the system CAs, or Node TLS options (CA, client certificate). Default: off. */
  ssl?: boolean | ConnectionOptions;
  sasl?: KafkaSaslOptions;
  /** Prepended to every topic and consumer group name, e.g. `prod.`. Must match the API. Default: none. */
  topicPrefix?: string;
  /** Create this worker's topics and the shared topics it writes to at startup. Default: true. */
  autoCreateTopics?: boolean;
  /** Partitions of auto-created topics. Default: 12. */
  numPartitions?: number;
  /** Replication of auto-created topics. -1 uses the broker default. Default: -1. */
  replicationFactor?: number;
  /** Jobs of one partition that may run at once on this instance. Default: the worker's `maxParallelJobs`. */
  maxInFlightPerPartition?: number;
}

export interface ResolvedKafkaOptions {
  brokers: string[];
  ssl?: boolean | ConnectionOptions;
  sasl?: KafkaSaslOptions;
  topicPrefix: string;
  autoCreateTopics: boolean;
  numPartitions: number;
  replicationFactor: number;
  maxInFlightPerPartition: number;
}

export interface WorkerOptions {
  /** Worker application id, the same on every replica, e.g. `report-worker`. */
  workerId: string;
  /** Message broker. Must be the same as the API's. Default: `rabbitmq`. */
  transport?: TransportType;
  rabbitmq?: RabbitMqOptions;
  /** Used when `transport` is `kafka`. */
  kafka?: KafkaOptions;
  /** Redis is only used to receive cancellation requests. Pass `false` to run without it. */
  redis?: RedisOptions | false;
  /** Capacity reported for this instance. Default: available parallelism × 2. */
  maxParallelJobs?: number;
  /** Default timeout for jobs that do not set one. 0 disables it. Default: 3600. */
  executionTimeoutSeconds?: number;
  /** Instance heartbeat interval. Default: 30. */
  heartbeatIntervalSeconds?: number;
  /** Interval at which a running job reports that it is alive. Default: 60. */
  jobHeartbeatIntervalSeconds?: number;
  /** How long `stop()` lets running jobs finish before returning them to the queue. Default: 30. */
  shutdownTimeoutSeconds?: number;
  /** How long `start()` keeps trying to reach the broker before failing. Default: 30. */
  startupTimeoutSeconds?: number;
  /** Version reported to the dashboard. Default: `npm_package_version` or `1.0.0`. */
  version?: string;
  /** Overrides the generated instance id. Must be unique per running process. */
  instanceId?: string;
  logger?: Logger;
}

export interface JobOptions {
  /** RabbitMQ routing pattern. Default: `{workerId}.{job name without "Job"}.*`. */
  routingPattern?: string;
  /** Default: `{job name without "Job"}-consumer`. */
  consumerId?: string;
  /** Jobs of this type that may run at once on this instance (prefetch). Default: min(10, worker maxParallelJobs). */
  maxParallelJobs?: number;
  /** Overridden by the job's own timeout when it has one. 0 disables it. Default: the worker default. */
  executionTimeoutSeconds?: number;
  /** Retries after the first failure before the job goes to the dead letter queue. Default: 3. */
  maxRetries?: number;
  /** Retry n waits `baseRetryDelaySeconds * 2^(n-1)` seconds. Default: 5. */
  baseRetryDelaySeconds?: number;
  /** JSON Schema of the job data. The dashboard builds the job data form from it. */
  dataSchema?: object;
  /** JSON Schema of the job result. */
  resultSchema?: object;
}

export interface ResolvedJobOptions {
  routingPattern: string;
  consumerId: string;
  maxParallelJobs: number;
  executionTimeoutSeconds: number;
  maxRetries: number;
  baseRetryDelaySeconds: number;
  dataSchema?: object;
  resultSchema?: object;
}

export interface ResolvedWorkerOptions {
  workerId: string;
  instanceId: string;
  transport: TransportType;
  /** Set when `transport` is `kafka`. */
  kafka: ResolvedKafkaOptions | null;
  rabbitmqUrl: string;
  queueType: 'classic' | 'quorum';
  amqpHeartbeatSeconds: number;
  redis: false | { url?: string; host: string; port: number; password?: string; db: number; cancellationChannel: string };
  maxParallelJobs: number;
  executionTimeoutSeconds: number;
  heartbeatIntervalSeconds: number;
  jobHeartbeatIntervalSeconds: number;
  shutdownTimeoutSeconds: number;
  startupTimeoutSeconds: number;
  version: string;
  logger: Logger;
}

const DefaultKeyPrefix = 'Milvaion:JobScheduler:';

export function resolveWorkerOptions(options: WorkerOptions): ResolvedWorkerOptions {
  if (!options?.workerId?.trim())
    throw new Error('workerId is required.');

  const workerId = options.workerId.trim();
  const rabbit = options.rabbitmq ?? {};
  const maxParallelJobs = options.maxParallelJobs ?? os.availableParallelism() * 2;

  if (!Number.isInteger(maxParallelJobs) || maxParallelJobs <= 0)
    throw new Error(`maxParallelJobs must be a positive integer but was ${maxParallelJobs}.`);

  const transport = options.transport ?? 'rabbitmq';

  if (transport !== 'rabbitmq' && transport !== 'kafka')
    throw new Error(`transport must be 'rabbitmq' or 'kafka' but was '${transport}'.`);

  return {
    workerId,
    instanceId: options.instanceId ?? generateInstanceId(workerId),
    transport,
    kafka: transport === 'kafka' ? resolveKafka(options.kafka ?? {}, maxParallelJobs) : null,
    rabbitmqUrl: rabbit.url ?? buildAmqpUrl(rabbit),
    queueType: rabbit.queueType ?? 'classic',
    amqpHeartbeatSeconds: rabbit.heartbeatSeconds ?? 60,
    redis: resolveRedis(options.redis),
    maxParallelJobs,
    executionTimeoutSeconds: options.executionTimeoutSeconds ?? 3600,
    heartbeatIntervalSeconds: positive(options.heartbeatIntervalSeconds, 30),
    jobHeartbeatIntervalSeconds: options.jobHeartbeatIntervalSeconds ?? 60,
    shutdownTimeoutSeconds: options.shutdownTimeoutSeconds ?? 30,
    startupTimeoutSeconds: options.startupTimeoutSeconds ?? 30,
    version: options.version ?? process.env.npm_package_version ?? '1.0.0',
    logger: options.logger ?? createConsoleLogger(),
  };
}

export function resolveJobOptions(jobName: string, options: JobOptions | undefined, worker: ResolvedWorkerOptions): ResolvedJobOptions {
  const maxParallelJobs = options?.maxParallelJobs ?? Math.min(10, worker.maxParallelJobs);

  if (!Number.isInteger(maxParallelJobs) || maxParallelJobs <= 0 || maxParallelJobs > 65535)
    throw new Error(`maxParallelJobs of job '${jobName}' must be an integer between 1 and 65535 but was ${maxParallelJobs}.`);

  return {
    routingPattern: options?.routingPattern?.trim() || generateRoutingPattern(jobName, worker.workerId),
    consumerId: options?.consumerId ?? defaultConsumerId(jobName),
    maxParallelJobs,
    executionTimeoutSeconds: options?.executionTimeoutSeconds ?? worker.executionTimeoutSeconds,
    maxRetries: Math.max(0, options?.maxRetries ?? 3),
    baseRetryDelaySeconds: Math.max(0, options?.baseRetryDelaySeconds ?? 5),
    dataSchema: options?.dataSchema,
    resultSchema: options?.resultSchema,
  };
}

/**
 * Reads worker options from environment variables, for container deployments:
 * MILVAION_WORKER_ID, MILVAION_TRANSPORT (`rabbitmq` or `kafka`), MILVAION_RABBITMQ_URL,
 * MILVAION_RABBITMQ_QUEUE_TYPE, MILVAION_REDIS_URL (set to `false` to disable), MILVAION_REDIS_KEY_PREFIX,
 * MILVAION_MAX_PARALLEL_JOBS, MILVAION_EXECUTION_TIMEOUT_SECONDS, MILVAION_HEARTBEAT_INTERVAL_SECONDS,
 * and for Kafka MILVAION_KAFKA_BOOTSTRAP_SERVERS (or KAFKA_BOOTSTRAP_SERVERS), MILVAION_KAFKA_TOPIC_PREFIX,
 * MILVAION_KAFKA_SSL, MILVAION_KAFKA_SASL_MECHANISM, MILVAION_KAFKA_SASL_USERNAME, MILVAION_KAFKA_SASL_PASSWORD,
 * MILVAION_KAFKA_AUTO_CREATE_TOPICS, MILVAION_KAFKA_NUM_PARTITIONS, MILVAION_KAFKA_REPLICATION_FACTOR and
 * MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION.
 * Values passed in `overrides` win over the environment.
 */
export function workerOptionsFromEnv(overrides: Partial<WorkerOptions> = {}, env: NodeJS.ProcessEnv = process.env): WorkerOptions {
  const redisUrl = env.MILVAION_REDIS_URL;
  const queueType = env.MILVAION_RABBITMQ_QUEUE_TYPE?.toLowerCase();

  if (queueType && queueType !== 'classic' && queueType !== 'quorum')
    throw new Error(`MILVAION_RABBITMQ_QUEUE_TYPE must be 'classic' or 'quorum' but was '${queueType}'.`);

  const transport = env.MILVAION_TRANSPORT?.trim().toLowerCase() || undefined;

  if (transport && transport !== 'rabbitmq' && transport !== 'kafka')
    throw new Error(`MILVAION_TRANSPORT must be 'rabbitmq' or 'kafka' but was '${transport}'.`);

  const fromEnv: WorkerOptions = {
    workerId: env.MILVAION_WORKER_ID ?? '',
    transport: transport as TransportType | undefined,
    kafka: kafkaFromEnv(env),
    rabbitmq: {
      url: env.MILVAION_RABBITMQ_URL,
      queueType: queueType as RabbitMqOptions['queueType'],
    },
    redis: redisUrl === 'false' ? false : { url: redisUrl, keyPrefix: env.MILVAION_REDIS_KEY_PREFIX },
    maxParallelJobs: int(env.MILVAION_MAX_PARALLEL_JOBS),
    executionTimeoutSeconds: int(env.MILVAION_EXECUTION_TIMEOUT_SECONDS),
    heartbeatIntervalSeconds: int(env.MILVAION_HEARTBEAT_INTERVAL_SECONDS),
  };

  return {
    ...fromEnv,
    ...overrides,
    workerId: overrides.workerId ?? fromEnv.workerId,
    transport: overrides.transport ?? fromEnv.transport,
    rabbitmq: { ...fromEnv.rabbitmq, ...overrides.rabbitmq },
    kafka: { ...fromEnv.kafka, ...overrides.kafka },
    redis: mergeRedis(fromEnv.redis, overrides.redis),
  };
}

export type LogThreshold = 'debug' | 'info' | 'warn' | 'error';

/** Console logger used when none is given. The threshold defaults to MILVAION_LOG_LEVEL or `info`. */
export function createConsoleLogger(threshold: LogThreshold = (process.env.MILVAION_LOG_LEVEL as LogThreshold) ?? 'info'): Logger {
  const order: LogThreshold[] = ['debug', 'info', 'warn', 'error'];
  const min = Math.max(0, order.indexOf(threshold));
  const enabled = (level: LogThreshold) => order.indexOf(level) >= min;
  const prefix = '[milvaion]';

  return {
    debug: (message, ...args) => enabled('debug') && console.debug(prefix, message, ...args),
    info: (message, ...args) => enabled('info') && console.info(prefix, message, ...args),
    warn: (message, ...args) => enabled('warn') && console.warn(prefix, message, ...args),
    error: (message, ...args) => enabled('error') && console.error(prefix, message, ...args),
  };
}

function mergeRedis(fromEnv: RedisOptions | false | undefined, override: RedisOptions | false | undefined): RedisOptions | false | undefined {
  if (override === undefined)
    return fromEnv;

  if (override === false)
    return false;

  return { ...(fromEnv || {}), ...override };
}

function kafkaFromEnv(env: NodeJS.ProcessEnv): KafkaOptions {
  const brokers = env.MILVAION_KAFKA_BOOTSTRAP_SERVERS ?? env.KAFKA_BOOTSTRAP_SERVERS;
  const mechanism = env.MILVAION_KAFKA_SASL_MECHANISM?.trim().toLowerCase();
  const kafka: KafkaOptions = {
    brokers: brokers ? splitBrokers(brokers) : undefined,
    topicPrefix: env.MILVAION_KAFKA_TOPIC_PREFIX,
    ssl: bool(env.MILVAION_KAFKA_SSL),
    autoCreateTopics: bool(env.MILVAION_KAFKA_AUTO_CREATE_TOPICS),
    numPartitions: int(env.MILVAION_KAFKA_NUM_PARTITIONS),
    replicationFactor: int(env.MILVAION_KAFKA_REPLICATION_FACTOR),
    maxInFlightPerPartition: int(env.MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION),
  };

  if (mechanism) {
    if (mechanism !== 'plain' && mechanism !== 'scram-sha-256' && mechanism !== 'scram-sha-512')
      throw new Error(`MILVAION_KAFKA_SASL_MECHANISM must be 'plain', 'scram-sha-256' or 'scram-sha-512' but was '${mechanism}'.`);

    kafka.sasl = { mechanism, username: env.MILVAION_KAFKA_SASL_USERNAME ?? '', password: env.MILVAION_KAFKA_SASL_PASSWORD ?? '' };
  }

  // Unset values are left out so they do not override options given in code with undefined.
  return Object.fromEntries(Object.entries(kafka).filter(([, value]) => value !== undefined)) as KafkaOptions;
}

const TopicPrefixPattern = /^[A-Za-z0-9._-]*$/;

function resolveKafka(kafka: KafkaOptions, maxParallelJobs: number): ResolvedKafkaOptions {
  const brokers = (kafka.brokers ?? ['localhost:9092']).flatMap(splitBrokers);
  const topicPrefix = kafka.topicPrefix ?? '';
  const numPartitions = kafka.numPartitions ?? 12;
  const replicationFactor = kafka.replicationFactor ?? -1;
  const maxInFlightPerPartition = kafka.maxInFlightPerPartition ?? maxParallelJobs;

  if (brokers.length === 0)
    throw new Error('kafka.brokers must name at least one broker.');

  if (!TopicPrefixPattern.test(topicPrefix))
    throw new Error(`kafka.topicPrefix may only contain a-z A-Z 0-9 . _ - but was '${topicPrefix}'.`);

  if (!Number.isInteger(numPartitions) || numPartitions <= 0)
    throw new Error(`kafka.numPartitions must be a positive integer but was ${numPartitions}.`);

  if (!Number.isInteger(replicationFactor) || (replicationFactor <= 0 && replicationFactor !== -1))
    throw new Error(`kafka.replicationFactor must be a positive integer or -1 but was ${replicationFactor}.`);

  if (!Number.isInteger(maxInFlightPerPartition) || maxInFlightPerPartition <= 0)
    throw new Error(`kafka.maxInFlightPerPartition must be a positive integer but was ${maxInFlightPerPartition}.`);

  return {
    brokers,
    ssl: kafka.ssl || undefined,
    sasl: kafka.sasl,
    topicPrefix,
    autoCreateTopics: kafka.autoCreateTopics ?? true,
    numPartitions,
    replicationFactor,
    maxInFlightPerPartition,
  };
}

function splitBrokers(value: string): string[] {
  return value.split(',').map(broker => broker.trim()).filter(Boolean);
}

function bool(value: string | undefined): boolean | undefined {
  if (value === undefined || value === '')
    return undefined;

  const normalized = value.trim().toLowerCase();

  if (normalized === 'true' || normalized === '1')
    return true;

  if (normalized === 'false' || normalized === '0')
    return false;

  throw new Error(`Expected true or false but got '${value}'.`);
}

function resolveRedis(redis: RedisOptions | false | undefined): ResolvedWorkerOptions['redis'] {
  if (redis === false)
    return false;

  const keyPrefix = redis?.keyPrefix ?? DefaultKeyPrefix;

  return {
    url: redis?.url,
    host: redis?.host ?? 'localhost',
    port: redis?.port ?? 6379,
    password: redis?.password || undefined,
    db: redis?.db ?? 0,
    cancellationChannel: redis?.cancellationChannel ?? `${keyPrefix}cancellation_channel`,
  };
}

function buildAmqpUrl(rabbit: RabbitMqOptions): string {
  const user = encodeURIComponent(rabbit.username ?? 'guest');
  const password = encodeURIComponent(rabbit.password ?? 'guest');
  const vhost = encodeURIComponent(rabbit.vhost ?? '/');

  return `amqp://${user}:${password}@${rabbit.host ?? 'localhost'}:${rabbit.port ?? 5672}/${vhost}`;
}

function positive(value: number | undefined, fallback: number): number {
  return value !== undefined && value > 0 ? value : fallback;
}

function int(value: string | undefined): number | undefined {
  if (value === undefined || value === '')
    return undefined;

  const parsed = Number(value);

  if (!Number.isInteger(parsed))
    throw new Error(`Expected an integer but got '${value}'.`);

  return parsed;
}
