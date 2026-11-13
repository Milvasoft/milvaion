import type { Logger } from './options.ts';
import type { OccurrenceLog, WorkerLogBatchMessage, WorkerLogMessage } from './protocol.ts';

export type BatchSender = (batch: WorkerLogBatchMessage) => Promise<void>;

export interface LogBufferOptions {
  /** A flush starts as soon as this many logs are buffered. Default: 50. */
  batchSize?: number;
  /** Buffered logs are flushed at least this often. Default: 500 ms. */
  flushIntervalMs?: number;
  /** Oldest logs are dropped beyond this. Default: 10 000. */
  maxBufferedLogs?: number;
}

/**
 * Buffers occurrence logs and sends them in batches. A batch that fails to send is kept and sent
 * first on the next flush, so logs keep their order.
 */
export class LogBuffer {
  private readonly buffer: WorkerLogMessage[] = [];
  private unsent: WorkerLogMessage[] | null = null;
  private flushing: Promise<void> = Promise.resolve();
  private timer: NodeJS.Timeout | null = null;
  private dropped = 0;
  private readonly batchSize: number;
  private readonly flushIntervalMs: number;
  private readonly maxBufferedLogs: number;
  private readonly send: BatchSender;
  private readonly logger: Logger;

  constructor(send: BatchSender, logger: Logger, options: LogBufferOptions = {}) {
    this.send = send;
    this.logger = logger;
    this.batchSize = options.batchSize ?? 50;
    this.flushIntervalMs = options.flushIntervalMs ?? 500;
    this.maxBufferedLogs = options.maxBufferedLogs ?? 10_000;
  }

  get pending(): number {
    return this.buffer.length + (this.unsent?.length ?? 0);
  }

  add(occurrenceId: string, instanceId: string, log: OccurrenceLog): void {
    if (this.buffer.length >= this.maxBufferedLogs) {
      this.buffer.shift();

      if (++this.dropped % 1000 === 1)
        this.logger.warn(`Log buffer is full; dropped ${this.dropped} oldest log(s) so far.`);
    }

    this.buffer.push({ occurrenceId, workerId: instanceId, log, messageTimestamp: new Date().toISOString() });

    if (this.buffer.length >= this.batchSize)
      void this.flush().catch(() => { /* retried by the timer */ });
    else
      this.ensureTimer();
  }

  /** Sends everything buffered so far. Rejects if a batch cannot be sent; it stays buffered. */
  flush(): Promise<void> {
    const run = this.flushing.catch(() => undefined).then(() => this.flushAll());

    this.flushing = run;

    return run;
  }

  /** Stops the timer. Call `flush()` first to send what is left. */
  dispose(): void {
    if (this.timer)
      clearTimeout(this.timer);

    this.timer = null;
  }

  private async flushAll(): Promise<void> {
    // Bounded by what is buffered now, so producers that keep logging cannot keep this looping.
    let rounds = Math.ceil(this.pending / (this.batchSize * 2)) + 1;

    while (rounds-- > 0) {
      const batch = this.unsent ?? this.buffer.splice(0, this.batchSize * 2);

      if (batch.length === 0)
        return;

      this.unsent = batch;

      await this.send({ logs: batch, batchTimestamp: new Date().toISOString(), count: batch.length });

      this.unsent = null;
    }
  }

  private ensureTimer(): void {
    if (this.timer)
      return;

    this.timer = setTimeout(() => {
      this.timer = null;

      this.flush().catch(error => {
        this.logger.warn('Failed to send logs; will retry.', error);
        this.ensureTimer();
      });
    }, this.flushIntervalMs);

    this.timer.unref();
  }
}
