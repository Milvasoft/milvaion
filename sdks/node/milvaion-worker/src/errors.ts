/**
 * Thrown by a job to signal a failure that retrying cannot fix, such as invalid job data or a
 * rejected business rule. The job is not retried and goes straight to the dead letter queue.
 */
export class PermanentJobError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'PermanentJobError';
  }
}

/**
 * Thrown by a job to say "do not retry now, run me again at this time", for example when an API
 * quota resets at a known time. The worker does not retry it and does not dead-letter it: the
 * occurrence ends as Failed and reports `retryAt` to Milvaion, which reruns workflow steps at that
 * time. A standalone job ends here.
 */
export class RetryAfterError extends Error {
  readonly retryAt: Date;

  constructor(retryAt: Date, message?: string, options?: { cause?: unknown }) {
    if (!(retryAt instanceof Date) || Number.isNaN(retryAt.getTime()))
      throw new TypeError('RetryAfterError needs a valid Date.');

    super(message ?? `Job asked to be retried at ${retryAt.toISOString()}.`, options);
    this.name = 'RetryAfterError';
    this.retryAt = new Date(retryAt.getTime());
  }
}

export type AbortKind = 'cancelled' | 'timeout' | 'shutdown';

/** Reason a running job's signal was aborted. Available as `ctx.signal.reason`. */
export class JobAbortedError extends Error {
  readonly kind: AbortKind;

  constructor(kind: AbortKind, message: string) {
    super(message);
    this.name = 'JobAbortedError';
    this.kind = kind;
  }
}

/** Formats an error the way the .NET SDK does, so the dashboard shows both the same way. */
export function formatError(error: unknown): string {
  if (!(error instanceof Error))
    return `Type: ${typeof error}\nMessage: ${String(error)}\n`;

  let formatted = `Type: ${error.name}\nMessage: ${error.message}\nStackTrace: ${error.stack ?? ''}\n`;

  if (error.cause !== undefined)
    formatted += `\nInner Exception:\n${formatError(error.cause)}`;

  return formatted;
}
