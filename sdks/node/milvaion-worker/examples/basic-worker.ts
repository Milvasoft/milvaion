// Run from this package's folder: node --experimental-strip-types examples/basic-worker.ts
// Configure with MILVAION_* environment variables (see README), e.g.
//   MILVAION_WORKER_ID=node-sample-worker MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 MILVAION_REDIS_URL=redis://localhost:6379
// On Kafka, set MILVAION_TRANSPORT=kafka MILVAION_KAFKA_BOOTSTRAP_SERVERS=localhost:9092 instead of the RabbitMQ URL.
import { MilvaionWorker, PermanentJobError, workerOptionsFromEnv } from '../src/index.ts';

interface GreetData {
  name: string;
  delayMs?: number;
}

const worker = new MilvaionWorker(workerOptionsFromEnv({
  workerId: process.env.MILVAION_WORKER_ID ?? 'node-sample-worker',
  heartbeatIntervalSeconds: 5,
}));

worker.addJob<GreetData, { greeting: string }>('GreetJob', async ctx => {
  const { name, delayMs = 1000 } = ctx.data ?? ({} as GreetData);

  if (!name)
    throw new PermanentJobError('Job data needs a "name".');

  ctx.log(`Greeting ${name}`, { attempt: ctx.retryCount + 1 });

  // Honour the signal so cancelling from the dashboard stops the wait.
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(resolve, delayMs);

    ctx.signal.addEventListener('abort', () => {
      clearTimeout(timer);
      reject(ctx.signal.reason);
    }, { once: true });
  });

  return { greeting: `Hello, ${name}!` };
}, {
  executionTimeoutSeconds: 60,
  dataSchema: {
    type: 'object',
    properties: {
      name: { type: 'string', description: 'Who to greet' },
      delayMs: { type: 'integer', description: 'How long the job takes' },
    },
    required: ['name'],
  },
});

worker.addJob('FlakyJob', ctx => {
  // Fails on the first attempt to show retries in the dashboard.
  if (ctx.retryCount === 0)
    throw new Error('Simulated transient failure');

  ctx.log('Succeeded on retry');
}, { maxRetries: 2, baseRetryDelaySeconds: 2 });

await worker.start();

for (const signal of ['SIGINT', 'SIGTERM'] as const)
  process.once(signal, () => void worker.stop().then(() => process.exit(0)));
