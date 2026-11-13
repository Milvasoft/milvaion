/**
 * Kafka topic and consumer group names. They must be identical in every SDK and in the API;
 * see docs/githubdocs/KAFKA-TRANSPORT.md.
 */

export interface KafkaTopicNames {
  /** `{prefix}milvaion.jobs.{worker}`: jobs from the API. */
  jobs: string;
  /** `{prefix}milvaion.jobs.{worker}.retry`: retries waiting for their due time. */
  retry: string;
  status: string;
  logs: string;
  heartbeats: string;
  registrations: string;
  dlq: string;
  /** `{prefix}milvaion.worker.{worker}`: shared by every instance of the worker. */
  group: string;
}

/** The worker id as it appears in topic names: lower case, every character outside `a-z 0-9 . _ -` replaced by `_`. */
export function kafkaWorkerSegment(workerId: string): string {
  return workerId.toLowerCase().replace(/[^a-z0-9._-]/g, '_');
}

export function kafkaTopicNames(workerId: string, prefix = ''): KafkaTopicNames {
  const worker = kafkaWorkerSegment(workerId);

  return {
    jobs: `${prefix}milvaion.jobs.${worker}`,
    retry: `${prefix}milvaion.jobs.${worker}.retry`,
    status: `${prefix}milvaion.status`,
    logs: `${prefix}milvaion.logs`,
    heartbeats: `${prefix}milvaion.heartbeats`,
    registrations: `${prefix}milvaion.registrations`,
    dlq: `${prefix}milvaion.dlq`,
    group: `${prefix}milvaion.worker.${worker}`,
  };
}
