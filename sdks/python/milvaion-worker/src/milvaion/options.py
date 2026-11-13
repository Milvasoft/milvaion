from __future__ import annotations

import logging
import os
import re
import secrets
from dataclasses import dataclass, field
from typing import Any

from .protocol import QUEUE_JOBS

DEFAULT_KEY_PREFIX = "Milvaion:JobScheduler:"


@dataclass
class WorkerOptions:
    """Worker configuration. Only ``worker_id`` is required."""

    worker_id: str
    """Identifies the worker application. Use the same value on every replica."""
    rabbitmq_url: str = "amqp://guest:guest@localhost:5672/"
    queue_type: str = "classic"
    """Must match the API's MilvaionConfig:RabbitMQ:QueueType: "classic" or "quorum"."""
    redis_url: str | None = "redis://localhost:6379"
    """Redis only carries cancellation requests. None disables them."""
    redis_key_prefix: str = DEFAULT_KEY_PREFIX
    """Must match the API's MilvaionConfig:Redis:KeyPrefix."""
    max_parallel_jobs: int = field(default_factory=lambda: (os.cpu_count() or 1) * 2)
    """Capacity reported for this instance."""
    execution_timeout: float | None = 3600
    """Default job timeout in seconds. None or 0 disables it."""
    heartbeat_interval: float = 30
    job_heartbeat_interval: float = 60
    """Keeps long jobs from being flagged as zombies. 0 disables it."""
    shutdown_timeout: float = 30
    """How long stop() lets running jobs finish before returning them to the queue."""
    startup_timeout: float = 30
    """How long start() waits for RabbitMQ and Redis."""
    version: str = "1.0.0"
    instance_id: str | None = None
    """Overrides the generated "{worker_id}-{8 hex}" id. Must be unique per process."""
    logger: logging.Logger | None = None

    def __post_init__(self) -> None:
        self.worker_id = (self.worker_id or "").strip()

        if not self.worker_id:
            raise ValueError("worker_id is required")

        self.queue_type = (self.queue_type or "classic").strip().lower()

        if self.queue_type not in ("classic", "quorum"):
            raise ValueError(f'queue_type must be "classic" or "quorum", got {self.queue_type!r}')

        if self.max_parallel_jobs <= 0:
            raise ValueError(f"max_parallel_jobs must be positive, got {self.max_parallel_jobs}")

        if not self.instance_id:
            self.instance_id = f"{self.worker_id}-{secrets.token_hex(4)}"

        if self.logger is None:
            self.logger = logging.getLogger("milvaion")

    @property
    def cancellation_channel(self) -> str:
        return f"{self.redis_key_prefix}cancellation_channel"

    @classmethod
    def from_env(cls, **overrides: Any) -> WorkerOptions:
        """Reads MILVAION_WORKER_ID, MILVAION_RABBITMQ_URL, MILVAION_RABBITMQ_QUEUE_TYPE,
        MILVAION_REDIS_URL ("false" disables Redis), MILVAION_REDIS_KEY_PREFIX,
        MILVAION_MAX_PARALLEL_JOBS, MILVAION_EXECUTION_TIMEOUT_SECONDS and
        MILVAION_HEARTBEAT_INTERVAL_SECONDS. Keyword arguments win over the environment."""
        env: dict[str, Any] = {}
        mapping = {
            "MILVAION_WORKER_ID": ("worker_id", str),
            "MILVAION_RABBITMQ_URL": ("rabbitmq_url", str),
            "MILVAION_RABBITMQ_QUEUE_TYPE": ("queue_type", str),
            "MILVAION_REDIS_KEY_PREFIX": ("redis_key_prefix", str),
            "MILVAION_MAX_PARALLEL_JOBS": ("max_parallel_jobs", int),
            "MILVAION_EXECUTION_TIMEOUT_SECONDS": ("execution_timeout", float),
            "MILVAION_HEARTBEAT_INTERVAL_SECONDS": ("heartbeat_interval", float),
        }

        for key, (name, convert) in mapping.items():
            value = os.environ.get(key, "").strip()

            if value:
                try:
                    env[name] = convert(value)
                except ValueError as error:
                    raise ValueError(f"{key} must be a number, got {value!r}") from error

        redis_url = os.environ.get("MILVAION_REDIS_URL", "").strip()

        if redis_url.lower() == "false":
            env["redis_url"] = None
        elif redis_url:
            env["redis_url"] = redis_url

        env.update(overrides)
        env.setdefault("worker_id", "")

        return cls(**env)


@dataclass
class JobOptions:
    """Configuration of one job type. Defaults apply to fields left as None."""

    routing_pattern: str | None = None
    """Default: "{worker_id}.{name without Job}.*"."""
    consumer_id: str | None = None
    """Default: "{name without Job}-consumer"."""
    max_parallel_jobs: int | None = None
    """Jobs of this type that may run at once on this instance (prefetch). Default: min(10, worker max)."""
    execution_timeout: float | None = None
    """Seconds. A timeout set on the job in the dashboard wins. 0 disables it. Default: the worker default."""
    max_retries: int = 3
    """Retries after the first failure before the job goes to the dead letter queue."""
    base_retry_delay: float = 5
    """Retry n waits base_retry_delay * 2^(n-1) seconds."""
    data_schema: dict[str, Any] | str | None = None
    """JSON Schema of the job data; the dashboard builds the job data form from it."""
    result_schema: dict[str, Any] | str | None = None


@dataclass
class ResolvedJob:
    name: str
    handler: Any
    routing_pattern: str
    queue: str
    consumer_id: str
    max_parallel_jobs: int
    execution_timeout: float
    max_retries: int
    base_retry_delay: float
    data_schema: dict[str, Any] | str | None
    result_schema: dict[str, Any] | str | None


def resolve_job(name: str, handler: Any, options: JobOptions, worker: WorkerOptions) -> ResolvedJob:
    max_parallel = options.max_parallel_jobs or min(10, worker.max_parallel_jobs)

    if not 1 <= max_parallel <= 65535:
        raise ValueError(f"max_parallel_jobs of job {name!r} must be between 1 and 65535, got {max_parallel}")

    pattern = (options.routing_pattern or "").strip() or generate_routing_pattern(name, worker.worker_id)
    timeout = options.execution_timeout if options.execution_timeout is not None else worker.execution_timeout

    return ResolvedJob(
        name=name,
        handler=handler,
        routing_pattern=pattern,
        queue=queue_name_for_pattern(pattern),
        consumer_id=options.consumer_id or f"{_trim_job_suffix(name).lower()}-consumer",
        max_parallel_jobs=max_parallel,
        execution_timeout=timeout or 0,
        max_retries=max(0, options.max_retries),
        base_retry_delay=max(0.0, options.base_retry_delay),
        data_schema=options.data_schema,
        result_schema=options.result_schema,
    )


def generate_routing_pattern(job_name: str, worker_id: str) -> str:
    """The routing pattern a job gets when it does not set one, identical to the .NET SDK:
    "SendEmailJob" in worker "Mail-Worker" becomes "mail-worker.sendemail.*"."""
    return f"{worker_id.lower()}.{_trim_job_suffix(job_name).lower()}.*"


def queue_name_for_pattern(pattern: str) -> str:
    """The queue a job type is consumed from."""
    return f"{QUEUE_JOBS}.{pattern.replace('*', 'wildcard').replace('#', 'all')}"


def _trim_job_suffix(name: str) -> str:
    return re.sub(r"job$", "", name, flags=re.IGNORECASE)


__all__ = [
    "JobOptions",
    "WorkerOptions",
    "generate_routing_pattern",
    "queue_name_for_pattern",
]
