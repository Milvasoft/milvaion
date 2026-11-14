<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

use Milvasoft\Milvaion\Worker\KafkaOptions;

/**
 * Creates topics with the Kafka protocol's CreateTopics request. php-rdkafka has no admin API, so
 * this speaks the few requests it needs itself (Metadata v4, CreateTopics v4, SaslHandshake v1,
 * SaslAuthenticate v0) over TCP or TLS, with SASL PLAIN or SCRAM like the clients.
 *
 * @internal
 */
final class Admin
{
    private const API_METADATA = 3;
    private const API_SASL_HANDSHAKE = 17;
    private const API_CREATE_TOPICS = 19;
    private const API_SASL_AUTHENTICATE = 36;

    public const ERROR_TOPIC_ALREADY_EXISTS = 36;
    public const ERROR_NOT_CONTROLLER = 41;

    /** @var resource|null */
    private $socket = null;
    private int $correlationId = 0;

    public function __construct(
        private readonly KafkaOptions $options,
        private readonly string $clientId,
        private readonly float $timeout = 30,
    ) {
    }

    /**
     * Creates the topics that do not exist yet. A topic that exists already is not an error.
     *
     * @param list<string> $topics
     */
    public function createTopics(array $topics, int $partitions, int $replicationFactor): void
    {
        $lastError = null;

        foreach ($this->options->brokers as $bootstrap) {
            try {
                $this->open($bootstrap);
                [$brokers, $controller] = self::decodeMetadata($this->request(self::API_METADATA, 4, self::encodeMetadataRequest()));

                // Older clusters only accept CreateTopics on the controller; newer ones forward it.
                if (isset($brokers[$controller]) && $brokers[$controller] !== $bootstrap) {
                    $this->close();
                    $this->open($brokers[$controller]);
                }

                $results = self::decodeCreateTopics($this->request(self::API_CREATE_TOPICS, 4,
                    self::encodeCreateTopicsRequest($topics, $partitions, $replicationFactor, (int) ($this->timeout * 1000))));

                foreach ($results as $topic => [$code, $message]) {
                    if ($code !== 0 && $code !== self::ERROR_TOPIC_ALREADY_EXISTS) {
                        throw new \RuntimeException("Could not create Kafka topic {$topic}: error {$code}" . ($message !== null && $message !== '' ? " ({$message})" : ''));
                    }
                }

                return;
            } catch (\RuntimeException $e) {
                $lastError = $e;
            } finally {
                $this->close();
            }
        }

        throw new \RuntimeException('Could not create Kafka topics: ' . $lastError?->getMessage(), 0, $lastError);
    }

    private function open(string $broker): void
    {
        $tls = $this->options->tlsEnabled();
        [$host] = self::splitHostPort($broker);
        $context = stream_context_create(['ssl' => array_filter([
            'verify_peer' => true,
            'verify_peer_name' => true,
            'peer_name' => $host,
            'cafile' => $this->options->tlsCaFile ?: null,
            'local_cert' => $this->options->tlsCertFile ?: null,
            'local_pk' => $this->options->tlsKeyFile ?: null,
        ], static fn ($v) => $v !== null)]);

        $socket = @stream_socket_client(($tls ? 'tls://' : 'tcp://') . $broker, $errno, $error, $this->timeout, STREAM_CLIENT_CONNECT, $context);

        if ($socket === false) {
            throw new \RuntimeException("Could not connect to Kafka broker {$broker}: {$error}");
        }

        stream_set_timeout($socket, (int) ceil($this->timeout));
        $this->socket = $socket;

        if (($mechanism = $this->options->saslMechanismName()) !== null) {
            $this->authenticate($mechanism);
        }
    }

    private function close(): void
    {
        if ($this->socket !== null) {
            @fclose($this->socket);
            $this->socket = null;
        }
    }

    private function authenticate(string $mechanism): void
    {
        $handshake = new Reader($this->request(self::API_SASL_HANDSHAKE, 1, self::string($mechanism)));

        if (($code = $handshake->int16()) !== 0) {
            throw new \RuntimeException("The Kafka broker does not accept SASL mechanism {$mechanism} (error {$code}).");
        }

        $username = (string) $this->options->saslUsername;
        $password = (string) $this->options->saslPassword;

        if ($mechanism === 'PLAIN') {
            $this->saslAuthenticate("\0{$username}\0{$password}");

            return;
        }

        $scram = new Scram($mechanism === 'SCRAM-SHA-512' ? 'sha512' : 'sha256', $username, $password);
        $serverFirst = $this->saslAuthenticate($scram->clientFirst());
        $serverFinal = $this->saslAuthenticate($scram->clientFinal($serverFirst));
        $scram->verifyServerFinal($serverFinal);
    }

    private function saslAuthenticate(string $bytes): string
    {
        $response = new Reader($this->request(self::API_SASL_AUTHENTICATE, 0, self::bytes($bytes)));
        $code = $response->int16();
        $message = $response->nullableString();
        $data = $response->bytes();

        if ($code !== 0) {
            throw new \RuntimeException("Kafka SASL authentication failed (error {$code}): {$message}");
        }

        return $data;
    }

    /** Sends one request and returns the response body after its correlation id. */
    private function request(int $apiKey, int $version, string $body): string
    {
        $correlationId = ++$this->correlationId;
        $payload = self::int16($apiKey) . self::int16($version) . self::int32($correlationId) . self::string($this->clientId) . $body;
        $this->write(self::int32(strlen($payload)) . $payload);

        $size = (new Reader($this->read(4)))->int32();
        $response = new Reader($this->read($size));

        if ($response->int32() !== $correlationId) {
            throw new \RuntimeException('Unexpected Kafka response.');
        }

        return $response->rest();
    }

    private function write(string $data): void
    {
        for ($written = 0; $written < strlen($data);) {
            $result = @fwrite($this->socket, substr($data, $written));

            if ($result === false || $result === 0) {
                throw new \RuntimeException('Could not write to the Kafka broker.');
            }

            $written += $result;
        }
    }

    private function read(int $length): string
    {
        $data = '';

        while (strlen($data) < $length) {
            $chunk = @fread($this->socket, $length - strlen($data));

            if ($chunk === false || $chunk === '') {
                $meta = stream_get_meta_data($this->socket);

                throw new \RuntimeException($meta['timed_out'] ? 'Timed out waiting for the Kafka broker.' : 'The Kafka broker closed the connection.');
            }

            $data .= $chunk;
        }

        return $data;
    }

    /** Metadata v4 for no topics: just the brokers and the controller. */
    public static function encodeMetadataRequest(): string
    {
        return self::int32(0) . "\0";
    }

    /** @return array{0: array<int, string>, 1: int} brokers by node id ("host:port"), controller id */
    public static function decodeMetadata(string $body): array
    {
        $reader = new Reader($body);
        $reader->int32(); // throttle_time_ms
        $brokers = [];

        for ($count = $reader->int32(); $count > 0; $count--) {
            $node = $reader->int32();
            $host = (string) $reader->nullableString();
            $port = $reader->int32();
            $reader->nullableString(); // rack
            $brokers[$node] = str_contains($host, ':') ? "[{$host}]:{$port}" : "{$host}:{$port}";
        }

        $reader->nullableString(); // cluster_id

        return [$brokers, $reader->int32()];
    }

    /** @param list<string> $topics */
    public static function encodeCreateTopicsRequest(array $topics, int $partitions, int $replicationFactor, int $timeoutMs): string
    {
        $body = self::int32(count($topics));

        foreach ($topics as $topic) {
            // No manual assignments, no configs.
            $body .= self::string($topic) . self::int32($partitions) . self::int16($replicationFactor) . self::int32(0) . self::int32(0);
        }

        return $body . self::int32($timeoutMs) . "\0";
    }

    /** @return array<string, array{0: int, 1: ?string}> error code and message by topic */
    public static function decodeCreateTopics(string $body): array
    {
        $reader = new Reader($body);
        $reader->int32(); // throttle_time_ms
        $results = [];

        for ($count = $reader->int32(); $count > 0; $count--) {
            $topic = (string) $reader->nullableString();
            $results[$topic] = [$reader->int16(), $reader->nullableString()];
        }

        return $results;
    }

    /** @return array{0: string, 1: int} */
    private static function splitHostPort(string $broker): array
    {
        $colon = strrpos($broker, ':');

        return $colon === false ? [$broker, 9092] : [trim(substr($broker, 0, $colon), '[]'), (int) substr($broker, $colon + 1)];
    }

    public static function int16(int $value): string
    {
        return pack('n', $value & 0xFFFF);
    }

    public static function int32(int $value): string
    {
        return pack('N', $value & 0xFFFFFFFF);
    }

    public static function string(string $value): string
    {
        return self::int16(strlen($value)) . $value;
    }

    public static function bytes(string $value): string
    {
        return self::int32(strlen($value)) . $value;
    }
}
