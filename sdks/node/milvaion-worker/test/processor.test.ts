import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { PermanentJobError } from '../src/errors.ts';
import { JobOccurrenceStatus as S } from '../src/protocol.ts';
import { delivery, JobId, OccurrenceId, setup } from './helpers.ts';

describe('JobProcessor', () => {
  it('reports Running then Completed with the result and acks', async () => {
    const { processor, publisher, consumer } = setup({
      TestJob: async ctx => {
        ctx.log('working', { to: (ctx.data as any).to });
        return { sent: true };
      },
    });

    const d = delivery({ jobData: JSON.stringify({ to: 'a@b.c' }) });

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(publisher.statuses(), [S.Running, S.Completed]);

    const final = publisher.to('job_status_updates_queue').at(-1)!.body;

    assert.equal(final.occurrenceId, OccurrenceId);
    assert.equal(final.jobId, JobId);
    assert.equal(final.workerId, 'node-worker');
    assert.equal(final.instanceId, 'node-worker-abcd1234');
    assert.equal(final.result, '{"sent":true}');
    assert.ok(final.startTime && final.endTime && typeof final.durationMs === 'number');

    const log = publisher.logs().find(l => l.message === 'working') as any;

    assert.equal(log.level, 'Information');
    assert.equal(log.category, 'UserCode');
    assert.deepEqual(log.data, { to: 'a@b.c' });
  });

  it('flushes logs before the final status', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: ctx => { ctx.log('before end'); } });

    await processor.handle(delivery({}), consumer);

    const order = publisher.messages.map(m => m.routingKey === 'worker_logs_queue' ? 'logs' : m.body.status);
    const logsIndex = order.lastIndexOf('logs');

    assert.ok(logsIndex >= 0 && logsIndex < order.lastIndexOf(S.Completed));
    assert.equal(publisher.to('job_status_updates_queue').at(-1)!.body.result, 'Job TestJob completed successfully');
  });

  it('retries a failed job with backoff and a higher retry count', async () => {
    const { processor, publisher, consumer, sleeps } = setup({ TestJob: () => { throw new Error('boom'); } }, { maxRetries: 3, baseRetryDelaySeconds: 5 });
    const d = delivery({}, { 'x-retry-count': 1 });

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(sleeps, [10_000]);
    assert.deepEqual(publisher.statuses(), [S.Running], 'stays Running while retrying');

    const [retry] = publisher.to(consumer.queue);

    assert.ok(retry);
    assert.ok(Buffer.isBuffer(retry.raw), 'republishes the original body');
    assert.equal(retry.options.correlationId, OccurrenceId);
    assert.deepEqual(retry.options.headers, { CorrelationId: OccurrenceId, 'x-retry-count': 2, MaxRetries: 3 });
    assert.ok(publisher.logs().some(l => l.category === 'Retry' && l.message.includes('attempt 2/3')));
    assert.equal(publisher.deadLettered().length, 0);
  });

  it('dead-letters a job that exhausted its retries', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => { throw new Error('still broken'); } }, { maxRetries: 3 });
    const d = delivery({}, { 'x-retry-count': 3 });

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(publisher.statuses(), [S.Running, S.Failed]);

    const final = publisher.to('job_status_updates_queue').at(-1)!.body;

    assert.match(final.exception, /^Max retries \(3\) exceeded\. Original exception: Type: Error\nMessage: still broken/);

    const [dlq] = publisher.deadLettered();

    assert.ok(dlq);
    assert.equal(dlq.routingKey, 'failed_jobs');
    assert.equal(dlq.body.id, JobId);
    assert.equal(dlq.body.status, S.Failed);
    assert.deepEqual(dlq.options.headers, { CorrelationId: OccurrenceId, 'x-retry-count': 3, MaxRetries: 3 });
  });

  it('dead-letters a permanent failure without retrying', async () => {
    const { processor, publisher, consumer, sleeps } = setup({ TestJob: () => { throw new PermanentJobError('bad input'); } });
    const d = delivery({});

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(sleeps, []);
    assert.deepEqual(publisher.statuses(), [S.Running, S.Failed]);

    const [dlq] = publisher.deadLettered();

    assert.ok(dlq);
    assert.match(dlq.body.exception, /^\[PERMANENT FAILURE\] Type: PermanentJobError/);
    assert.equal(dlq.options.headers?.MaxRetries, 0);
  });

  it('treats job data that is not JSON as a permanent failure', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: ctx => ctx.data });

    await processor.handle(delivery({ jobData: '{not json' }), consumer);

    assert.match(publisher.deadLettered()[0]!.body.exception, /Job data is not valid JSON/);
  });

  it('times a job out even if the handler ignores its signal', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => new Promise(() => {}) });
    const d = delivery({ executionTimeoutSeconds: 0.05 });

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(publisher.statuses(), [S.Running, S.TimedOut]);
    assert.match(publisher.to('job_status_updates_queue').at(-1)!.body.exception, /exceeded maximum timeout of 0.05 seconds/);
  });

  it('cancels a running job when asked', async () => {
    let seenSignal: AbortSignal | undefined;
    const { processor, publisher, consumer, cancellations } = setup({
      TestJob: ctx => {
        seenSignal = ctx.signal;
        return new Promise(() => {});
      },
    });
    const d = delivery({});
    const running = processor.handle(d, consumer);

    await waitFor(() => seenSignal !== undefined);
    assert.ok(cancellations.cancel(OccurrenceId.toUpperCase(), 'Cancelled by user'));
    await running;

    assert.ok(seenSignal!.aborted);
    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(publisher.statuses(), [S.Running, S.Cancelled]);
  });

  it('returns a job interrupted by shutdown to the queue without a final status', async () => {
    let started = false;
    const { processor, publisher, consumer, cancellations } = setup({ TestJob: () => { started = true; return new Promise(() => {}); } });
    const d = delivery({});
    const running = processor.handle(d, consumer);

    await waitFor(() => started);
    cancellations.abortAll('shutdown', 'stopping');
    await running;

    assert.deepEqual(d.settled, ['requeue']);
    assert.deepEqual(publisher.statuses(), [S.Running]);
  });

  it('returns a job waiting to retry to the queue on shutdown', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => { throw new Error('boom'); } });
    const d = delivery({});

    processor.beginShutdown();
    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['requeue']);
    assert.equal(publisher.to(consumer.queue).length, 0);
  });

  it('dead-letters a job type this worker does not have', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => 'ok' });
    const d = delivery({ jobNameInWorker: 'OtherJob' });

    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['ack']);
    assert.deepEqual(publisher.statuses(), [S.Running, S.Failed]);
    assert.match(publisher.deadLettered()[0]!.body.exception, /^\[PERMANENT FAILURE\] No IJob implementation registered for type: OtherJob/);
  });

  it('resolves job names case-insensitively', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => 'done' });

    await processor.handle(delivery({ jobNameInWorker: 'testjob' }), consumer);

    assert.equal(publisher.to('job_status_updates_queue').at(-1)!.body.result, 'done');
  });

  it('rejects a body that is not a job', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => 'ok' });
    const d = delivery({});

    d.body = Buffer.from('not json');
    await processor.handle(d, consumer);

    assert.deepEqual(d.settled, ['nack']);
    assert.equal(publisher.messages.length, 0);
  });

  it('reports a redelivery before running the job again', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => 'ok' });

    await processor.handle(delivery({}, {}, true), consumer);

    assert.deepEqual(publisher.statuses(), [S.Running, S.Running, S.Completed]);
    assert.ok(publisher.logs().some(l => l.category === 'Redelivery'));
  });

  it('keeps retrying a final status the broker refused', async () => {
    const { processor, publisher, consumer } = setup({ TestJob: () => 'ok' });

    await processor.handle(delivery({}), consumer);
    publisher.messages.length = 0;
    publisher.failNext = 1;

    await processor.handle(delivery({}), consumer);

    assert.deepEqual(publisher.statuses(), [S.Running, S.Completed]);
  });

  it('tracks the number of running jobs', async () => {
    let release!: () => void;
    const { processor, consumer } = setup({ TestJob: () => new Promise<void>(resolve => { release = resolve; }) });
    const running = processor.handle(delivery({}), consumer);

    await waitFor(() => release !== undefined);
    assert.equal(processor.running, 1);
    assert.equal(await processor.waitForIdle(10), false);

    release();
    await running;

    assert.equal(processor.running, 0);
    assert.equal(await processor.waitForIdle(10), true);
  });
});

async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  while (!condition()) {
    if (Date.now() > deadline)
      throw new Error('Timed out waiting for condition');

    await new Promise(resolve => setTimeout(resolve, 5));
  }
}
