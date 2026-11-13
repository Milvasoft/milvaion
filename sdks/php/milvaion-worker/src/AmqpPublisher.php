<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

use PhpAmqpLib\Channel\AMQPChannel;
use PhpAmqpLib\Message\AMQPMessage;
use PhpAmqpLib\Wire\AMQPTable;

/** Publishes on a confirm channel and waits for the broker to accept each message. @internal */
final class AmqpPublisher implements Publisher
{
    private ?AMQPChannel $channel = null;
    private bool $nacked = false;

    public function attach(AMQPChannel $channel): void
    {
        $channel->set_nack_handler(function (): void {
            $this->nacked = true;
        });

        $this->channel = $channel;
    }

    public function detach(): void
    {
        $this->channel = null;
    }

    public function publish(string $exchange, string $routingKey, string $body, bool $persistent = true, ?string $correlationId = null, ?array $headers = null): void
    {
        if ($this->channel === null) {
            throw new \RuntimeException('Not connected to RabbitMQ.');
        }

        $properties = [
            'content_type' => 'application/json',
            'delivery_mode' => $persistent ? AMQPMessage::DELIVERY_MODE_PERSISTENT : AMQPMessage::DELIVERY_MODE_NON_PERSISTENT,
        ];

        if ($correlationId !== null) {
            $properties['correlation_id'] = $correlationId;
        }

        if ($headers !== null) {
            $table = new AMQPTable();

            foreach ($headers as $key => $value) {
                // Integers as 32-bit ints, the type the .NET side expects for retry counts.
                is_int($value) ? $table->set($key, $value, AMQPTable::T_INT_LONG) : $table->set($key, (string) $value, AMQPTable::T_STRING_LONG);
            }

            $properties['application_headers'] = $table;
        }

        $this->nacked = false;
        $this->channel->basic_publish(new AMQPMessage($body, $properties), $exchange, $routingKey);
        $this->channel->wait_for_pending_acks(30);

        if ($this->nacked) {
            throw new \RuntimeException('RabbitMQ rejected the message.');
        }
    }
}
