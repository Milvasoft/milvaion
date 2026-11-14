"""Kafka wire contract shared with the Milvaion API and the other SDKs.

See docs/githubdocs/KAFKA-TRANSPORT.md in the repository. This module has no dependency on a Kafka
client library, so the options can be validated without the ``kafka`` extra installed.
"""

from __future__ import annotations

import bisect
import os
import re
import ssl
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass
from typing import Any

TRANSPORT_RABBITMQ = "rabbitmq"
TRANSPORT_KAFKA = "kafka"

HEADER_OCCURRENCE_ID = "OccurrenceId"
HEADER_CORRELATION_ID = "CorrelationId"
HEADER_RETRY_COUNT = "x-retry-count"
HEADER_MAX_RETRIES = "MaxRetries"
HEADER_NOT_BEFORE = "x-not-before"
HEADER_ORIGINAL_TOPIC = "x-original-topic"
HEADER_DLQ_REASON = "x-dlq-reason"

_TOPIC_PREFIX = re.compile(r"^[A-Za-z0-9._-]*$")
_SASL_MECHANISMS = {"plain": "PLAIN", "scramsha256": "SCRAM-SHA-256", "scramsha512": "SCRAM-SHA-512"}


@dataclass
class KafkaOptions:
    """Kafka transport settings. The defaults connect to localhost:9092 without TLS or SASL."""

    brokers: list[str] | str | None = None
    """Bootstrap servers as "host:port". An entry may itself be comma separated. Default: localhost:9092."""
    tls: bool = False
    """Turns TLS on. Setting any of the three files below turns it on too."""
    tls_ca_file: str | None = None
    """PEM bundle used to verify the brokers. Default: the system roots."""
    tls_cert_file: str | None = None
    """PEM client certificate for mutual TLS, set together with ``tls_key_file``."""
    tls_key_file: str | None = None
    sasl_mechanism: str | None = None
    """"plain", "scram-sha-256" or "scram-sha-512". None disables SASL."""
    sasl_username: str | None = None
    sasl_password: str | None = None
    topic_prefix: str = ""
    """Put in front of every topic and consumer group name, e.g. "prod.". Must match the API's."""
    auto_create_topics: bool = True
    """Create the worker's topics, and the shared topics it writes to, at startup."""
    num_partitions: int = 12
    """Partitions of the topics the worker creates."""
    replication_factor: int = -1
    """Replication of the topics the worker creates. -1 uses the broker's default.replication.factor."""
    max_in_flight_per_partition: int | None = None
    """Jobs of one partition that may run at the same time. Default: the worker's max_parallel_jobs."""


@dataclass
class ResolvedKafka:
    brokers: list[str]
    security_protocol: str
    ssl_context: ssl.SSLContext | None
    sasl_mechanism: str | None
    sasl_username: str | None
    sasl_password: str | None
    topic_prefix: str
    auto_create_topics: bool
    num_partitions: int
    replication_factor: int
    max_in_flight_per_partition: int

    def connection_options(self) -> dict[str, Any]:
        """Security keyword arguments of the aiokafka clients."""
        options: dict[str, Any] = {"bootstrap_servers": list(self.brokers), "security_protocol": self.security_protocol}

        if self.ssl_context is not None:
            options["ssl_context"] = self.ssl_context

        if self.sasl_mechanism is not None:
            options["sasl_mechanism"] = self.sasl_mechanism
            options["sasl_plain_username"] = self.sasl_username
            options["sasl_plain_password"] = self.sasl_password or ""

        return options


def resolve_kafka(options: KafkaOptions | None, max_parallel_jobs: int) -> ResolvedKafka:
    """Validates the options and fills in the defaults."""
    o = options or KafkaOptions()
    entries = [o.brokers] if isinstance(o.brokers, str) else list(o.brokers or [])
    brokers = [b.strip() for entry in entries for b in (entry or "").split(",") if b.strip()] or ["localhost:9092"]

    prefix = o.topic_prefix or ""

    if not _TOPIC_PREFIX.match(prefix):
        raise ValueError(f"kafka.topic_prefix may only contain a-z A-Z 0-9 . _ -, got {prefix!r}")

    partitions = 12 if not o.num_partitions else o.num_partitions

    if not 1 <= partitions <= 1 << 20:
        raise ValueError(f"kafka.num_partitions must be positive, got {o.num_partitions}")

    replication = -1 if not o.replication_factor else o.replication_factor

    if not -1 <= replication <= 32767:
        raise ValueError(f"kafka.replication_factor must be -1 or positive, got {o.replication_factor}")

    in_flight = o.max_in_flight_per_partition or max_parallel_jobs

    if in_flight < 1:
        raise ValueError(f"kafka.max_in_flight_per_partition must be positive, got {o.max_in_flight_per_partition}")

    context = _ssl_context(o)
    mechanism = _sasl_mechanism(o)

    if context is not None:
        protocol = "SASL_SSL" if mechanism else "SSL"
    else:
        protocol = "SASL_PLAINTEXT" if mechanism else "PLAINTEXT"

    return ResolvedKafka(
        brokers=brokers,
        security_protocol=protocol,
        ssl_context=context,
        sasl_mechanism=mechanism,
        sasl_username=o.sasl_username if mechanism else None,
        sasl_password=o.sasl_password if mechanism else None,
        topic_prefix=prefix,
        auto_create_topics=bool(o.auto_create_topics),
        num_partitions=partitions,
        replication_factor=replication,
        max_in_flight_per_partition=in_flight,
    )


def _ssl_context(o: KafkaOptions) -> ssl.SSLContext | None:
    if not (o.tls or o.tls_ca_file or o.tls_cert_file or o.tls_key_file):
        return None

    if bool(o.tls_cert_file) != bool(o.tls_key_file):
        raise ValueError("kafka.tls_cert_file and kafka.tls_key_file must be set together")

    try:
        context = ssl.create_default_context(cafile=o.tls_ca_file or None)
    except (OSError, ssl.SSLError) as error:
        raise ValueError(f"kafka.tls_ca_file {o.tls_ca_file!r} could not be loaded: {error}") from error

    context.minimum_version = ssl.TLSVersion.TLSv1_2

    if o.tls_cert_file:
        try:
            context.load_cert_chain(o.tls_cert_file, o.tls_key_file)
        except (OSError, ssl.SSLError) as error:
            raise ValueError(f"Loading the Kafka client certificate failed: {error}") from error

    return context


def _sasl_mechanism(o: KafkaOptions) -> str | None:
    raw = (o.sasl_mechanism or "").strip()
    normalized = raw.lower().replace("-", "").replace("_", "")

    if not normalized:
        return None

    if not o.sasl_username:
        raise ValueError("kafka.sasl_username is required with kafka.sasl_mechanism")

    mechanism = _SASL_MECHANISMS.get(normalized)

    if mechanism is None:
        raise ValueError(f'kafka.sasl_mechanism must be "plain", "scram-sha-256" or "scram-sha-512", got {raw!r}')

    return mechanism


def kafka_options_from_env(env: Callable[[str], str] | None = None) -> KafkaOptions:
    """Reads the MILVAION_KAFKA_* variables (see the README)."""
    get = env or (lambda key: os.environ.get(key, "").strip())

    def integer(*keys: str) -> int | None:
        for key in keys:
            value = get(key)

            if value:
                try:
                    return int(value)
                except ValueError as error:
                    raise ValueError(f"{key} must be an integer, got {value!r}") from error

        return None

    options = KafkaOptions(
        brokers=get("MILVAION_KAFKA_BOOTSTRAP_SERVERS") or get("KAFKA_BOOTSTRAP_SERVERS") or None,
        tls=get("MILVAION_KAFKA_TLS").lower() == "true",
        tls_ca_file=get("MILVAION_KAFKA_TLS_CA_FILE") or None,
        tls_cert_file=get("MILVAION_KAFKA_TLS_CERT_FILE") or None,
        tls_key_file=get("MILVAION_KAFKA_TLS_KEY_FILE") or None,
        sasl_mechanism=get("MILVAION_KAFKA_SASL_MECHANISM") or None,
        sasl_username=get("MILVAION_KAFKA_SASL_USERNAME") or None,
        sasl_password=get("MILVAION_KAFKA_SASL_PASSWORD") or None,
        topic_prefix=get("MILVAION_KAFKA_TOPIC_PREFIX"),
        auto_create_topics=get("MILVAION_KAFKA_AUTO_CREATE_TOPICS").lower() != "false",
    )

    partitions = integer("MILVAION_KAFKA_PARTITIONS", "MILVAION_KAFKA_NUM_PARTITIONS")
    replication = integer("MILVAION_KAFKA_REPLICATION_FACTOR")
    in_flight = integer("MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION")

    if partitions is not None:
        options.num_partitions = partitions

    if replication is not None:
        options.replication_factor = replication

    if in_flight is not None:
        options.max_in_flight_per_partition = in_flight

    return options


@dataclass(frozen=True)
class KafkaTopics:
    """Topic and consumer group names of one worker."""

    jobs: str
    retry: str
    status: str
    logs: str
    heartbeats: str
    registrations: str
    dlq: str
    group: str

    @classmethod
    def create(cls, prefix: str, worker_id: str) -> KafkaTopics:
        worker = worker_segment(worker_id)

        return cls(
            jobs=f"{prefix}milvaion.jobs.{worker}",
            retry=f"{prefix}milvaion.jobs.{worker}.retry",
            status=f"{prefix}milvaion.status",
            logs=f"{prefix}milvaion.logs",
            heartbeats=f"{prefix}milvaion.heartbeats",
            registrations=f"{prefix}milvaion.registrations",
            dlq=f"{prefix}milvaion.dlq",
            group=f"{prefix}milvaion.worker.{worker}",
        )


def worker_segment(worker_id: str) -> str:
    """The worker id as it appears in topic and group names: lower case, with every character
    outside a-z 0-9 . _ - replaced by "_" (one per character)."""
    out = []

    for char in worker_id:
        char = char.lower()
        out.append(char if ("a" <= char <= "z") or ("0" <= char <= "9") or char in "._-" else "_")

    return "".join(out)


def header_map(headers: Iterable[tuple[str, bytes | None]] | None) -> dict[str, Any]:
    """Record headers as the dict the processor reads. Values are strings; the last header of a name wins."""
    out: dict[str, Any] = {}

    for key, value in headers or ():
        out[key] = (value or b"").decode("utf-8", errors="replace")

    return out


def header_value(value: Any) -> str:
    """Header values are UTF-8 strings, numbers decimal strings."""
    if value is None:
        return ""

    if isinstance(value, str):
        return value

    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", errors="replace")

    if isinstance(value, bool):
        return "true" if value else "false"

    return str(value)


def record_headers(headers: Mapping[str, Any]) -> list[tuple[str, bytes]]:
    """Encodes headers as UTF-8 strings in a stable order."""
    return [(key, header_value(headers[key]).encode()) for key in sorted(headers)]


def not_before_ms(headers: Mapping[str, Any]) -> int | None:
    """x-not-before as Unix milliseconds, or None when it is missing or invalid."""
    value = headers.get(HEADER_NOT_BEFORE)
    text = header_value(value).strip() if value is not None else ""

    try:
        return int(text)
    except ValueError:
        return None


class OffsetTracker:
    """Follows the records of one partition that this instance has taken, in offset order, and
    knows how far the partition may be committed: up to the first record that is not finished,
    because a Kafka commit covers every record before it."""

    def __init__(self) -> None:
        self._offsets: list[int] = []
        self._done: list[bool] = []
        self.last = -1
        self._position = -1

    def add(self, offset: int) -> bool:
        """Registers a record taken from the partition. A repeated or older offset returns False."""
        if offset <= self.last:
            return False

        self.last = offset
        self._offsets.append(offset)
        self._done.append(False)

        return True

    def done(self, offset: int) -> bool:
        """Marks a record finished. Returns False for an offset the tracker does not hold."""
        i = bisect.bisect_left(self._offsets, offset)

        if i == len(self._offsets) or self._offsets[i] != offset:
            return False

        self._done[i] = True
        finished = 0

        while finished < len(self._done) and self._done[finished]:
            finished += 1

        if finished:
            self._position = self._offsets[finished - 1] + 1
            del self._offsets[:finished]
            del self._done[:finished]

        return True

    def commit_position(self) -> int | None:
        """The offset to commit: one past the end of the contiguous finished range, or None until
        the first record has finished."""
        return self._position if self._position >= 0 else None

    @property
    def outstanding(self) -> int:
        """Taken records that cannot be committed yet."""
        return len(self._offsets)
