<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** @internal */
final class ResolvedJob
{
    public function __construct(
        public readonly string $name,
        public readonly \Closure $handler,
        public readonly string $routingPattern,
        public readonly string $queue,
        public readonly string $consumerId,
        public readonly float $executionTimeout,
        public readonly int $maxRetries,
        public readonly float $baseRetryDelay,
        public readonly ?string $dataSchema,
        public readonly ?string $resultSchema,
    ) {
    }
}
