/** Kafka record headers: UTF-8 strings on the wire, numbers as decimal strings. */

/** Header names of the Kafka wire contract. */
export const KafkaHeaders = {
  occurrenceId: 'OccurrenceId',
  correlationId: 'CorrelationId',
  jobType: 'JobType',
  workerId: 'WorkerId',
  executeAt: 'ExecuteAt',
  priority: 'Priority',
  retryCount: 'x-retry-count',
  maxRetries: 'MaxRetries',
  notBefore: 'x-not-before',
  originalTopic: 'x-original-topic',
  dlqReason: 'x-dlq-reason',
} as const;

/** Header values as a Kafka client hands them over. */
export type RawKafkaHeaders = Record<string, Buffer | string | Array<Buffer | string> | undefined>;

/** Decodes record headers to strings. A header that appears more than once keeps its last value. */
export function decodeKafkaHeaders(headers: RawKafkaHeaders | undefined): Record<string, string> {
  const decoded: Record<string, string> = {};

  for (const [name, value] of Object.entries(headers ?? {})) {
    const last = Array.isArray(value) ? value.at(-1) : value;

    if (last !== undefined)
      decoded[name] = Buffer.isBuffer(last) ? last.toString('utf8') : String(last);
  }

  return decoded;
}

/** Encodes header values as strings; numbers become decimal strings. */
export function encodeKafkaHeaders(headers: Record<string, string | number>): Record<string, string> {
  return Object.fromEntries(Object.entries(headers).map(([name, value]) => [name, String(value)]));
}

/** Occurrence id from `OccurrenceId`, then `CorrelationId`. */
export function kafkaOccurrenceId(headers: Record<string, string>): string | undefined {
  const value = headers[KafkaHeaders.occurrenceId]?.trim() || headers[KafkaHeaders.correlationId]?.trim();

  return value || undefined;
}

/** Retry attempt from `x-retry-count`; absent or invalid means 0. */
export function kafkaRetryCount(headers: Record<string, string>): number {
  const value = headers[KafkaHeaders.retryCount]?.trim();

  return value && /^\d+$/.test(value) ? Number(value) : 0;
}

/** Due time from `x-not-before` in Unix milliseconds, or undefined when absent or invalid (the record is due). */
export function kafkaNotBefore(headers: Record<string, string>): number | undefined {
  const value = headers[KafkaHeaders.notBefore]?.trim();

  return value && /^-?\d+$/.test(value) ? Number(value) : undefined;
}
