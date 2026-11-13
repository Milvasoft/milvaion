"""Wire-level names shared with the Milvaion API.

See docs/githubdocs/WORKER-PROTOCOL.md in the repository for the full protocol.
"""

from __future__ import annotations

from enum import IntEnum

EXCHANGE_JOBS = "jobs.topic"
EXCHANGE_DEAD_LETTER = "dlx_scheduled_jobs"
DEAD_LETTER_ROUTING_KEY = "failed_jobs"

QUEUE_JOBS = "scheduled_jobs_queue"
QUEUE_WORKER_LOGS = "worker_logs_queue"
QUEUE_WORKER_HEARTBEAT = "worker_heartbeat_queue"
QUEUE_WORKER_REGISTRATION = "worker_registration_queue"
QUEUE_STATUS_UPDATES = "job_status_updates_queue"
QUEUE_FAILED_OCCURRENCES = "failed_jobs_queue"


class Status(IntEnum):
    """The API's JobOccurrenceStatus, sent as a number."""

    QUEUED = 0
    RUNNING = 1
    COMPLETED = 2
    FAILED = 3
    CANCELLED = 4
    TIMED_OUT = 5
    UNKNOWN = 6
    SKIPPED = 7
