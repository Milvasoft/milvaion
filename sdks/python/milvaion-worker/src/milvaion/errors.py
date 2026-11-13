from __future__ import annotations

import traceback
from enum import Enum


class PermanentJobError(Exception):
    """A failure that retrying cannot fix, such as invalid job data.

    A job that raises it is not retried and goes straight to the dead letter queue.
    """


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
