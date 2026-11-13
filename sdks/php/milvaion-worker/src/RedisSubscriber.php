<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use Psr\Log\LoggerInterface;

/**
 * A minimal Redis pub/sub subscriber that can be polled without blocking, so a single-threaded
 * worker can check for cancellations while it runs a job. Needs no Redis extension.
 *
 * @internal
 */
final class RedisSubscriber
{
    /** @var resource|null */
    private $socket = null;
    private string $buffer = '';
    private float $nextReconnect = 0;

    public function __construct(
        private readonly string $url,
        private readonly string $channel,
        private readonly LoggerInterface $logger,
    ) {
    }

    /** Connects and subscribes; throws if that does not happen within the timeout. */
    public function connect(float $timeout): void
    {
        $parts = parse_url($this->url);

        if ($parts === false || !isset($parts['host'])) {
            throw new \InvalidArgumentException('Invalid Redis URL.');
        }

        $scheme = ($parts['scheme'] ?? 'redis') === 'rediss' ? 'tls' : 'tcp';
        $address = sprintf('%s://%s:%d', $scheme, $parts['host'], $parts['port'] ?? 6379);
        $socket = @stream_socket_client($address, $errno, $error, $timeout);

        if ($socket === false) {
            throw new \RuntimeException("Could not connect to Redis: {$error}");
        }

        stream_set_timeout($socket, (int) ceil($timeout));
        $this->socket = $socket;
        $this->buffer = '';

        $password = isset($parts['pass']) ? rawurldecode($parts['pass']) : null;
        $user = isset($parts['user']) && $parts['user'] !== '' ? rawurldecode($parts['user']) : null;

        if ($password !== null && $password !== '') {
            $this->send($user !== null ? ['AUTH', $user, $password] : ['AUTH', $password]);
            $reply = $this->readBlocking($timeout);

            if (!is_string($reply) || $reply !== 'OK') {
                throw new \RuntimeException('Redis authentication failed.');
            }
        }

        $this->send(['SUBSCRIBE', $this->channel]);
        $reply = $this->readBlocking($timeout);

        if (!is_array($reply) || strtolower((string) ($reply[0] ?? '')) !== 'subscribe') {
            throw new \RuntimeException("Could not subscribe to {$this->channel}.");
        }

        stream_set_blocking($socket, false);
    }

    /**
     * Returns the messages published since the last call, without blocking. Reconnects after a
     * dropped connection, at most every few seconds.
     *
     * @return list<string>
     */
    public function poll(): array
    {
        if ($this->socket === null) {
            if (microtime(true) < $this->nextReconnect) {
                return [];
            }

            try {
                $this->connect(2);
                $this->logger->info("Reconnected to Redis; listening on {$this->channel}.");
            } catch (\Throwable $e) {
                $this->nextReconnect = microtime(true) + 5;
                $this->socket = null;

                return [];
            }
        }

        $chunk = @fread($this->socket, 65536);

        if ($chunk === false || ($chunk === '' && feof($this->socket))) {
            $this->logger->warning('Lost the Redis connection; cancellations are paused until it is back.');
            $this->close();
            $this->nextReconnect = microtime(true) + 2;

            return [];
        }

        $this->buffer .= $chunk;
        $messages = [];

        while (($reply = $this->parse()) !== null) {
            if (is_array($reply) && ($reply[0] ?? null) === 'message' && isset($reply[2])) {
                $messages[] = (string) $reply[2];
            }
        }

        return $messages;
    }

    public function close(): void
    {
        if ($this->socket !== null) {
            @fclose($this->socket);
            $this->socket = null;
        }
    }

    /** @param list<string> $args */
    private function send(array $args): void
    {
        $command = '*' . count($args) . "\r\n";

        foreach ($args as $arg) {
            $command .= '$' . strlen($arg) . "\r\n" . $arg . "\r\n";
        }

        fwrite($this->socket, $command);
    }

    private function readBlocking(float $timeout): mixed
    {
        $deadline = microtime(true) + $timeout;

        while (microtime(true) < $deadline) {
            $reply = $this->parse();

            if ($reply !== null) {
                return $reply;
            }

            $chunk = fread($this->socket, 65536);

            if ($chunk === false || ($chunk === '' && feof($this->socket))) {
                throw new \RuntimeException('Redis closed the connection.');
            }

            $this->buffer .= $chunk;
        }

        throw new \RuntimeException('Timed out waiting for Redis.');
    }

    /** Parses one RESP value off the buffer, or returns null if it is incomplete. */
    private function parse(): mixed
    {
        $offset = 0;
        $value = $this->parseAt($offset);

        if ($value === null) {
            return null;
        }

        $this->buffer = substr($this->buffer, $offset);

        return $value[0];
    }

    /** @return array{0: mixed}|null */
    private function parseAt(int &$offset): ?array
    {
        $end = strpos($this->buffer, "\r\n", $offset);

        if ($end === false) {
            return null;
        }

        $type = $this->buffer[$offset];
        $line = substr($this->buffer, $offset + 1, $end - $offset - 1);
        $next = $end + 2;

        switch ($type) {
            case '+':
            case '-':
                $offset = $next;

                return [$line];
            case ':':
                $offset = $next;

                return [(int) $line];
            case '$':
                $length = (int) $line;

                if ($length < 0) {
                    $offset = $next;

                    return [null];
                }

                if (strlen($this->buffer) < $next + $length + 2) {
                    return null;
                }

                $offset = $next + $length + 2;

                return [substr($this->buffer, $next, $length)];
            case '*':
                $count = (int) $line;
                $items = [];
                $cursor = $next;

                for ($i = 0; $i < $count; $i++) {
                    $item = $this->parseAt($cursor);

                    if ($item === null) {
                        return null;
                    }

                    $items[] = $item[0];
                }

                $offset = $cursor;

                return [$items];
            default:
                // Unknown type: drop the line so the stream does not stall.
                $offset = $next;

                return [null];
        }
    }
}
