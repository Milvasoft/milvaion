import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { createJobContext } from '../src/context.ts';
import { formatError } from '../src/errors.ts';
import { LogBuffer } from '../src/log-buffer.ts';
import { resolveJobOptions, resolveWorkerOptions, workerOptionsFromEnv } from '../src/options.ts';
import { parseOccurrenceId, parseRetryCount } from '../src/processor.ts';
import type { OccurrenceLog, WorkerLogBatchMessage } from '../src/protocol.ts';
import { generateInstanceId, generateRoutingPattern, queueNameForPattern } from '../src/routing.ts';
import { silentLogger } from './helpers.ts';

describe('routing', () => {
  it('generates the same patterns and queue names as the .NET SDK', () => {
    assert.equal(generateRoutingPattern('SendEmailJob', 'Email-Worker-01'), 'email-worker-01.sendemail.*');
    assert.equal(generateRoutingPattern('NonParallel', 'w'), 'w.nonparallel.*');
    assert.equal(queueNameForPattern('email-worker-01.sendemail.*'), 'scheduled_jobs_queue.email-worker-01.sendemail.wildcard');
    assert.equal(queueNameForPattern('a.#'), 'scheduled_jobs_queue.a.all');
  });

  it('generates instance ids as {workerId}-{8 hex}', () => {
    assert.match(generateInstanceId('node-worker'), /^node-worker-[0-9a-f]{8}$/);
  });
});

describe('headers', () => {
  const id = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';

  it('reads the occurrence id from bytes, strings or the correlation id', () => {
    assert.equal(parseOccurrenceId({ OccurrenceId: Buffer.from(id) }, undefined), id);
    assert.equal(parseOccurrenceId({ CorrelationId: id }, undefined), id);
    assert.equal(parseOccurrenceId({}, id), id);
    assert.equal(parseOccurrenceId({ OccurrenceId: 'not-a-guid' }, id), id);
    assert.equal(parseOccurrenceId(undefined, undefined), undefined);
  });

  it('reads the retry count in the encodings brokers and clients use', () => {
    assert.equal(parseRetryCount({ 'x-retry-count': 2 }), 2);
    assert.equal(parseRetryCount({ 'x-retry-count': 3n }), 3);
    assert.equal(parseRetryCount({ 'x-retry-count': Buffer.from([4, 0, 0, 0]) }), 4);
    assert.equal(parseRetryCount({ 'x-retry-count': '5' }), 5);
    assert.equal(parseRetryCount({}), 0);
    assert.equal(parseRetryCount(undefined), 0);
  });
});

describe('formatError', () => {
  it('includes the cause chain like the .NET inner exception', () => {
    const text = formatError(new Error('outer', { cause: new TypeError('inner') }));

    assert.match(text, /^Type: Error\nMessage: outer\nStackTrace: /);
    assert.match(text, /Inner Exception:\nType: TypeError\nMessage: inner/);
    assert.equal(formatError('plain'), 'Type: string\nMessage: plain\n');
  });
});

describe('LogBuffer', () => {
  const log = (message: string): OccurrenceLog => ({ timestamp: new Date().toISOString(), level: 'Information', message });

  it('sends logs in batches and keeps their order after a failed send', async () => {
    const sent: WorkerLogBatchMessage[] = [];
    let fail = true;
    const buffer = new LogBuffer(async batch => {
      if (fail) {
        fail = false;
        throw new Error('broker down');
      }

      sent.push(batch);
    }, silentLogger, { batchSize: 1000, flushIntervalMs: 60_000 });

    buffer.add('o', 'i', log('1'));
    buffer.add('o', 'i', log('2'));
    await assert.rejects(buffer.flush());
    buffer.add('o', 'i', log('3'));
    await buffer.flush();
    buffer.dispose();

    assert.deepEqual(sent.flatMap(b => b.logs.map(l => l.log.message)), ['1', '2', '3']);
    assert.equal(sent[0]!.count, 2);
    assert.equal(sent[0]!.logs[0]!.workerId, 'i');
  });

  it('drops the oldest logs beyond its limit', async () => {
    const sent: string[] = [];
    const buffer = new LogBuffer(async batch => { sent.push(...batch.logs.map(l => l.log.message)); }, silentLogger, { batchSize: 1000, maxBufferedLogs: 2, flushIntervalMs: 60_000 });

    buffer.add('o', 'i', log('1'));
    buffer.add('o', 'i', log('2'));
    buffer.add('o', 'i', log('3'));
    await buffer.flush();
    buffer.dispose();

    assert.deepEqual(sent, ['2', '3']);
  });
});

describe('options', () => {
  it('applies defaults', () => {
    const options = resolveWorkerOptions({ workerId: ' report-worker ', logger: silentLogger });

    assert.equal(options.workerId, 'report-worker');
    assert.equal(options.rabbitmqUrl, 'amqp://guest:guest@localhost:5672/%2F');
    assert.equal(options.queueType, 'classic');
    assert.ok(options.redis && options.redis.cancellationChannel === 'Milvaion:JobScheduler:cancellation_channel');
    assert.equal(options.executionTimeoutSeconds, 3600);

    const job = resolveJobOptions('SendEmailJob', undefined, options);

    assert.equal(job.routingPattern, 'report-worker.sendemail.*');
    assert.equal(job.consumerId, 'sendemail-consumer');
    assert.equal(job.maxRetries, 3);
    assert.equal(job.baseRetryDelaySeconds, 5);
    assert.equal(job.executionTimeoutSeconds, 3600);
  });

  it('rejects a missing worker id', () => {
    assert.throws(() => resolveWorkerOptions({ workerId: '' }), /workerId is required/);
  });

  it('reads the environment', () => {
    const options = workerOptionsFromEnv({}, {
      MILVAION_WORKER_ID: 'env-worker',
      MILVAION_RABBITMQ_URL: 'amqp://u:p@rabbit:5672/',
      MILVAION_RABBITMQ_QUEUE_TYPE: 'Quorum',
      MILVAION_REDIS_URL: 'redis://redis:6379',
      MILVAION_REDIS_KEY_PREFIX: 'X:',
      MILVAION_MAX_PARALLEL_JOBS: '4',
    });

    assert.equal(options.workerId, 'env-worker');
    assert.equal(options.rabbitmq?.queueType, 'quorum');
    assert.equal(options.maxParallelJobs, 4);

    const resolved = resolveWorkerOptions({ ...options, logger: silentLogger });

    assert.ok(resolved.redis && resolved.redis.cancellationChannel === 'X:cancellation_channel');
    assert.equal(workerOptionsFromEnv({}, { MILVAION_WORKER_ID: 'w', MILVAION_REDIS_URL: 'false' }).redis, false);
    assert.equal(workerOptionsFromEnv({ redis: false }, { MILVAION_WORKER_ID: 'w' }).redis, false);
  });
});

describe('JobContext', () => {
  it('writes error logs with exception details', () => {
    const logs: OccurrenceLog[] = [];
    const ctx = createJobContext({
      occurrenceId: 'o', job: { id: 'j', jobNameInWorker: 'J', jobData: null }, retryCount: 0, instanceId: 'i',
      signal: new AbortController().signal, sink: l => logs.push(l),
    });

    ctx.error('failed', new RangeError('out of range'), { id: 7 });

    assert.equal(ctx.data, undefined);
    assert.equal(logs[0]!.level, 'Error');
    assert.equal(logs[0]!.category, 'UserDefined');
    assert.equal(logs[0]!.exceptionType, 'RangeError');
    assert.equal((logs[0]!.data as any).id, 7);
  });
});
