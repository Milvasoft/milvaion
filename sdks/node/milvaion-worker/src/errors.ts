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
