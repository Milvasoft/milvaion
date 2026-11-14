<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Tests;

use Milvasoft\Milvaion\Worker\JobStatus;
use Milvasoft\Milvaion\Worker\Kafka\Topics;
use Milvasoft\Milvaion\Worker\KafkaOptions;
use PHPUnit\Framework\TestCase;

/**
 * Integration tests against a real Kafka, with the worker in its own process
 * (tests/fixtures/kafka-worker.php). They run only when MILVAION_KAFKA_BROKERS is set and
 * ext-rdkafka is loaded, e.g.
 *
 *   docker run -d --name milvaion-kafka -p 29092:9092 \
 *     -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
 *     -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 \
 *     -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092 \
 *     -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \
 *     -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT \
 *     -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \
 *     -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \
 *     apache/kafka:3.9.0
 *   MILVAION_KAFKA_BROKERS=localhost:29092 vendor/bin/phpunit --filter Kafka
 *
 * Every test uses its own topic prefix, so the tests do not see each other's records. TLS and SASL
 * settings for a secured broker come from the MILVAION_KAFKA_* variables of KafkaOptions::fromEnv().
 */
final class KafkaIntegrationTest extends TestCase
{
    private const WORKER_ID = 'PHP-Kafka-Worker';

    private string $brokers;
    private string $prefix;
    private string $instanceId;
    private Topics $topics;
    /** @var resource|null */
    private $process = null;
    private string $stderr = '';
    private string $attempts = '';
    private string $workerOutput = '';
    private ?\RdKafka\KafkaConsumer $collector = null;
    /** @var array<string, list<\RdKafka\Message>> */
    private array $records = [];

    protected function setUp(): void
    {
        $brokers = trim((string) getenv('MILVAION_KAFKA_BROKERS'));

        if ($brokers === '') {
            $this->markTestSkipped('Set MILVAION_KAFKA_BROKERS (e.g. localhost:29092) to run the Kafka integration tests.');
        }

        if (!extension_loaded('rdkafka')) {
            $this->markTestSkipped('The Kafka integration tests need ext-rdkafka.');
        }

        $this->brokers = $brokers;
        $this->prefix = 'it' . bin2hex(random_bytes(4)) . '.';
        $this->instanceId = 'php-kafka-it-' . bin2hex(random_bytes(3));
        $this->topics = new Topics($this->prefix, self::WORKER_ID);
        $this->stderr = (string) tempnam(sys_get_temp_dir(), 'kafka-worker-log');
        $this->attempts = (string) tempnam(sys_get_temp_dir(), 'kafka-worker-attempts');
    }

    protected function tearDown(): void
    {
        $this->stopWorker();
        // Released, not closed: the destructor closes the consumer and destroys its librdkafka handle,
        // while an explicit close() leaves the handle's threads running until PHP exits (see
        // KafkaTransport::stop()).
        $this->collector = null;

        $this->workerOutput = is_file($this->stderr) ? (string) file_get_contents($this->stderr) : '';
        @unlink($this->stderr);
        @unlink($this->attempts);

        // The fixture reports its threads once Worker::run() has returned: only PHP's own should be left.
        if (preg_match('/threads after run: (\d+)/', $this->workerOutput, $m)) {
            $this->assertSame(1, (int) $m[1], 'the stopped worker left librdkafka threads running');
        }
    }

    protected function onNotSuccessfulTest(\Throwable $t): never
    {
        fwrite(STDERR, "\n--- worker output ---\n{$this->workerOutput}\n");

        throw $t;
    }

    public function testCompletesJob(): void
    {
        $this->startWorker();
        [$jobId, $occurrenceId] = $this->produceJob(['jobData' => '{"n":7}']);

        $statuses = $this->waitForStatus($occurrenceId, JobStatus::Completed);

        $this->assertSame(JobStatus::Running->value, $statuses[0]['status']);
        $this->assertNotNull($statuses[0]['startTime']);
        $final = end($statuses);
        $this->assertSame([$jobId, 'PHP-Kafka-Worker', $this->instanceId, '{"done":true,"retry":0}'],
            [$final['jobId'], $final['workerId'], $final['instanceId'], $final['result']]);

        foreach ($this->records[$this->topics->status] as $record) {
            $this->assertSame($occurrenceId, $record->key, 'status records are keyed by occurrence id');
        }

        $logs = $this->waitFor($this->topics->logs, fn (array $records) => $this->logMessages($records, 'hello from php') !== []);
        $this->assertSame($occurrenceId, $logs[0]->key, 'log records are keyed by the occurrence id of their first entry');
        $this->assertSame(['n' => 7], $this->logMessages($logs, 'hello from php')[0]['data']);

        $registrations = $this->records[$this->topics->registrations];
        $this->assertSame($this->instanceId, $registrations[0]->key);
        $registration = json_decode($registrations[0]->payload, true);
        $this->assertSame([self::WORKER_ID, $this->instanceId, ['TestJob']], [$registration['workerId'], $registration['instanceId'], $registration['jobTypes']]);

        $heartbeats = $this->waitFor($this->topics->heartbeats, fn (array $records) => $records !== []);
        $this->assertSame($this->instanceId, $heartbeats[0]->key);
        $this->assertSame($this->instanceId, json_decode($heartbeats[0]->payload, true)['instanceId']);

        $this->waitForCommitted($this->topics->jobs, 1);
    }

    public function testRetriesThenDeadLetters(): void
    {
        $this->startWorker(['TEST_MODE' => 'fail', 'TEST_MAX_RETRIES' => '2', 'TEST_BASE_DELAY' => '0.5']);
        [$jobId, $occurrenceId] = $this->produceJob();

        $dlq = $this->waitFor($this->topics->dlq, fn (array $records) => $records !== [], 60);
        $this->assertCount(1, $dlq);
        $this->assertSame($occurrenceId, $dlq[0]->key);

        foreach (['OccurrenceId' => $occurrenceId, 'CorrelationId' => $occurrenceId, 'x-retry-count' => '2', 'MaxRetries' => '2'] as $name => $value) {
            $this->assertSame($value, $dlq[0]->headers[$name] ?? null, "dead letter header {$name}");
        }

        $body = json_decode($dlq[0]->payload, true);
        $this->assertSame([$jobId, 'TestJob', JobStatus::Failed->value], [$body['id'], $body['jobNameInWorker'], $body['status']]);
        $this->assertStringStartsWith('Max retries (2) exceeded.', $body['exception']);

        $retries = $this->waitFor($this->topics->retry, fn (array $records) => count($records) >= 2);
        $this->assertCount(2, $retries);

        foreach ($retries as $i => $retry) {
            $this->assertSame($jobId, $retry->key, 'retry records are keyed by job id');
            $this->assertSame((string) ($i + 1), $retry->headers['x-retry-count']);
            $this->assertSame('2', $retry->headers['MaxRetries']);
            $this->assertSame($occurrenceId, $retry->headers['OccurrenceId']);
            $this->assertSame($occurrenceId, $retry->headers['CorrelationId']);

            // x-not-before is the write time plus BaseRetryDelaySeconds * 2^retryCount.
            $delay = ((int) $retry->headers['x-not-before']) - $retry->timestamp;
            $this->assertEqualsWithDelta(500 * (2 ** $i), $delay, 250, "retry {$i} due {$delay}ms after it was written");
        }

        $attempts = array_map(fn (string $line) => explode(' ', $line), array_filter(explode("\n", (string) file_get_contents($this->attempts))));
        $this->assertSame(['0', '1', '2'], array_column($attempts, 1));
        $this->assertGreaterThanOrEqual(0.5, $attempts[1][2] - $attempts[0][2], 'the second attempt waits 0.5s');
        $this->assertGreaterThanOrEqual(1.0, $attempts[2][2] - $attempts[1][2], 'the third attempt waits 1s');

        $statuses = $this->waitForStatus($occurrenceId, JobStatus::Failed);
        $codes = array_column($statuses, 'status');
        $this->assertSame(JobStatus::Failed->value, array_pop($codes));
        $this->assertSame([JobStatus::Running->value], array_values(array_unique($codes)), 'only Running precedes Failed');
        $this->assertGreaterThan(4, count($codes), 'job heartbeats continue during the retry waits');

        $this->waitForCommitted($this->topics->jobs, 1);
        $this->waitForCommitted($this->topics->retry, 2);
    }

    public function testForwardsPoisonRecordToTheDeadLetterTopic(): void
    {
        $this->startWorker();
        $this->produce($this->topics->jobs, 'poison-key', 'this is not json', ['Foo' => 'bar']);

        $dlq = $this->waitFor($this->topics->dlq, fn (array $records) => $records !== []);
        $this->assertSame('this is not json', $dlq[0]->payload);
        $this->assertSame('poison-key', $dlq[0]->key);
        $this->assertSame('bar', $dlq[0]->headers['Foo']);
        $this->assertSame($this->topics->jobs, $dlq[0]->headers['x-original-topic']);
        $this->assertSame('the message is not a valid job', $dlq[0]->headers['x-dlq-reason']);

        $this->waitForCommitted($this->topics->jobs, 1);
    }

    public function testRunsEveryJobOfAPartitionInOrder(): void
    {
        $this->startWorker();
        $jobId = $this->uuid();
        $occurrences = [];

        for ($i = 0; $i < 5; $i++) {
            $occurrences[] = $this->produceJob([], $jobId)[1];
        }

        $this->waitFor($this->topics->status, function (array $records) use ($occurrences): bool {
            $completed = array_filter(array_map(fn ($r) => json_decode($r->payload, true), $records), fn ($s) => $s['status'] === JobStatus::Completed->value);

            return count($completed) === count($occurrences);
        });

        $started = [];

        foreach ($this->records[$this->topics->status] as $record) {
            $status = json_decode($record->payload, true);

            if ($status['status'] === JobStatus::Running->value && !in_array($status['occurrenceId'], $started, true)) {
                $started[] = $status['occurrenceId'];
            }
        }

        $this->assertSame($occurrences, $started, 'jobs of one partition run in the order they were written');
        $this->waitForCommitted($this->topics->jobs, 5);
    }

    public function testShutdownWritesTheInterruptedJobBack(): void
    {
        $this->startWorker(['TEST_MODE' => 'sleep', 'TEST_SHUTDOWN_TIMEOUT' => '0.5']);
        [, $occurrenceId] = $this->produceJob();

        $this->waitForStatus($occurrenceId, JobStatus::Running);
        $this->stopWorker();

        $jobs = $this->waitFor($this->topics->jobs, fn (array $records) => count($records) >= 2);
        $this->assertCount(2, $jobs);
        $this->assertSame([$jobs[0]->key, $jobs[0]->payload, $jobs[0]->headers['OccurrenceId']], [$jobs[1]->key, $jobs[1]->payload, $jobs[1]->headers['OccurrenceId']]);
        $this->assertSame([$jobs[0]->partition], [$jobs[1]->partition]);

        $this->waitForCommitted($this->topics->jobs, 1);

        $this->drain(1.0);
        $codes = array_unique(array_map(fn ($r) => json_decode($r->payload, true)['status'], $this->records[$this->topics->status] ?? []));
        $this->assertSame([JobStatus::Running->value], array_values($codes), 'an interrupted job reports no final status');

        $heartbeats = array_map(fn ($r) => json_decode($r->payload, true), $this->records[$this->topics->heartbeats] ?? []);
        $this->assertTrue(end($heartbeats)['isStopping'], 'the last heartbeat says the instance is stopping');
    }

    /** @param array<string, string> $env */
    private function startWorker(array $env = []): void
    {
        $env = array_merge(getenv(), [
            'MILVAION_KAFKA_BROKERS' => $this->brokers,
            'TEST_PREFIX' => $this->prefix,
            'TEST_INSTANCE_ID' => $this->instanceId,
            'TEST_ATTEMPTS_FILE' => $this->attempts,
        ], $env);

        $process = proc_open([PHP_BINARY, __DIR__ . '/fixtures/kafka-worker.php'], [0 => ['pipe', 'r'], 1 => ['file', $this->stderr, 'a'], 2 => ['file', $this->stderr, 'a']], $pipes, null, $env);
        $this->assertIsResource($process);
        fclose($pipes[0]);
        $this->process = $process;

        $this->collector = $this->newCollector();

        // Registration is produced after the topics have been created.
        $this->waitFor($this->topics->registrations, fn (array $records) => array_filter($records, fn ($r) => $r->key === $this->instanceId) !== [], 60);
    }

    private function stopWorker(): void
    {
        if ($this->process === null) {
            return;
        }

        $status = proc_get_status($this->process);

        if ($status['running']) {
            posix_kill($status['pid'], SIGTERM);
        }

        $deadline = microtime(true) + 60;

        // Before PHP 8.3 only the first call after the child exits reports how it ended, so keep it.
        while (($status = proc_get_status($this->process))['running'] && microtime(true) < $deadline) {
            usleep(50_000);
        }

        if ($status['running']) {
            proc_terminate($this->process, SIGKILL);
            $this->fail('the worker did not stop');
        }

        proc_close($this->process);
        $this->process = null;

        // A worker that leaves librdkafka threads behind crashes while PHP shuts down (SIGSEGV).
        $this->assertFalse($status['signaled'], "the worker died with signal {$status['termsig']} while stopping");
        $this->assertSame(0, $status['exitcode'], 'the worker did not exit cleanly');
    }

    private function newCollector(): \RdKafka\KafkaConsumer
    {
        $conf = $this->conf();
        $conf->set('group.id', 'collector-' . bin2hex(random_bytes(4)));
        $conf->set('auto.offset.reset', 'earliest');
        $conf->set('enable.auto.commit', 'false');
        $conf->set('topic.metadata.refresh.interval.ms', '500');
        $conf->set('log_level', '3');

        $consumer = new \RdKafka\KafkaConsumer($conf);
        $consumer->subscribe(['^' . preg_quote($this->prefix, '/') . 'milvaion\..*']);

        return $consumer;
    }

    /**
     * Reads every topic of the test until $done accepts the records of $topic.
     *
     * @param callable(list<\RdKafka\Message>): bool $done
     * @return list<\RdKafka\Message>
     */
    private function waitFor(string $topic, callable $done, float $timeout = 30): array
    {
        $deadline = microtime(true) + $timeout;

        while (!$done($this->records[$topic] ?? [])) {
            if (microtime(true) > $deadline) {
                $this->fail("Timed out reading {$topic}; got " . count($this->records[$topic] ?? []) . ' record(s): '
                    . implode(' | ', array_map(fn ($r) => $r->payload, $this->records[$topic] ?? [])));
            }

            $this->drain(0.1);
        }

        return $this->records[$topic];
    }

    private function drain(float $seconds): void
    {
        $until = microtime(true) + $seconds;

        do {
            $message = $this->collector->consume(100);

            if ($message->err === RD_KAFKA_RESP_ERR_NO_ERROR) {
                $this->records[$message->topic_name][] = $message;
            }
        } while (microtime(true) < $until);
    }

    /** @return list<array<string, mixed>> statuses of the occurrence, up to the first $status */
    private function waitForStatus(string $occurrenceId, JobStatus $status): array
    {
        $mine = fn (array $records) => array_values(array_filter(array_map(fn ($r) => json_decode($r->payload, true), $records), fn ($s) => $s['occurrenceId'] === $occurrenceId));
        $this->waitFor($this->topics->status, fn (array $records) => in_array($status->value, array_column($mine($records), 'status'), true), 60);

        return $mine($this->records[$this->topics->status]);
    }

    /** Waits until the worker's group has committed $count records of $topic in total. */
    private function waitForCommitted(string $topic, int $count): void
    {
        $conf = $this->conf();
        $conf->set('group.id', $this->topics->group);
        $conf->set('log_level', '3');
        $consumer = new \RdKafka\KafkaConsumer($conf);
        $partitions = [new \RdKafka\TopicPartition($topic, 0), new \RdKafka\TopicPartition($topic, 1)];
        $deadline = microtime(true) + 15;
        $total = 0;

        try {
            while (microtime(true) < $deadline) {
                $total = array_sum(array_map(fn (\RdKafka\TopicPartition $p) => max(0, $p->getOffset()), $consumer->getCommittedOffsets($partitions, 5000)));

                if ($total === $count) {
                    return;
                }

                usleep(200_000);
            }

            $this->fail("committed offsets on {$topic} = {$total}, want {$count}");
        } finally {
            $consumer = null;
        }
    }

    /** Client settings of the test's own clients, with the TLS and SASL settings of MILVAION_KAFKA_*. */
    private function conf(): \RdKafka\Conf
    {
        $conf = new \RdKafka\Conf();

        foreach (KafkaOptions::fromEnv(null, brokers: $this->brokers)->clientConfig('milvaion-php-it') as $key => $value) {
            $conf->set($key, $value);
        }

        return $conf;
    }

    /**
     * @param array<string, mixed> $fields
     * @return array{0: string, 1: string} job id and occurrence id
     */
    private function produceJob(array $fields = [], ?string $jobId = null): array
    {
        $jobId ??= $this->uuid();
        $occurrenceId = $this->uuid();
        $body = json_encode(array_merge(['id' => $jobId, 'displayName' => 'Kafka test', 'jobNameInWorker' => 'TestJob', 'jobData' => '{"n":1}', 'workerId' => self::WORKER_ID], $fields));

        $this->produce($this->topics->jobs, $jobId, (string) $body, [
            'OccurrenceId' => $occurrenceId,
            'CorrelationId' => $occurrenceId,
            'JobType' => 'TestJob',
            'WorkerId' => self::WORKER_ID,
            'ExecuteAt' => gmdate('Y-m-d\TH:i:s\Z'),
            'Priority' => '5',
        ]);

        return [$jobId, $occurrenceId];
    }

    /** @param array<string, string> $headers */
    private function produce(string $topic, string $key, string $value, array $headers): void
    {
        $conf = $this->conf();
        $conf->set('log_level', '3');
        $producer = new \RdKafka\Producer($conf);
        $producer->newTopic($topic)->producev(RD_KAFKA_PARTITION_UA, 0, $value, $key, $headers);
        $this->assertSame(RD_KAFKA_RESP_ERR_NO_ERROR, $producer->flush(10_000));
    }

    /**
     * @param list<\RdKafka\Message> $records
     * @return list<array<string, mixed>>
     */
    private function logMessages(array $records, string $message): array
    {
        $found = [];

        foreach ($records as $record) {
            foreach (json_decode($record->payload, true)['logs'] ?? [] as $entry) {
                if (($entry['log']['message'] ?? null) === $message) {
                    $found[] = $entry['log'];
                }
            }
        }

        return $found;
    }

    private function uuid(): string
    {
        $hex = bin2hex(random_bytes(16));

        return sprintf('%s-%s-4%s-a%s-%s', substr($hex, 0, 8), substr($hex, 8, 4), substr($hex, 13, 3), substr($hex, 17, 3), substr($hex, 20, 12));
    }
}
