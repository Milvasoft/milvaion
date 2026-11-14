<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Tests;

use Milvasoft\Milvaion\Worker\Kafka\Admin;
use Milvasoft\Milvaion\Worker\Kafka\Headers;
use Milvasoft\Milvaion\Worker\Kafka\KafkaPublisher;
use Milvasoft\Milvaion\Worker\Kafka\OffsetTracker;
use Milvasoft\Milvaion\Worker\Kafka\Scram;
use Milvasoft\Milvaion\Worker\Kafka\Topics;
use Milvasoft\Milvaion\Worker\KafkaOptions;
use Milvasoft\Milvaion\Worker\Protocol;
use Milvasoft\Milvaion\Worker\Worker;
use Milvasoft\Milvaion\Worker\WorkerOptions;
use PHPUnit\Framework\TestCase;

/** Kafka transport logic that needs neither ext-rdkafka nor a broker. */
final class KafkaTest extends TestCase
{
    private const OCCURRENCE_ID = '0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00';

    public function testTopicNames(): void
    {
        $topics = new Topics('prod.', 'Report Worker/01');

        $this->assertSame('prod.milvaion.jobs.report_worker_01', $topics->jobs);
        $this->assertSame('prod.milvaion.jobs.report_worker_01.retry', $topics->retry);
        $this->assertSame('prod.milvaion.status', $topics->status);
        $this->assertSame('prod.milvaion.logs', $topics->logs);
        $this->assertSame('prod.milvaion.heartbeats', $topics->heartbeats);
        $this->assertSame('prod.milvaion.registrations', $topics->registrations);
        $this->assertSame('prod.milvaion.dlq', $topics->dlq);
        $this->assertSame('prod.milvaion.worker.report_worker_01', $topics->group);
        $this->assertCount(7, $topics->all());
    }

    public function testWorkerSegment(): void
    {
        $this->assertSame('email-worker.v2_x', Topics::workerSegment('Email-Worker.V2_x'));
        $this->assertSame('a_b_c', Topics::workerSegment('a b:c'));
        // One "_" per character, not per byte.
        $this->assertSame('caf_', Topics::workerSegment('café'));
        $this->assertSame('milvaion.jobs.w', (new Topics('', 'W'))->jobs);
    }

    public function testOptionDefaults(): void
    {
        $o = new KafkaOptions();

        $this->assertSame(['localhost:9092'], $o->brokers);
        $this->assertSame(12, $o->numPartitions);
        $this->assertSame(-1, $o->replicationFactor);
        $this->assertTrue($o->autoCreateTopics);
        $this->assertSame('', $o->topicPrefix);
        $this->assertSame(1, $o->inFlightPerPartition(1));
        $this->assertFalse($o->tlsEnabled());
        $this->assertNull($o->saslMechanismName());
        $this->assertSame(['bootstrap.servers' => 'localhost:9092', 'client.id' => 'w-1', 'security.protocol' => 'PLAINTEXT'], $o->clientConfig('w-1'));
    }

    public function testBrokersAreSplitAndTrimmed(): void
    {
        $this->assertSame(['a:9092', 'b:9092', 'c:9092'], (new KafkaOptions(['a:9092, b:9092', ' c:9092 ', '']))->brokers);
        $this->assertSame(['a:1', 'b:2'], (new KafkaOptions('a:1,b:2'))->brokers);
        $this->assertSame(['localhost:9092'], (new KafkaOptions(' , '))->brokers);
    }

    public function testReplicationZeroMeansBrokerDefault(): void
    {
        $this->assertSame(-1, (new KafkaOptions(replicationFactor: 0))->replicationFactor);
        $this->assertSame(3, (new KafkaOptions(replicationFactor: 3))->replicationFactor);
    }

    /** @return iterable<string, array{0: array<string, mixed>, 1: string}> */
    public static function invalidOptions(): iterable
    {
        yield 'prefix' => [['topicPrefix' => 'prod/'], 'topicPrefix'];
        yield 'partitions' => [['numPartitions' => 0], 'numPartitions'];
        yield 'replication' => [['replicationFactor' => -2], 'replicationFactor'];
        yield 'in flight' => [['maxInFlightPerPartition' => 0], 'maxInFlightPerPartition'];
        yield 'cert without key' => [['tlsCertFile' => '/c.pem'], 'tlsCertFile and tlsKeyFile'];
        yield 'mechanism' => [['saslMechanism' => 'gssapi', 'saslUsername' => 'u'], 'saslMechanism'];
        yield 'username' => [['saslMechanism' => 'plain'], 'saslUsername'];
    }

    /** @param array<string, mixed> $args */
    #[\PHPUnit\Framework\Attributes\DataProvider('invalidOptions')]
    public function testRejectsInvalidOptions(array $args, string $message): void
    {
        $this->expectException(\InvalidArgumentException::class);
        $this->expectExceptionMessage($message);

        new KafkaOptions(...$args);
    }

    public function testSecurityProtocolAndSasl(): void
    {
        $sasl = static fn (array $args): KafkaOptions => new KafkaOptions(...array_merge($args, ['saslUsername' => 'u', 'saslPassword' => 'p']));

        $this->assertSame('SSL', (new KafkaOptions(tls: true))->clientConfig('c')['security.protocol']);
        $this->assertSame('SSL', (new KafkaOptions(tlsCaFile: '/ca.pem'))->clientConfig('c')['security.protocol']);
        $this->assertSame('SASL_PLAINTEXT', $sasl(['saslMechanism' => 'plain'])->clientConfig('c')['security.protocol']);

        $config = $sasl(['tls' => true, 'tlsCaFile' => '/ca.pem', 'tlsCertFile' => '/c.pem', 'tlsKeyFile' => '/k.pem', 'saslMechanism' => 'SCRAM-SHA-512'])->clientConfig('c');
        $this->assertSame([
            'bootstrap.servers' => 'localhost:9092',
            'client.id' => 'c',
            'security.protocol' => 'SASL_SSL',
            'ssl.ca.location' => '/ca.pem',
            'ssl.certificate.location' => '/c.pem',
            'ssl.key.location' => '/k.pem',
            'sasl.mechanisms' => 'SCRAM-SHA-512',
            'sasl.username' => 'u',
            'sasl.password' => 'p',
        ], $config);

        $this->assertSame('SCRAM-SHA-256', $sasl(['saslMechanism' => 'scram_sha_256'])->saslMechanismName());
        $this->assertSame('PLAIN', $sasl(['saslMechanism' => ' PLAIN '])->saslMechanismName());
    }

    public function testOptionsFromEnv(): void
    {
        $o = KafkaOptions::fromEnv([
            'MILVAION_KAFKA_BOOTSTRAP_SERVERS' => 'k1:9092,k2:9092',
            'KAFKA_BOOTSTRAP_SERVERS' => 'ignored:9092',
            'MILVAION_KAFKA_TLS' => 'TRUE',
            'MILVAION_KAFKA_TLS_CA_FILE' => '/ca.pem',
            'MILVAION_KAFKA_TLS_CERT_FILE' => '/c.pem',
            'MILVAION_KAFKA_TLS_KEY_FILE' => '/k.pem',
            'MILVAION_KAFKA_SASL_MECHANISM' => 'scram-sha-256',
            'MILVAION_KAFKA_SASL_USERNAME' => 'user',
            'MILVAION_KAFKA_SASL_PASSWORD' => 'secret',
            'MILVAION_KAFKA_TOPIC_PREFIX' => 'prod.',
            'MILVAION_KAFKA_AUTO_CREATE_TOPICS' => 'false',
            'MILVAION_KAFKA_PARTITIONS' => '6',
            'MILVAION_KAFKA_REPLICATION_FACTOR' => '3',
            'MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION' => '2',
        ]);

        $this->assertSame(['k1:9092', 'k2:9092'], $o->brokers);
        $this->assertTrue($o->tls);
        $this->assertSame(['/ca.pem', '/c.pem', '/k.pem'], [$o->tlsCaFile, $o->tlsCertFile, $o->tlsKeyFile]);
        $this->assertSame(['scram-sha-256', 'user', 'secret'], [$o->saslMechanism, $o->saslUsername, $o->saslPassword]);
        $this->assertSame('prod.', $o->topicPrefix);
        $this->assertFalse($o->autoCreateTopics);
        $this->assertSame([6, 3, 2], [$o->numPartitions, $o->replicationFactor, $o->maxInFlightPerPartition]);
    }

    public function testOptionsFromEnvFallbacksAndOverrides(): void
    {
        $o = KafkaOptions::fromEnv(['KAFKA_BOOTSTRAP_SERVERS' => 'fallback:9092', 'MILVAION_KAFKA_NUM_PARTITIONS' => '4', 'MILVAION_KAFKA_TOPIC_PREFIX' => 'env.'], topicPrefix: 'code.');

        $this->assertSame(['fallback:9092'], $o->brokers);
        $this->assertSame(4, $o->numPartitions);
        $this->assertSame('code.', $o->topicPrefix);
        $this->assertTrue($o->autoCreateTopics);
        $this->assertNull($o->maxInFlightPerPartition);

        $this->expectExceptionMessage('MILVAION_KAFKA_PARTITIONS must be an integer');
        KafkaOptions::fromEnv(['MILVAION_KAFKA_PARTITIONS' => 'many']);
    }

    public function testWorkerOptionsTransport(): void
    {
        $this->assertSame('rabbitmq', (new WorkerOptions('w'))->transport);
        $this->assertFalse((new WorkerOptions('w'))->usesKafka());
        $this->assertTrue((new WorkerOptions('w', transport: 'Kafka'))->usesKafka());

        $o = WorkerOptions::fromEnv(['MILVAION_WORKER_ID' => 'w', 'MILVAION_TRANSPORT' => 'kafka', 'MILVAION_KAFKA_BOOTSTRAP_SERVERS' => 'k:9092', 'MILVAION_KAFKA_TOPIC_PREFIX' => 'p.']);
        $this->assertTrue($o->usesKafka());
        $this->assertSame(['k:9092'], $o->kafka->brokers);
        $this->assertSame('p.', $o->kafka->topicPrefix);

        // The Kafka variables are not read (nor validated) on RabbitMQ.
        $this->assertFalse(WorkerOptions::fromEnv(['MILVAION_WORKER_ID' => 'w', 'MILVAION_KAFKA_TOPIC_PREFIX' => 'bad/'])->usesKafka());

        $this->expectExceptionMessage('transport must be "rabbitmq" or "kafka"');
        new WorkerOptions('w', transport: 'nats');
    }

    public function testKafkaWorkerNeedsTheExtension(): void
    {
        if (extension_loaded('rdkafka')) {
            $this->assertInstanceOf(Worker::class, new Worker(new WorkerOptions('w', transport: 'kafka')));

            return;
        }

        $this->expectException(\RuntimeException::class);
        $this->expectExceptionMessage('ext-rdkafka');

        new Worker(new WorkerOptions('w', transport: 'kafka'));
    }

    public function testHeadersAreDecimalStringsInStableOrder(): void
    {
        $this->assertSame(
            ['CorrelationId' => 'c', 'MaxRetries' => '3', 'OccurrenceId' => 'o', 'flag' => 'true', 'x-retry-count' => '2'],
            Headers::encode(['x-retry-count' => 2, 'OccurrenceId' => 'o', 'MaxRetries' => 3, 'CorrelationId' => 'c', 'flag' => true]),
        );
        $this->assertSame('', Headers::value(null));
        $this->assertSame('1700000000000', Headers::value(1.7e12));
    }

    public function testNotBefore(): void
    {
        $this->assertSame(1700000000.5, Headers::notBefore(['x-not-before' => '1700000000500']));
        $this->assertNull(Headers::notBefore([]));
        $this->assertNull(Headers::notBefore(['x-not-before' => 'soon']));
        $this->assertSame('1700000000500', Headers::millis(1700000000.5004));
    }

    public function testOffsetTrackerCommitsTheContiguousFinishedRange(): void
    {
        $t = new OffsetTracker();

        $this->assertNull($t->commitPosition());
        $this->assertTrue($t->add(10));
        $this->assertTrue($t->add(11));
        $this->assertTrue($t->add(13));
        $this->assertFalse($t->add(11), 'repeated offsets are ignored');
        $this->assertFalse($t->add(5), 'older offsets are ignored');
        $this->assertSame(3, $t->outstanding());

        $this->assertTrue($t->done(11));
        $this->assertNull($t->commitPosition(), '10 still runs');

        $this->assertTrue($t->done(10));
        $this->assertSame(12, $t->commitPosition());
        $this->assertSame(1, $t->outstanding());

        $this->assertFalse($t->done(12), 'not taken');
        $this->assertTrue($t->done(13));
        $this->assertSame(14, $t->commitPosition());
        $this->assertSame(0, $t->outstanding());
        $this->assertFalse($t->done(13));
    }

    public function testPublisherMapsDestinationsToTopicsAndKeys(): void
    {
        $publisher = new KafkaPublisher(new Topics('p.', 'W'), 'w-1234');

        $status = Protocol::json(['occurrenceId' => self::OCCURRENCE_ID, 'status' => 1]);
        $this->assertSame(['p.milvaion.status', self::OCCURRENCE_ID, []], $publisher->record('', Protocol::QUEUE_STATUS_UPDATES, $status, null, null));

        $logs = Protocol::json(['logs' => [['occurrenceId' => 'first'], ['occurrenceId' => 'second']]]);
        $this->assertSame(['p.milvaion.logs', 'first', []], $publisher->record('', Protocol::QUEUE_WORKER_LOGS, $logs, null, null));

        $this->assertSame(['p.milvaion.heartbeats', 'w-1234', []], $publisher->record('', Protocol::QUEUE_WORKER_HEARTBEAT, '{}', null, null));
        $this->assertSame(['p.milvaion.registrations', 'w-1234', []], $publisher->record('', Protocol::QUEUE_WORKER_REGISTRATION, '{}', null, null));

        $this->assertSame(
            ['p.milvaion.dlq', self::OCCURRENCE_ID, ['CorrelationId' => self::OCCURRENCE_ID, 'MaxRetries' => '0', 'OccurrenceId' => self::OCCURRENCE_ID, 'x-retry-count' => '1']],
            $publisher->record(Protocol::EXCHANGE_DEAD_LETTER, Protocol::DEAD_LETTER_ROUTING_KEY, '{}', self::OCCURRENCE_ID,
                ['CorrelationId' => self::OCCURRENCE_ID, 'x-retry-count' => 1, 'MaxRetries' => 0]),
        );

        $this->expectException(\InvalidArgumentException::class);
        $publisher->record('jobs.topic', 'w.job.*', '{}', null, null);
    }

    public function testCreateTopicsWireFormat(): void
    {
        $request = Admin::encodeCreateTopicsRequest(['a', 'bc'], 12, -1, 30000);

        $this->assertSame(bin2hex(
            "\x00\x00\x00\x02"
            . "\x00\x01a" . "\x00\x00\x00\x0c" . "\xff\xff" . "\x00\x00\x00\x00" . "\x00\x00\x00\x00"
            . "\x00\x02bc" . "\x00\x00\x00\x0c" . "\xff\xff" . "\x00\x00\x00\x00" . "\x00\x00\x00\x00"
            . "\x00\x00\x75\x30" . "\x00"
        ), bin2hex($request));

        $response = "\x00\x00\x00\x00" . "\x00\x00\x00\x02"
            . "\x00\x01a" . "\x00\x00" . "\xff\xff"
            . "\x00\x02bc" . "\x00\x24" . "\x00\x05exist";
        $this->assertSame(['a' => [0, null], 'bc' => [Admin::ERROR_TOPIC_ALREADY_EXISTS, 'exist']], Admin::decodeCreateTopics($response));
    }

    public function testMetadataWireFormat(): void
    {
        $this->assertSame("\x00\x00\x00\x00\x00", Admin::encodeMetadataRequest());

        $response = "\x00\x00\x00\x00" . "\x00\x00\x00\x02"
            . "\x00\x00\x00\x01" . "\x00\x02k1" . "\x00\x00\x23\x84" . "\xff\xff"
            . "\x00\x00\x00\x02" . "\x00\x02k2" . "\x00\x00\x23\x84" . "\x00\x01r"
            . "\x00\x03abc" . "\x00\x00\x00\x02" . "\x00\x00\x00\x00";

        $this->assertSame([[1 => 'k1:9092', 2 => 'k2:9092'], 2], Admin::decodeMetadata($response));
    }

    public function testScramMatchesRfc7677(): void
    {
        $scram = new Scram('sha256', 'user', 'pencil', 'rOprNGfwEbeRWgbNEkqO');

        $this->assertSame('n,,n=user,r=rOprNGfwEbeRWgbNEkqO', $scram->clientFirst());
        $this->assertSame(
            'c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=',
            $scram->clientFinal('r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096'),
        );

        $scram->verifyServerFinal('v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=');

        $this->expectExceptionMessage('signature');
        $scram->verifyServerFinal('v=AAAA');
    }

    public function testScramRejectsAForeignNonce(): void
    {
        $this->expectException(\RuntimeException::class);

        (new Scram('sha256', 'user', 'pencil', 'abc'))->clientFinal('r=xyz,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096');
    }
}
