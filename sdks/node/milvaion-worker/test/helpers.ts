import { CancellationRegistry } from '../src/cancellation.ts';
import { LogBuffer } from '../src/log-buffer.ts';
import { resolveJobOptions, resolveWorkerOptions, type JobOptions, type Logger } from '../src/options.ts';
import { JobProcessor, type Delivery, type JobHandler, type Publisher, type PublishOptions, type RegisteredJob } from '../src/processor.ts';
import type { OccurrenceLog, WorkerLogBatchMessage } from '../src/protocol.ts';
import { queueNameForPattern } from '../src/routing.ts';

export const silentLogger: Logger = { debug() {}, info() {}, warn() {}, error() {} };

export interface Published {
  exchange: string;
  routingKey: string;
  body: any;
  raw: Buffer | object;
  options: PublishOptions;
}

export class FakePublisher implements Publisher {
  readonly messages: Published[] = [];
  failNext = 0;

  async sendToQueue(queue: string, body: Buffer | object, options: PublishOptions = {}): Promise<void> {
    return this.publish('', queue, body, options);
  }

  async publish(exchange: string, routingKey: string, body: Buffer | object, options: PublishOptions = {}): Promise<void> {
    if (this.failNext > 0) {
      this.failNext--;
      throw new Error('publish failed');
    }

    const parsed = Buffer.isBuffer(body) ? JSON.parse(body.toString('utf8')) : structuredClone(body);

    this.messages.push({ exchange, routingKey, body: parsed, raw: body, options });
  }

  to(queue: string): Published[] {
    return this.messages.filter(m => m.exchange === '' && m.routingKey === queue);
  }

  statuses(): number[] {
    return this.to('job_status_updates_queue').map(m => m.body.status);
  }

  logs(): OccurrenceLog[] {
    return this.to('worker_logs_queue').flatMap(m => (m.body as WorkerLogBatchMessage).logs.map(l => l.log));
  }

  deadLettered(): Published[] {
    return this.messages.filter(m => m.exchange === 'dlx_scheduled_jobs');
  }
}

export interface FakeDelivery extends Delivery {
  settled: Array<'ack' | 'nack' | 'requeue'>;
}

export const OccurrenceId = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';
export const JobId = '0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99';

export function delivery(job: Record<string, unknown>, headers: Record<string, unknown> = {}, redelivered = false): FakeDelivery {
  const settled: FakeDelivery['settled'] = [];

  return {
    body: Buffer.from(JSON.stringify({ id: JobId, displayName: 'Test', jobNameInWorker: 'TestJob', jobData: null, ...job })),
    headers: { OccurrenceId: Buffer.from(OccurrenceId), ...headers },
    redelivered,
    settled,
    ack: () => settled.push('ack'),
    nack: requeue => settled.push(requeue ? 'requeue' : 'nack'),
  };
}

export function setup(handlers: Record<string, JobHandler>, jobOptions: JobOptions = {}) {
  const options = resolveWorkerOptions({ workerId: 'node-worker', redis: false, logger: silentLogger, jobHeartbeatIntervalSeconds: 0, instanceId: 'node-worker-abcd1234' });
  const publisher = new FakePublisher();
  const logs = new LogBuffer(batch => publisher.sendToQueue('worker_logs_queue', batch), silentLogger, { flushIntervalMs: 5 });
  const cancellations = new CancellationRegistry();
  const sleeps: number[] = [];
  const jobs = new Map<string, RegisteredJob>();

  for (const [name, handler] of Object.entries(handlers)) {
    const resolved = resolveJobOptions(name, jobOptions, options);

    jobs.set(name.toLowerCase(), { name, handler, options: resolved, queue: queueNameForPattern(resolved.routingPattern) });
  }

  const processor = new JobProcessor({
    options,
    jobs,
    publisher,
    logs,
    cancellations,
    sleep: async (ms, signal) => {
      sleeps.push(ms);

      if (signal.aborted)
        throw signal.reason;
    },
  });

  const consumer = [...jobs.values()][0]!;

  return { processor, publisher, cancellations, sleeps, jobs, consumer, logs };
}
