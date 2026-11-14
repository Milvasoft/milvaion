<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

use Milvasoft\Milvaion\Worker\Delivery;
use Milvasoft\Milvaion\Worker\JobStatus;
use Milvasoft\Milvaion\Worker\Processor;
use Milvasoft\Milvaion\Worker\RetryRequest;
use Milvasoft\Milvaion\Worker\WorkerOptions;
use Psr\Log\LoggerInterface;

/**
 * Runs a worker on Kafka (docs/githubdocs/KAFKA-TRANSPORT.md). One consumer group member reads
 * the worker's job and retry topics; records are buffered per partition, run one at a time (a PHP
 * worker process runs one job at a time) and committed in order once their outcome is reported.
 *
 * Everything here runs in the worker process. librdkafka is not fork safe, so job processes never
 * use these handles: they send their logs and outcome to this process, which produces and commits.
 *
 * @internal
 */
final class KafkaTransport
{
    private const COMMIT_INTERVAL = 0.5;

    private ?\RdKafka\KafkaConsumer $consumer = null;
    /** @var array<string, PartitionState> by PartitionState::key() */
    private array $parts = [];
    private bool $running = false;
    private bool $stopping = false;
    private int $cursor = 0;
    private float $nextCommit = 0;
    private float $lastFetchWarning = 0;
    private readonly int $inFlightPerPartition;

    /** @param \Closure(int): void $onJobCountChanged Receives the number of running jobs (0 or 1). */
    public function __construct(
        private readonly WorkerOptions $options,
        private readonly Topics $topics,
        private readonly KafkaPublisher $publisher,
        private readonly Processor $processor,
        private readonly LoggerInterface $logger,
        private readonly \Closure $onJobCountChanged,
    ) {
        $this->inFlightPerPartition = $options->kafka->inFlightPerPartition(1);
    }

    /**
     * Connects, creates the topics, registers the instance (through $register) and joins the
     * consumer group.
     *
     * @param \Closure(): void $register
     */
    public function start(\Closure $register): void
    {
        $kafka = $this->options->kafka;
        $config = $kafka->clientConfig($this->options->instanceId);
        $producer = $this->publisher->connect($config);
        $deadline = microtime(true) + $this->options->startupTimeout;

        while (true) {
            try {
                $producer->getMetadata(true, null, 5000);

                break;
            } catch (\Throwable $e) {
                $this->logger->warning('Could not connect to Kafka (' . implode(',', $kafka->brokers) . "): {$e->getMessage()}");

                if (microtime(true) + 1 > $deadline) {
                    $this->publisher->close();

                    throw new \RuntimeException("Could not connect to Kafka within {$this->options->startupTimeout}s: {$e->getMessage()}", 0, $e);
                }

                sleep(1);
            }
        }

        try {
            if ($kafka->autoCreateTopics) {
                (new Admin($kafka, $this->options->instanceId))->createTopics($this->topics->all(), $kafka->numPartitions, $kafka->replicationFactor);
            }

            $register();
        } catch (\Throwable $e) {
            $this->publisher->close();

            throw $e;
        }

        $conf = new \RdKafka\Conf();

        foreach ($config as $key => $value) {
            $conf->set($key, $value);
        }

        $conf->set('group.id', $this->topics->group);
        $conf->set('enable.auto.commit', 'false');
        $conf->set('enable.auto.offset.store', 'false');
        $conf->set('auto.offset.reset', 'earliest');
        $conf->set('partition.assignment.strategy', 'cooperative-sticky');
        $conf->set('enable.partition.eof', 'false');
        $conf->setRebalanceCb(function (\RdKafka\KafkaConsumer $consumer, int $err, ?array $partitions = null): void {
            $this->onRebalance($consumer, $err, $partitions ?? []);
        });

        $this->consumer = new \RdKafka\KafkaConsumer($conf);
        $this->consumer->subscribe([$this->topics->jobs, $this->topics->retry]);
        $this->nextCommit = microtime(true) + self::COMMIT_INTERVAL;

        $this->logger->info("Consuming {$this->topics->jobs} and {$this->topics->retry} in group {$this->topics->group}.");
    }

    /** One round of the worker loop: reads, runs the next record that is due, commits. */
    public function step(float $timeout): void
    {
        $this->poll($this->hasRunnable() ? 0 : (int) ($this->wait($timeout) * 1000));
        $this->dispatch();
        $this->commitIfDue();
    }

    /** Called regularly while a job runs: keeps the group membership alive and commits. */
    public function service(): void
    {
        if ($this->consumer === null) {
            return;
        }

        $this->poll(0);
        $this->commitIfDue();
    }

    /** Stops taking records, commits what has finished and leaves the group. */
    public function stop(): void
    {
        $this->stopping = true;

        if ($this->consumer === null) {
            return;
        }

        foreach ($this->parts as $ps) {
            $this->releaseHold($ps);
        }

        $this->syncPauses();
        $this->commit();

        try {
            $this->consumer->close();
        } catch (\Throwable $e) {
            $this->logger->warning('Could not leave the Kafka consumer group cleanly: ' . $e->getMessage());
        }

        $this->consumer = null;
        $this->parts = [];
    }

    /** Disconnects the producer after the final heartbeat and logs have been sent. */
    public function close(): void
    {
        $this->publisher->close();
    }

    private function poll(int $timeoutMs): void
    {
        if ($this->consumer === null) {
            return;
        }

        try {
            $message = $this->consumer->consume($timeoutMs);
        } catch (\Throwable $e) {
            $this->warnFetch('Kafka consume failed: ' . $e->getMessage());

            return;
        }

        switch ($message->err) {
            case RD_KAFKA_RESP_ERR_NO_ERROR:
                $this->accept($message);
                break;
            case RD_KAFKA_RESP_ERR__TIMED_OUT:
            case RD_KAFKA_RESP_ERR__PARTITION_EOF:
                break;
            default:
                $this->warnFetch('Kafka fetch failed: ' . $message->errstr());
        }

        $this->syncPauses();
    }

    private function accept(\RdKafka\Message $message): void
    {
        // A record of a partition revoked since is left to its new owner.
        $ps = $this->parts[PartitionState::key($message->topic_name, $message->partition)] ?? null;

        if ($ps === null || $this->stopping || !$ps->tracker->add($message->offset)) {
            return;
        }

        $headers = [];

        foreach ($message->headers ?? [] as $name => $value) {
            $headers[(string) $name] = (string) $value;
        }

        $ps->backlog[] = new Record($message->topic_name, $message->partition, $message->offset, $message->key, $message->payload, $headers);
    }

    /** Whether a buffered record could run now. */
    private function hasRunnable(): bool
    {
        $now = microtime(true);

        foreach ($this->parts as $ps) {
            if ($ps->backlog !== [] && (!$ps->retry || (Headers::notBefore($ps->backlog[0]->headers) ?? 0) <= $now)) {
                return true;
            }
        }

        return false;
    }

    /** How long to wait for records: no longer than until the next held retry is due. */
    private function wait(float $timeout): float
    {
        $now = microtime(true);

        foreach ($this->parts as $ps) {
            if ($ps->retry && $ps->backlog !== [] && ($due = Headers::notBefore($ps->backlog[0]->headers)) !== null) {
                $timeout = min($timeout, max(0.0, $due - $now));
            }
        }

        return $timeout;
    }

    /**
     * Runs the next buffered record that is due, if the instance slot is free, and holds retry
     * records that are not. Partitions take turns.
     */
    private function dispatch(): void
    {
        if ($this->running || $this->stopping || $this->processor->stopRequested()) {
            return;
        }

        $states = array_values($this->parts);
        $count = count($states);
        $start = $count > 0 ? $this->cursor++ % $count : 0;
        $now = microtime(true);

        for ($i = 0; $i < $count; $i++) {
            $ps = $states[($start + $i) % $count];

            if ($ps->backlog === []) {
                continue;
            }

            $next = $ps->backlog[0];

            if ($ps->retry && ($due = Headers::notBefore($next->headers)) !== null && $due > $now) {
                $this->hold($ps, $next, $due);

                continue;
            }

            $this->releaseHold($ps);
            array_shift($ps->backlog);
            $this->run($ps, $next);

            break;
        }

        $this->syncPauses();
    }

    /**
     * Pauses every partition while the instance slot is busy (the consumer keeps polling, so the
     * group does not evict it), and otherwise the partitions that have records waiting here, so
     * the consumer does not buffer more of them. A retry partition holding a record that is not
     * due counts as full.
     */
    private function syncPauses(): void
    {
        if ($this->consumer === null) {
            return;
        }

        $pause = [];
        $resume = [];

        foreach ($this->parts as $ps) {
            $want = $this->running || $this->stopping || count($ps->backlog) >= $this->inFlightPerPartition || ($ps->backlog !== [] && $ps->retry);

            if ($want !== $ps->paused) {
                $ps->paused = $want;
                $partition = new \RdKafka\TopicPartition($ps->topic, $ps->partition);

                if ($want) {
                    $pause[] = $partition;
                } else {
                    $resume[] = $partition;
                }
            }
        }

        try {
            if ($pause !== []) {
                $this->consumer->pausePartitions($pause);
            }

            if ($resume !== []) {
                $this->consumer->resumePartitions($resume);
            }
        } catch (\Throwable $e) {
            $this->logger->warning('Could not pause or resume Kafka partitions: ' . $e->getMessage());
        }
    }

    /**
     * Keeps the head of a retry partition until it is due, sending the occurrence's job heartbeats
     * meanwhile, since the occurrence stays Running.
     */
    private function hold(PartitionState $ps, Record $record, float $due): void
    {
        if ($ps->heldOffset === $record->offset) {
            return;
        }

        $this->releaseHold($ps);
        $ps->heldOffset = $record->offset;

        $job = Processor::parseJob($record->value ?? '');
        $occurrenceId = Processor::parseOccurrenceId($record->headers, null);

        if ($job === null || $occurrenceId === null) {
            return;
        }

        $ps->heldOccurrenceId = $occurrenceId;
        $run = $this->processor->newRun($job, $occurrenceId, Processor::parseRetryCount($record->headers));
        $run->status(JobStatus::Running, retry: false);
        $this->processor->heartbeatUntil($run, $due);
    }

    private function releaseHold(PartitionState $ps): void
    {
        if ($ps->heldOccurrenceId !== null) {
            $this->processor->stopHeartbeat($ps->heldOccurrenceId);
        }

        $ps->heldOffset = null;
        $ps->heldOccurrenceId = null;
    }

    private function run(PartitionState $ps, Record $record): void
    {
        $this->running = true;
        $this->syncPauses();
        ($this->onJobCountChanged)(1);

        try {
            $this->processor->handle(new Delivery(
                $record->value ?? '',
                $record->headers,
                null,
                false,
                fn () => $this->markDone($ps, $record->offset),
                fn (bool $requeue) => $requeue ? $this->requeue($ps, $record) : $this->deadLetterRaw($ps, $record, 'rejected by the worker'),
                fn (string $reason) => $this->deadLetterRaw($ps, $record, $reason),
                fn (RetryRequest $request) => $this->produceRetry($record, $request),
                $record->topic,
            ));
        } finally {
            $this->running = false;
            ($this->onJobCountChanged)(0);
        }
    }

    /** Lets the record's offset be committed. A partition revoked meanwhile is left to its new owner. */
    private function markDone(PartitionState $ps, int $offset): void
    {
        if (($this->parts[PartitionState::key($ps->topic, $ps->partition)] ?? null) === $ps) {
            $ps->tracker->done($offset);
        }
    }

    /** Writes an interrupted job back to its own topic, the counterpart of a RabbitMQ requeue. */
    private function requeue(PartitionState $ps, Record $record): void
    {
        $this->publisher->produceReliably($record->topic, $record->key, $record->value, $record->headers);
        $this->markDone($ps, $record->offset);
    }

    /** Forwards a record the worker cannot process to the dead letter topic unchanged. */
    private function deadLetterRaw(PartitionState $ps, Record $record, string $reason): void
    {
        $headers = $record->headers;
        $headers[Headers::ORIGINAL_TOPIC] = $record->topic;
        $headers[Headers::DLQ_REASON] = $reason;

        $key = Processor::parseOccurrenceId($record->headers, null) ?? $record->key;
        $this->publisher->produceReliably($this->topics->dlq, $key, $record->value, $headers);
        $this->markDone($ps, $record->offset);
    }

    /** Writes the next attempt to the retry topic, due at $request->notBefore. */
    private function produceRetry(Record $record, RetryRequest $request): void
    {
        $headers = $record->headers;
        $headers[Headers::OCCURRENCE_ID] = $request->occurrenceId;
        $headers[Headers::CORRELATION_ID] = $request->occurrenceId;
        $headers[Headers::RETRY_COUNT] = $request->retryCount;
        $headers[Headers::MAX_RETRIES] = $request->maxRetries;
        $headers[Headers::NOT_BEFORE] = Headers::millis($request->notBefore);

        $key = $request->jobId !== '' ? $request->jobId : $record->key;
        $this->publisher->produce($this->topics->retry, $key, $record->value, Headers::encode($headers));
    }

    private function commitIfDue(): void
    {
        if (microtime(true) >= $this->nextCommit) {
            $this->nextCommit = microtime(true) + self::COMMIT_INTERVAL;
            $this->commit();
        }
    }

    /**
     * Commits the finished position of every partition (or only of the given ones) that moved.
     *
     * @param list<string>|null $only PartitionState keys
     */
    private function commit(?array $only = null): void
    {
        if ($this->consumer === null) {
            return;
        }

        $offsets = [];
        $states = [];

        foreach ($this->parts as $key => $ps) {
            if ($only !== null && !in_array($key, $only, true)) {
                continue;
            }

            $position = $ps->tracker->commitPosition();

            if ($position === null || $position <= $ps->committed) {
                continue;
            }

            $offsets[] = new \RdKafka\TopicPartition($ps->topic, $ps->partition, $position);
            $states[] = [$ps, $position];
        }

        if ($offsets === []) {
            return;
        }

        try {
            $this->consumer->commit($offsets);
        } catch (\Throwable $e) {
            $this->logger->warning('Kafka offset commit failed; will retry: ' . $e->getMessage());

            return;
        }

        foreach ($states as [$ps, $position]) {
            $ps->committed = max($ps->committed, $position);
        }
    }

    /** @param list<\RdKafka\TopicPartition> $partitions */
    private function onRebalance(\RdKafka\KafkaConsumer $consumer, int $err, array $partitions): void
    {
        $names = implode(', ', array_map(fn (\RdKafka\TopicPartition $p) => "{$p->getTopic()}[{$p->getPartition()}]", $partitions));

        if ($err === RD_KAFKA_RESP_ERR__ASSIGN_PARTITIONS) {
            $this->changeAssignment($consumer, $partitions, true);

            foreach ($partitions as $p) {
                $key = PartitionState::key($p->getTopic(), $p->getPartition());
                $this->parts[$key] ??= new PartitionState($p->getTopic(), $p->getPartition(), $p->getTopic() === $this->topics->retry);
            }

            if ($partitions !== []) {
                $this->logger->info("Kafka partitions assigned: {$names}");
            }

            return;
        }

        // Revoked (or lost): commit what has finished, then forget. Jobs still running on them
        // finish and report, but their offsets are left to the new owner.
        $keys = array_map(fn (\RdKafka\TopicPartition $p) => PartitionState::key($p->getTopic(), $p->getPartition()), $partitions);

        if ($err === RD_KAFKA_RESP_ERR__REVOKE_PARTITIONS) {
            $this->commit($keys);
        } else {
            $this->logger->warning('Kafka rebalance error: ' . rd_kafka_err2str($err));
        }

        foreach ($keys as $key) {
            if (isset($this->parts[$key])) {
                $this->releaseHold($this->parts[$key]);
                unset($this->parts[$key]);
            }
        }

        $this->changeAssignment($consumer, $partitions, false);

        if ($partitions !== []) {
            $this->logger->info("Kafka partitions revoked: {$names}");
        }
    }

    /**
     * Applies an assignment change. The worker asks for cooperative-sticky, so changes are
     * incremental; if the group still runs the eager protocol (a member that does not support
     * cooperative rebalancing), the whole assignment is replaced instead.
     *
     * @param list<\RdKafka\TopicPartition> $partitions
     */
    private function changeAssignment(\RdKafka\KafkaConsumer $consumer, array $partitions, bool $assign): void
    {
        try {
            $assign ? $consumer->incrementalAssign($partitions) : $consumer->incrementalUnassign($partitions);
        } catch (\RdKafka\Exception $e) {
            $this->logger->debug('Incremental assignment failed, using an eager one: ' . $e->getMessage());
            $consumer->assign($assign ? $partitions : null);
        }
    }

    private function warnFetch(string $message): void
    {
        // At most one warning every 10 seconds while the brokers are unreachable.
        if (microtime(true) - $this->lastFetchWarning >= 10) {
            $this->lastFetchWarning = microtime(true);
            $this->logger->warning($message);
        }
    }
}
