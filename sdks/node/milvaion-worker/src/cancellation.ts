import { Redis } from 'ioredis';
import { JobAbortedError, type AbortKind } from './errors.ts';
import type { Logger, ResolvedWorkerOptions } from './options.ts';
import type { CancellationRequest } from './protocol.ts';

/** Abort controllers of the jobs running on this instance, keyed by occurrence id. */
export class CancellationRegistry {
  private readonly running = new Map<string, AbortController>();

  register(occurrenceId: string, controller: AbortController): void {
    this.running.set(occurrenceId.toLowerCase(), controller);
  }

  unregister(occurrenceId: string): void {
    this.running.delete(occurrenceId.toLowerCase());
  }

  /** Returns false when the job is not running on this instance. */
  cancel(occurrenceId: string, reason?: string): boolean {
    const controller = this.running.get(occurrenceId.toLowerCase());

    if (!controller)
      return false;

    controller.abort(new JobAbortedError('cancelled', reason ? `Job was cancelled: ${reason}` : 'Job was cancelled.'));

    return true;
  }

  abortAll(kind: AbortKind, message: string): void {
    for (const controller of this.running.values())
      controller.abort(new JobAbortedError(kind, message));
  }
}

/** Listens on the Redis cancellation channel the API publishes to. */
export class CancellationListener {
  private client: Redis | null = null;
  private readonly options: Exclude<ResolvedWorkerOptions['redis'], false>;
  private readonly registry: CancellationRegistry;
  private readonly logger: Logger;

  constructor(options: Exclude<ResolvedWorkerOptions['redis'], false>, registry: CancellationRegistry, logger: Logger) {
    this.options = options;
    this.registry = registry;
    this.logger = logger;
  }

  async start(): Promise<void> {
    const common = {
      lazyConnect: true,
      // A subscriber must not fail commands while reconnecting; it resubscribes on its own.
      maxRetriesPerRequest: null,
      retryStrategy: (attempt: number) => Math.min(attempt * 500, 10_000),
    };

    const client = this.options.url
      ? new Redis(this.options.url, common)
      : new Redis({ ...common, host: this.options.host, port: this.options.port, password: this.options.password, db: this.options.db });

    client.on('error', error => this.logger.warn(`Redis cancellation listener error: ${error.message}`));
    client.on('message', (_channel: string, message: string) => this.onMessage(message));

    this.client = client;

    await client.connect();
    await client.subscribe(this.options.cancellationChannel);

    this.logger.info(`Listening for cancellations on ${this.options.cancellationChannel}`);
  }

  async stop(): Promise<void> {
    const client = this.client;

    this.client = null;

    if (!client)
      return;

    try {
      await client.quit();
    } catch {
      client.disconnect();
    }
  }

  private onMessage(message: string): void {
    let request: CancellationRequest;

    try {
      request = JSON.parse(message) as CancellationRequest;
    } catch {
      this.logger.debug('Ignored a cancellation message that is not JSON.');
      return;
    }

    const occurrenceId = request.CorrelationId ?? request.OccurrenceId;

    if (!occurrenceId)
      return;

    if (this.registry.cancel(occurrenceId, request.Reason))
      this.logger.info(`Cancelling occurrence ${occurrenceId}: ${request.Reason ?? 'no reason given'}`);
  }
}
