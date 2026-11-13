# frozen_string_literal: true

module Milvaion
  # Wire-level names shared with the Milvaion API.
  # See docs/githubdocs/WORKER-PROTOCOL.md in the repository for the full protocol.
  module Protocol
    EXCHANGE_JOBS = "jobs.topic"
    EXCHANGE_DEAD_LETTER = "dlx_scheduled_jobs"
    DEAD_LETTER_ROUTING_KEY = "failed_jobs"

    QUEUE_JOBS = "scheduled_jobs_queue"
    QUEUE_WORKER_LOGS = "worker_logs_queue"
    QUEUE_WORKER_HEARTBEAT = "worker_heartbeat_queue"
    QUEUE_WORKER_REGISTRATION = "worker_registration_queue"
    QUEUE_STATUS_UPDATES = "job_status_updates_queue"
    QUEUE_FAILED_OCCURRENCES = "failed_jobs_queue"

    DEFAULT_KEY_PREFIX = "Milvaion:JobScheduler:"
  end

  # The API's JobOccurrenceStatus, sent as a number.
  module Status
    QUEUED = 0
    RUNNING = 1
    COMPLETED = 2
    FAILED = 3
    CANCELLED = 4
    TIMED_OUT = 5
    UNKNOWN = 6
    SKIPPED = 7

    def self.name_of(value)
      constants.find { |name| const_get(name) == value }&.to_s || value.to_s
    end
  end

  # Current UTC time as an ISO 8601 string with milliseconds, for example 2026-11-13T09:00:00.000Z.
  def self.utc_now
    iso(Time.now)
  end

  def self.iso(time)
    time&.utc&.iso8601(3)
  end
end
