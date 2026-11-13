<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Publishing side of the broker, with publisher confirms. @internal */
interface Publisher
{
    /** @param array<string, string|int>|null $headers Integer values are sent as 32-bit ints. */
    public function publish(string $exchange, string $routingKey, string $body, bool $persistent = true, ?string $correlationId = null, ?array $headers = null): void;
}
