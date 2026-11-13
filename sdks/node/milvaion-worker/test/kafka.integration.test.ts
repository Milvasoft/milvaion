// Runs against a real Kafka when MILVAION_KAFKA_BROKERS is set, e.g.
//   MILVAION_KAFKA_BROKERS=localhost:19092 npm test
// Every test uses its own topic prefix, so runs never see each other's records.
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { after, describe, it } from 'node:test';
import { Kafka, logLevel, Partitioners, type Consumer, type KafkaMessage, type Producer } from 'kafkajs';
import { kafkaTopicNames, type KafkaTopicNames } from '../src/kafka/topics.ts';
import { PermanentJobError } from '../src/errors.ts';
import type { JobHandler } from '../src/processor.ts';
import { JobOccurrenceStatus as S } from '../src/protocol.ts';
import { MilvaionWorker } from '../src/worker.ts';
import { silentLogger } from './helpers.ts';

const brokersEnv = process.env.MILVAION_KAFKA_BROKERS;
const brokers = brokersEnv?.split(',').map(b => b.trim()).filter(Boolean) ?? [];
const kafka = new Kafka({ clientId: 'milvaion-node-tests', brokers: brokers.length ? brokers : ['localhost:9092'], logLevel: logLevel.NOTHING });

interface Received {
  topic: string;
  key: string | null;
  headers: Record<string, string>;
  body: any;
  raw: string;
}

/** Reads every record of the given topics from the beginning. */
class Collector {
  readonly records: Received[] = [];
  private readonly consumer: Consumer;

  private constructor(consumer: Consumer) {
    this.consumer = consumer;
  }

  static async start(topics: string[]): Promise<Collector> {
    const consumer = kafka.consumer({ groupId: `test-collector-${randomUUID()}`, maxWaitTimeInMs: 100 });
    const collector = new Collector(consumer);

    await consumer.connect();
    await consumer.subscribe({ topics, fromBeginning: true });
    await consumer.run({ eachMessage: async ({ topic, message }) => collector.add(topic, message) });

    return collector;
  }

  of(topic: string): Received[] {
    return this.records.filter(r => r.topic === topic);
  }

  stop(): Promise<void> {
    return this.consumer.disconnect();
  }

  private add(topic: string, message: KafkaMessage): void {
    const raw = message.value?.toString('utf8') ?? '';
    const headers = Object.fromEntries(Object.entries(message.headers ?? {}).map(([k, v]) => [k, String(Array.isArray(v) ? v.at(-1) : v)]));
    let body: unknown = null;

    try {
      body = JSON.parse(raw);
    } catch {
      // Poison records stay raw.
    }

    this.records.push({ topic, key: message.key?.toString('utf8') ?? null, headers, body, raw });
  }
}

interface Harness {
  worker: MilvaionWorker;
  topics: KafkaTopicNames;
  collector: Collector;
  producer: Producer;
  /** Produces a job record the way the API does. */
  dispatch(job: Record<string, unknown>, occurrenceId?: string): Promise<{ jobId: string; occurrenceId: string }>;
  dispose(): Promise<void>;
}

const harnesses: Harness[] = [];

async function startWorker(jobs: Record<string, JobHandler>, jobOptions: { maxRetries?: number; baseRetryDelaySeconds?: number } = {},
  workerOptions: { shutdownTimeoutSeconds?: number; maxParallelJobs?: number; maxInFlightPerPartition?: number } = {}): Promise<Harness> {
  const prefix = `t${randomBytes(4).toString('hex')}.`;
  const workerId = 'Node-Kafka Worker';
  const topics = kafkaTopicNames(workerId, prefix);
  const worker = new MilvaionWorker({
    workerId,
    transport: 'kafka',
    kafka: { brokers, topicPrefix: prefix, numPartitions: 3, maxInFlightPerPartition: workerOptions.maxInFlightPerPartition },
    redis: false,
    maxParallelJobs: workerOptions.maxParallelJobs ?? 4,
    jobHeartbeatIntervalSeconds: 1,
    heartbeatIntervalSeconds: 5,
    startupTimeoutSeconds: 60,
    shutdownTimeoutSeconds: workerOptions.shutdownTimeoutSeconds ?? 5,
    logger: process.env.MILVAION_TEST_LOGS ? console : silentLogger,
  });

  for (const [name, handler] of Object.entries(jobs))
    worker.addJob(name, handler, jobOptions);

  await worker.start();

  const collector = await Collector.start([topics.jobs, topics.retry, topics.status, topics.dlq, topics.registrations, topics.heartbeats, topics.logs]);
  const producer = kafka.producer({ createPartitioner: Partitioners.DefaultPartitioner });

  await producer.connect();

  let disposed = false;
  const harness: Harness = {
    worker,
    topics,
    collector,
    producer,
    async dispatch(job, occurrenceId = randomUUID()) {
      const jobId = randomUUID();

      await producer.send({
        topic: topics.jobs,
        messages: [{
          key: jobId,
          value: JSON.stringify({ id: jobId, displayName: 'Test', jobNameInWorker: 'TestJob', jobData: null, workerId, ...job }),
          headers: { OccurrenceId: occurrenceId, CorrelationId: occurrenceId, JobType: String(job.jobNameInWorker ?? 'TestJob'), WorkerId: workerId },
        }],
      });

      return { jobId, occurrenceId };
    },
    async dispose() {
      if (disposed)
        return;

      disposed = true;
      await worker.stop();
      await collector.stop();
      await producer.disconnect();
    },
  };

  harnesses.push(harness);

  return harness;
}

async function waitFor(condition: () => boolean, timeoutMs = 20_000, what = 'condition'): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  while (!condition()) {
    if (Date.now() > deadline)
      throw new Error(`Timed out waiting for ${what}`);

    await new Promise(resolve => setTimeout(resolve, 50));
  }
}

/** Retries an async assertion until it passes. */
async function eventually(assertion: () => Promise<void>, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  for (;;) {
    try {
      return await assertion();
    } catch (error) {
      if (Date.now() > deadline)
        throw error;

      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }
}

function statuses(h: Harness, occurrenceId: string): number[] {
  return h.collector.of(h.topics.status).filter(r => r.key === occurrenceId).map(r => r.body.status);
}

/** Committed offset of the worker's group on its job topic, summed over partitions, and the topic's end offsets. */
async function committedVsEnd(h: Harness, topic: string): Promise<{ committed: number; end: number }> {
  const admin = kafka.admin();

  await admin.connect();

  try {
    const [group] = await admin.fetchOffsets({ groupId: h.topics.group, topics: [topic] });
    const ends = await admin.fetchTopicOffsets(topic);
    const committed = (group?.partitions ?? []).reduce((sum, p) => sum + Math.max(0, Number(p.offset)), 0);
    const end = ends.reduce((sum, p) => sum + Number(p.high), 0);

    return { committed, end };
  } finally {
    await admin.disconnect();
  }
}

describe('Kafka transport (integration)', { skip: brokers.length === 0 ? 'set MILVAION_KAFKA_BROKERS to run' : false, timeout: 180_000 }, () => {
  after(async () => {
    await Promise.allSettled(harnesses.map(h => h.dispose()));
  });

  it('creates its topics, registers, runs a job and reports Running then Completed', async () => {
    const h = await startWorker({ TestJob: ctx => { ctx.log('hello from kafka'); return { ok: true }; } });
    const { jobId, occurrenceId } = await h.dispatch({});

    await waitFor(() => statuses(h, occurrenceId).includes(S.Completed), 30_000, 'Completed status');

    assert.deepEqual(statuses(h, occurrenceId), [S.Running, S.Completed]);

    const final = h.collector.of(h.topics.status).find(r => r.key === occurrenceId && r.body.status === S.Completed)!;

    assert.equal(final.body.jobId, jobId);
    assert.equal(final.body.workerId, 'Node-Kafka Worker');
    assert.equal(final.body.result, '{"ok":true}');

    const registration = h.collector.of(h.topics.registrations)[0];

    assert.ok(registration, 'registration record');
    assert.equal(registration.key, h.worker.instanceId);
    assert.deepEqual(registration.body.jobTypes, ['TestJob']);

    await waitFor(() => h.collector.of(h.topics.logs).some(r => r.raw.includes('hello from kafka')), 10_000, 'log record');
    assert.equal(h.collector.of(h.topics.logs).find(r => r.raw.includes('hello from kafka'))!.key, occurrenceId);
    assert.ok(h.collector.of(h.topics.heartbeats).some(r => r.key === h.worker.instanceId), 'instance heartbeat');

    // The record is committed once its outcome is reported (commits run every 500 ms).
    await eventually(async () => assert.deepEqual(await committedVsEnd(h, h.topics.jobs), { committed: 1, end: 1 }));

    await h.dispose();
  });

  it('retries through the retry topic, then dead-letters with MaxRetries and a Failed status', async () => {
    const attempts: number[] = [];
    const attemptTimes: number[] = [];
    const h = await startWorker({ TestJob: ctx => { attempts.push(ctx.retryCount); attemptTimes.push(Date.now()); throw new Error('always broken'); } },
      { maxRetries: 2, baseRetryDelaySeconds: 1 });
    const started = Date.now();
    const { jobId, occurrenceId } = await h.dispatch({});

    await waitFor(() => h.collector.of(h.topics.dlq).some(r => r.key === occurrenceId), 60_000, 'dead letter record');
    await waitFor(() => statuses(h, occurrenceId).includes(S.Failed), 10_000, 'Failed status');

    assert.deepEqual(attempts, [0, 1, 2]);

    const retries = h.collector.of(h.topics.retry).filter(r => r.key === jobId);

    assert.deepEqual(retries.map(r => r.headers['x-retry-count']), ['1', '2']);
    assert.ok(retries.every(r => r.headers.OccurrenceId === occurrenceId && r.headers.CorrelationId === occurrenceId && r.headers.MaxRetries === '2'));
    assert.ok(retries.every(r => /^\d+$/.test(r.headers['x-not-before']!)), 'x-not-before in Unix ms');
    assert.ok(Number(retries[0]!.headers['x-not-before']) >= started + 1000, 'first retry waits base * 2^0');
    assert.equal(retries[0]!.headers.JobType, 'TestJob', 'original headers are kept');
    assert.ok(attemptTimes[1]! >= Number(retries[0]!.headers['x-not-before']), 'retry 1 runs no earlier than its due time');
    assert.ok(attemptTimes[2]! >= Number(retries[1]!.headers['x-not-before']), 'retry 2 runs no earlier than its due time');
    assert.ok(Number(retries[1]!.headers['x-not-before']) - attemptTimes[1]! >= 1900, 'retry 2 waits base * 2^1');

    const [dlq] = h.collector.of(h.topics.dlq).filter(r => r.key === occurrenceId);

    assert.ok(dlq);
    assert.equal(dlq.headers.MaxRetries, '2');
    assert.equal(dlq.headers['x-retry-count'], '2');
    assert.equal(dlq.headers.OccurrenceId, occurrenceId);
    assert.equal(dlq.headers.CorrelationId, occurrenceId);
    assert.equal(dlq.body.id, jobId);
    assert.equal(dlq.body.status, S.Failed);
    assert.match(dlq.body.exception, /^Max retries \(2\) exceeded/);

    const final = statuses(h, occurrenceId);

    assert.equal(final.at(-1), S.Failed);
    assert.ok(final.slice(0, -1).every(s => s === S.Running), 'stays Running while retrying');

    // Every record of both topics is committed once the outcome is reported.
    await eventually(async () => {
      assert.deepEqual(await committedVsEnd(h, h.topics.jobs), { committed: 1, end: 1 });
      assert.deepEqual(await committedVsEnd(h, h.topics.retry), { committed: 2, end: 2 });
    });

    await h.dispose();
  });

  it('dead-letters a permanent failure with MaxRetries 0', async () => {
    const h = await startWorker({ TestJob: () => { throw new PermanentJobError('bad input'); } });
    const { occurrenceId } = await h.dispatch({});

    await waitFor(() => h.collector.of(h.topics.dlq).some(r => r.key === occurrenceId), 30_000, 'dead letter record');

    const [dlq] = h.collector.of(h.topics.dlq).filter(r => r.key === occurrenceId);

    assert.equal(dlq!.headers.MaxRetries, '0');
    assert.equal(h.collector.of(h.topics.retry).length, 0);

    await h.dispose();
  });

  it('reports TimedOut for a job that runs past its timeout', async () => {
    const h = await startWorker({ TestJob: () => new Promise(() => {}) });
    const { occurrenceId } = await h.dispatch({ executionTimeoutSeconds: 1 });

    await waitFor(() => statuses(h, occurrenceId).includes(S.TimedOut), 30_000, 'TimedOut status');

    assert.equal(statuses(h, occurrenceId).at(-1), S.TimedOut);

    await h.dispose();
  });

  it('forwards a record that is not a job to the dead letter topic unchanged', async () => {
    const h = await startWorker({ TestJob: () => 'ok' });

    await h.producer.send({ topic: h.topics.jobs, messages: [{ key: 'poison', value: 'not json', headers: { JobType: 'TestJob' } }] });
    await waitFor(() => h.collector.of(h.topics.dlq).length > 0, 30_000, 'poison record in the DLQ');

    const [dlq] = h.collector.of(h.topics.dlq);

    assert.equal(dlq!.raw, 'not json');
    assert.equal(dlq!.headers['x-original-topic'], h.topics.jobs);
    assert.equal(dlq!.headers.JobType, 'TestJob');
    assert.ok(dlq!.headers['x-dlq-reason']);

    await h.dispose();
  });

  it('runs jobs of one partition in parallel and commits in order', async () => {
    const releases: Array<() => void> = [];
    const h = await startWorker({ TestJob: () => new Promise<void>(resolve => { releases.push(resolve); }) });
    const occurrences: string[] = [];

    // Same key, so the same partition.
    for (let i = 0; i < 3; i++) {
      const occurrenceId = randomUUID();

      occurrences.push(occurrenceId);
      await h.producer.send({
        topic: h.topics.jobs,
        messages: [{
          key: 'same-job',
          value: JSON.stringify({ id: 'same-job', jobNameInWorker: 'TestJob', jobData: null }),
          headers: { OccurrenceId: occurrenceId, CorrelationId: occurrenceId },
        }],
      });
    }

    await waitFor(() => releases.length === 3, 30_000, 'three jobs running at once');

    // Finish the second and third: the first still holds the commit position.
    releases[1]!();
    releases[2]!();
    await waitFor(() => statuses(h, occurrences[2]!).includes(S.Completed) && statuses(h, occurrences[1]!).includes(S.Completed), 10_000, 'two completions');
    await new Promise(resolve => setTimeout(resolve, 1500));
    assert.equal((await committedVsEnd(h, h.topics.jobs)).committed, 0, 'nothing committed while the oldest record runs');

    releases[0]!();
    await waitFor(() => statuses(h, occurrences[0]!).includes(S.Completed), 10_000, 'first completion');
    await eventually(async () => assert.equal((await committedVsEnd(h, h.topics.jobs)).committed, 3));

    await h.dispose();
  });

  it('caps the jobs of one partition and resumes it when a slot frees up', async () => {
    let running = 0;
    let peak = 0;
    const h = await startWorker({
      TestJob: async () => {
        peak = Math.max(peak, ++running);
        await new Promise(resolve => setTimeout(resolve, 300));
        running--;
      },
    }, {}, { maxParallelJobs: 4, maxInFlightPerPartition: 2 });
    const occurrences = Array.from({ length: 6 }, () => randomUUID());

    await h.producer.send({
      topic: h.topics.jobs,
      messages: occurrences.map(occurrenceId => ({
        key: 'one-partition',
        value: JSON.stringify({ id: 'one-partition', jobNameInWorker: 'TestJob', jobData: null }),
        headers: { OccurrenceId: occurrenceId, CorrelationId: occurrenceId },
      })),
    });

    await waitFor(() => occurrences.every(id => statuses(h, id).includes(S.Completed)), 30_000, 'all six completions');

    assert.equal(peak, 2, 'never more than maxInFlightPerPartition at once');
    await eventually(async () => assert.equal((await committedVsEnd(h, h.topics.jobs)).committed, 6));

    await h.dispose();
  });

  it('writes a job interrupted by shutdown back to its topic and commits the original', async () => {
    let started = false;
    const h = await startWorker({ TestJob: () => { started = true; return new Promise(() => {}); } }, {}, { shutdownTimeoutSeconds: 1 });
    const { occurrenceId } = await h.dispatch({});

    await waitFor(() => started, 30_000, 'job start');
    await h.worker.stop();
    await waitFor(() => h.collector.of(h.topics.jobs).filter(r => r.headers.OccurrenceId === occurrenceId).length === 2, 10_000, 'requeued record');

    assert.ok(!statuses(h, occurrenceId).some(s => s !== S.Running), 'no final status');
    assert.deepEqual(await committedVsEnd(h, h.topics.jobs), { committed: 1, end: 2 });

    await h.dispose();
  });
});
