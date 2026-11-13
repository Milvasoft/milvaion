<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use PhpAmqpLib\Connection\AMQPStreamConnection;

/** @internal */
final class AmqpConnector
{
    /**
     * Opens a connection from an amqp:// URL.
     *
     * AMQP heartbeats are off: a PHP process cannot answer them while a job's code runs, and the
     * broker would drop the connection under a long job. TCP keepalive detects dead peers instead.
     */
    public static function open(string $url): AMQPStreamConnection
    {
        $parts = parse_url($url);

        if ($parts === false || !isset($parts['host'])) {
            throw new \InvalidArgumentException('Invalid RabbitMQ URL.');
        }

        $vhost = isset($parts['path']) && $parts['path'] !== '' && $parts['path'] !== '/' ? rawurldecode(substr($parts['path'], 1)) : '/';

        return new AMQPStreamConnection(
            $parts['host'],
            $parts['port'] ?? 5672,
            rawurldecode($parts['user'] ?? 'guest'),
            rawurldecode($parts['pass'] ?? 'guest'),
            $vhost,
            false,
            'AMQPLAIN',
            null,
            'en_US',
            5.0,
            30.0,
            null,
            true,
            0,
        );
    }
}
