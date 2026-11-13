import type { Delivery, JobOutput, JobSource } from './processor.ts';
import type { WorkerDiscoveryRequest, WorkerHeartbeatMessage, WorkerLogBatchMessage } from './protocol.ts';

/** What a transport calls back into the worker. */
export interface TransportHooks {
  /** Runs a delivery through the job processor. Resolves once the delivery is settled. */
  handle(delivery: Delivery, source: JobSource): Promise<void>;
  /** Called once the topology exists and consuming has started, after every (re)connection: registers the instance. */
  onReady(): Promise<void>;
  /** True once `stop()` has begun. */
  isStopping(): boolean;
}

/** The message broker under a worker: RabbitMQ or Kafka. */
export interface WorkerTransport {
  /** Where the job processor reports outcomes. */
  readonly output: JobOutput;
  /** Connects, creates the topology, starts consuming and calls `onReady`. */
  connect(): Promise<void>;
  publishRegistration(message: WorkerDiscoveryRequest): Promise<void>;
  publishHeartbeat(message: WorkerHeartbeatMessage): Promise<void>;
  publishLogs(batch: WorkerLogBatchMessage): Promise<void>;
  /** Stops taking new jobs. Publishing keeps working, so running jobs can still report. */
  stopConsuming(): Promise<void>;
  /** Disconnects. Safe to call when not connected, and more than once. */
  close(): Promise<void>;
}

export function withTimeout<T>(promise: Promise<T>, ms: number, message: string | (() => string)): Promise<T> {
  let timer: NodeJS.Timeout | undefined;

  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(typeof message === 'function' ? message() : message)), ms);
  });

  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
