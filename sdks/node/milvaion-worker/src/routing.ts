import { randomBytes } from 'node:crypto';
import { Queues } from './protocol.ts';

/**
 * Routing pattern used when a job does not configure one, identical to the .NET SDK:
 * `SendEmailJob` in worker `Mail-Worker` becomes `mail-worker.sendemail.*`.
 */
export function generateRoutingPattern(jobName: string, workerId: string): string {
  const name = jobName.replace(/job$/i, '').toLowerCase();

  return `${workerId.toLowerCase()}.${name}.*`;
}

/** Queue a job type is consumed from, derived from its routing pattern. */
export function queueNameForPattern(routingPattern: string): string {
  return `${Queues.jobs}.${routingPattern.replaceAll('*', 'wildcard').replaceAll('#', 'all')}`;
}

/** A new instance id for this process: `{workerId}-{8 hex chars}`. */
export function generateInstanceId(workerId: string): string {
  return `${workerId}-${randomBytes(4).toString('hex')}`;
}

/** Consumer id reported to the API when a job does not configure one. */
export function defaultConsumerId(jobName: string): string {
  return `${jobName.replace(/job$/i, '').toLowerCase()}-consumer`;
}
