"""Sample Milvaion worker.

    MILVAION_WORKER_ID=py-sample-worker \
    MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
    MILVAION_REDIS_URL=redis://localhost:6379 \
    python examples/basic_worker.py

On Kafka, pip install "milvaion-worker[kafka]" and set MILVAION_TRANSPORT=kafka and
MILVAION_KAFKA_BOOTSTRAP_SERVERS=localhost:9092 instead of MILVAION_RABBITMQ_URL.
"""

from __future__ import annotations

import asyncio
import logging
import os

from milvaion import JobContext, PermanentJobError, Worker, WorkerOptions

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

worker = Worker(WorkerOptions.from_env(
    worker_id=os.environ.get("MILVAION_WORKER_ID", "py-sample-worker"),
    heartbeat_interval=5,
))


@worker.job(
    "GreetJob",
    execution_timeout=60,
    data_schema={
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Who to greet"},
            "delayMs": {"type": "integer", "description": "How long the job takes"},
        },
        "required": ["name"],
    },
)
async def greet(ctx: JobContext) -> dict:
    data = ctx.data or {}

    if not data.get("name"):
        raise PermanentJobError('Job data needs a "name".')

    ctx.log(f"Greeting {data['name']}", {"attempt": ctx.retry_count + 1})

    # Cancelled from the dashboard or by a timeout: asyncio cancels this await.
    await asyncio.sleep(data.get("delayMs", 1000) / 1000)

    return {"greeting": f"Hello, {data['name']}!"}


@worker.job("FlakyJob", max_retries=2, base_retry_delay=2)
def flaky(ctx: JobContext) -> None:
    # A plain function runs in a thread. Fails on the first attempt to show retries in the dashboard.
    if ctx.retry_count == 0:
        raise RuntimeError("Simulated transient failure")

    ctx.log("Succeeded on retry")


if __name__ == "__main__":
    worker.run()
