import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { CancellationRegistry } from '../src/cancellation.ts';
import { decodeKafkaHeaders, encodeKafkaHeaders, kafkaNotBefore, kafkaOccurrenceId, kafkaRetryCount } from '../src/kafka/headers.ts';
import { PartitionOffsetTracker } from '../src/kafka/offset-tracker.ts';
import { kafkaTopicNames, kafkaWorkerSegment } from '../src/kafka/topics.ts';
import { LogBuffer } from '../src/log-buffer.ts';
import { resolveJobOptions, resolveWorkerOptions, workerOptionsFromEnv } from '../src/options.ts';
import { JobProcessor, type DeadLetterRequest, type JobOutput, type RegisteredJob, type RetryRequest } from '../src/processor.ts';
import { JobOccurrenceStatus as S, type JobStatusUpdateMessage } from '../src/protocol.ts';
import { delivery, OccurrenceId, silentLogger } from './helpers.ts';

describe('Kafka topic names', () => {
  it('lower-cases the worker id and replaces characters outside a-z 0-9 . _ -', () => {
    assert.equal(kafkaWorkerSegment('Report-Worker'), 'report-worker');
    assert.equal(kafkaWorkerSegment('Mail Worker/01:EU'), 'mail_worker_01_eu');
    assert.equal(kafkaWorkerSegment('a.b_c-d'), 'a.b_c-d');
    assert.equal(kafkaWorkerSegment('işçi'), 'i__i');
  });

  it('builds every topic and the consumer group with the prefix', () => {
    assert.deepEqual(kafkaTopicNames('Report Worker', 'prod.'), {
      jobs: 'prod.milvaion.jobs.report_worker',
      retry: 'prod.milvaion.jobs.report_worker.retry',
      status: 'prod.milvaion.status',
      logs: 'prod.milvaion.logs',
      heartbeats: 'prod.milvaion.heartbeats',
      registrations: 'prod.milvaion.registrations',
      dlq: 'prod.milvaion.dlq',
      group: 'prod.milvaion.worker.report_worker',
    });
    assert.equal(kafkaTopicNames('w').jobs, 'milvaion.jobs.w');
  });
});

describe('Kafka headers', () => {
  const id = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';

  it('decodes bytes and strings, keeping the last of repeated headers', () => {
    assert.deepEqual(decodeKafkaHeaders({ a: Buffer.from('x'), b: 'y', c: [Buffer.from('1'), Buffer.from('2')], d: undefined }), { a: 'x', b: 'y', c: '2' });
    assert.deepEqual(decodeKafkaHeaders(undefined), {});
  });

  it('encodes numbers as decimal strings', () => {
    assert.deepEqual(encodeKafkaHeaders({ 'x-retry-count': 2, MaxRetries: 0, OccurrenceId: id }), { 'x-retry-count': '2', MaxRetries: '0', OccurrenceId: id });
  });

  it('reads the occurrence id from OccurrenceId, then CorrelationId', () => {
    assert.equal(kafkaOccurrenceId({ OccurrenceId: id, CorrelationId: 'other' }), id);
    assert.equal(kafkaOccurrenceId({ CorrelationId: id }), id);
    assert.equal(kafkaOccurrenceId({ OccurrenceId: ' ', CorrelationId: id }), id);
    assert.equal(kafkaOccurrenceId({}), undefined);
  });

  it('reads the retry count, absent or invalid meaning 0', () => {
    assert.equal(kafkaRetryCount({ 'x-retry-count': '3' }), 3);
    assert.equal(kafkaRetryCount({ 'x-retry-count': 'abc' }), 0);
    assert.equal(kafkaRetryCount({}), 0);
  });

  it('reads x-not-before as Unix milliseconds', () => {
    assert.equal(kafkaNotBefore({ 'x-not-before': '1763100000000' }), 1763100000000);
    assert.equal(kafkaNotBefore({ 'x-not-before': 'soon' }), undefined);
    assert.equal(kafkaNotBefore({}), undefined);
  });
});

describe('PartitionOffsetTracker', () => {
  it('commits only the end of the contiguous finished range', () => {
    const tracker = new PartitionOffsetTracker();

    assert.equal(tracker.committable(), undefined);

    for (const offset of [10n, 11n, 12n, 15n])
      tracker.add(offset);

    assert.equal(tracker.committable(), 10n);
    assert.equal(tracker.pending, 4);

    tracker.complete(11n);
    tracker.complete(15n);
    assert.equal(tracker.committable(), 10n, 'an older record is still running');

    tracker.complete(10n);
    assert.equal(tracker.committable(), 12n);

    tracker.complete(12n);
    assert.equal(tracker.committable(), 16n, 'the next offset after the last finished record');
    assert.equal(tracker.pending, 0);
  });

  it('reports a commit only when the position moved', () => {
    const tracker = new PartitionOffsetTracker();

    tracker.add(0n);
    assert.equal(tracker.pendingCommit(), 0n);
    tracker.markCommitted(0n);
    assert.equal(tracker.pendingCommit(), undefined);

    tracker.complete(0n);
    assert.equal(tracker.pendingCommit(), 1n);
    tracker.markCommitted(1n);
    assert.equal(tracker.committed, 1n);
    assert.equal(tracker.pendingCommit(), undefined);
  });

  it('knows which offsets it has dispatched and rejects going backwards', () => {
    const tracker = new PartitionOffsetTracker();

    tracker.add(5n);
    assert.ok(tracker.isDispatched(5n));
    assert.ok(tracker.isDispatched(3n));
    assert.ok(!tracker.isDispatched(6n));
    assert.throws(() => tracker.add(5n));
    assert.equal(tracker.complete(99n), false);
    assert.equal(tracker.complete(5n), true);
    assert.equal(tracker.complete(5n), false);
  });

  it('stays correct over many records finishing out of order', () => {
    const tracker = new PartitionOffsetTracker();
    const offsets = Array.from({ length: 5000 }, (_, i) => BigInt(i * 2));

    offsets.forEach(offset => tracker.add(offset));

    const shuffled = [...offsets].sort(() => Math.random() - 0.5);
    const lowestOpen = new Set(offsets);

    for (const offset of shuffled) {
      tracker.complete(offset);
      lowestOpen.delete(offset);

      const expected = lowestOpen.size ? [...lowestOpen].reduce((a, b) => (a < b ? a : b)) : offsets.at(-1)! + 1n;

      if (offset % 97n === 0n)
        assert.equal(tracker.committable(), expected);
    }

    assert.equal(tracker.committable(), 10_000n - 1n);
  });
});

describe('Kafka options', () => {
  it('defaults to RabbitMQ', () => {
    const options = resolveWorkerOptions({ workerId: 'w', logger: silentLogger });

    assert.equal(options.transport, 'rabbitmq');
    assert.equal(options.kafka, null);
  });

  it('applies the Kafka defaults', () => {
    const options = resolveWorkerOptions({ workerId: 'w', transport: 'kafka', maxParallelJobs: 6, logger: silentLogger });

    assert.deepEqual(options.kafka, {
      brokers: ['localhost:9092'],
      ssl: undefined,
      sasl: undefined,
      topicPrefix: '',
      autoCreateTopics: true,
      numPartitions: 12,
      replicationFactor: -1,
      maxInFlightPerPartition: 6,
    });
  });

  it('validates the Kafka options', () => {
    assert.throws(() => resolveWorkerOptions({ workerId: 'w', transport: 'kafka', kafka: { topicPrefix: 'bad prefix' } }), /topicPrefix/);
    assert.throws(() => resolveWorkerOptions({ workerId: 'w', transport: 'kafka', kafka: { replicationFactor: 0 } }), /replicationFactor/);
    assert.throws(() => resolveWorkerOptions({ workerId: 'w', transport: 'kafka', kafka: { brokers: [] } }), /brokers/);
    assert.throws(() => resolveWorkerOptions({ workerId: 'w', transport: 'nats' as any }), /transport/);
  });

  it('reads the Kafka environment', () => {
    const options = workerOptionsFromEnv({ kafka: { numPartitions: 3 } }, {
      MILVAION_WORKER_ID: 'w',
      MILVAION_TRANSPORT: 'Kafka',
      MILVAION_KAFKA_BOOTSTRAP_SERVERS: 'k1:9092, k2:9092',
      MILVAION_KAFKA_TOPIC_PREFIX: 'prod.',
      MILVAION_KAFKA_SSL: 'true',
      MILVAION_KAFKA_SASL_MECHANISM: 'SCRAM-SHA-512',
      MILVAION_KAFKA_SASL_USERNAME: 'u',
      MILVAION_KAFKA_SASL_PASSWORD: 'p',
      MILVAION_KAFKA_AUTO_CREATE_TOPICS: 'false',
      MILVAION_KAFKA_NUM_PARTITIONS: '24',
    });

    assert.equal(options.transport, 'kafka');

    const kafka = resolveWorkerOptions({ ...options, logger: silentLogger }).kafka!;

    assert.deepEqual(kafka.brokers, ['k1:9092', 'k2:9092']);
    assert.equal(kafka.topicPrefix, 'prod.');
    assert.equal(kafka.ssl, true);
    assert.deepEqual(kafka.sasl, { mechanism: 'scram-sha-512', username: 'u', password: 'p' });
    assert.equal(kafka.autoCreateTopics, false);
    assert.equal(kafka.numPartitions, 3, 'code wins over the environment');
    assert.deepEqual(workerOptionsFromEnv({}, { MILVAION_WORKER_ID: 'w', KAFKA_BOOTSTRAP_SERVERS: 'b:1' }).kafka?.brokers, ['b:1']);
  });
});

/** A JobOutput that records calls, like the Kafka transport's: retries are delayed by the broker. */
class BrokerDelayOutput implements JobOutput {
  readonly retryDelay = 'broker';
  readonly statuses: JobStatusUpdateMessage[] = [];
  readonly retries: RetryRequest[] = [];
  readonly deadLetters: DeadLetterRequest[] = [];

  async publishStatus(message: JobStatusUpdateMessage): Promise<void> {
    this.statuses.push(message);
  }

  async publishRetry(request: RetryRequest): Promise<void> {
    this.retries.push(request);
  }

  async publishDeadLetter(request: DeadLetterRequest): Promise<void> {
    this.deadLetters.push(request);
  }
}

describe('JobProcessor with broker-delayed retries', () => {
  function setupBroker(handler: () => unknown) {
    const options = resolveWorkerOptions({ workerId: 'node-worker', redis: false, logger: silentLogger, jobHeartbeatIntervalSeconds: 0, instanceId: 'node-worker-abcd1234' });
    const output = new BrokerDelayOutput();
    const sleeps: number[] = [];
    const resolved = resolveJobOptions('TestJob', { maxRetries: 3, baseRetryDelaySeconds: 5 }, options);
    const jobs = new Map<string, RegisteredJob>([['testjob', { name: 'TestJob', handler, options: resolved, queue: 'unused' }]]);
    const processor = new JobProcessor({
      options,
      jobs,
      output,
      logs: new LogBuffer(async () => undefined, silentLogger, { flushIntervalMs: 5 }),
      cancellations: new CancellationRegistry(),
      sleep: async ms => { sleeps.push(ms); },
    });

    return { processor, output, sleeps };
  }

  it('hands a retry to the broker right away with its due time, then settles the original', async () => {
    const { processor, output, sleeps } = setupBroker(() => { throw new Error('boom'); });
    const d = delivery({}, { 'x-retry-count': '1' });
    const before = Date.now();

    await processor.handle(d, { queue: 'milvaion.jobs.node-worker' });

    assert.deepEqual(sleeps, [], 'does not wait in process');
    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(output.statuses.map(s => s.status), [S.Running]);

    const [retry] = output.retries;

    assert.ok(retry);
    assert.equal(retry.occurrenceId, OccurrenceId);
    assert.equal(retry.retryCount, 2);
    assert.equal(retry.maxRetries, 3);
    assert.ok(retry.notBefore >= before + 10_000 && retry.notBefore <= Date.now() + 10_000, 'now + base * 2^retryCount');
  });

  it('dead-letters through the output after the last retry', async () => {
    const { processor, output } = setupBroker(() => { throw new Error('boom'); });
    const d = delivery({}, { 'x-retry-count': '3' });

    await processor.handle(d, { queue: 'milvaion.jobs.node-worker.retry' });

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(output.statuses.map(s => s.status), [S.Running, S.Failed]);
    assert.equal(output.deadLetters[0]?.maxRetries, 3);
    assert.equal(output.deadLetters[0]?.retryCount, 3);
  });

  it('passes a reason when it rejects a record that is not a job', async () => {
    const { processor } = setupBroker(() => 'ok');
    const reasons: Array<string | undefined> = [];
    const d = delivery({});

    d.body = Buffer.from('not json');
    d.nack = (requeue, reason) => { reasons.push(requeue ? 'requeue' : reason); };

    await processor.handle(d, { queue: 'milvaion.jobs.node-worker' });

    assert.deepEqual(reasons, ['The message body is not a valid job.']);
  });
});
