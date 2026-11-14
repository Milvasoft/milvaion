from __future__ import annotations

import traceback
from datetime import datetime, timezone
from enum import Enum


class PermanentJobError(Exception):
    """A failure that retrying cannot fix, such as invalid job data.

    A job that raises it is not retried and goes straight to the dead letter queue.
    """


class RetryAfterError(Exception):
    """Asks for the job to run again at a given UTC time instead of being retried now, for example
    when an API quota resets at 11:00.

    The worker does not retry it and does not dead-letter it. The occurrence ends as ``Failed`` and
    reports ``retry_at`` to Milvaion, which reruns a workflow step at that time. A standalone job
    ends there. A naive ``retry_at`` is treated as UTC.
    """

    def __init__(self, retry_at: datetime, message: str | None = None) -> None:
        if not isinstance(retry_at, datetime):
            raise TypeError("retry_at must be a datetime")

        self.retry_at: datetime = retry_at.replace(tzinfo=timezone.utc) if retry_at.tzinfo is None else retry_at.astimezone(timezone.utc)
        super().__init__(message or f"Job asked to be retried at {_iso_utc(self.retry_at)}.")

    @property
    def retry_at_iso(self) -> str:
        """``retry_at`` as an ISO-8601 UTC string ending in ``Z``."""
        return _iso_utc(self.retry_at)


def _iso_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


class AbortReason(str, Enum):
    """Why a running job was aborted."""

    CANCELLED = "cancelled"
    """Cancelled from the dashboard."""
    TIMED_OUT = "timed_out"
    """Ran past its execution timeout."""
    SHUTDOWN = "shutdown"
    """The worker is shutting down; the job goes back to the queue."""


class JobAborted(Exception):
    """Raised by ``JobContext.raise_if_aborted()`` once the job has been aborted."""

    def __init__(self, reason: AbortReason, message: str) -> None:
        super().__init__(message)
        self.reason = reason


def format_error(error: BaseException) -> str:
    """Renders an exception the way the .NET SDK does, so the dashboard shows both alike."""
    stack = "".join(traceback.format_tb(error.__traceback__))
    formatted = f"Type: {_type_name(error)}\nMessage: {error}\nStackTrace: {stack}\n"
    inner = error.__cause__ or (None if error.__suppress_context__ else error.__context__)

    if inner is not None:
        formatted += "\nInner Exception:\n" + format_error(inner)

    return formatted


def _type_name(error: BaseException) -> str:
    cls = type(error)

    return cls.__qualname__ if cls.__module__ == "builtins" else f"{cls.__module__}.{cls.__qualname__}"
