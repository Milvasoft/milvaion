<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** An exception raised in a job process. @internal */
final class RemoteJobException extends \RuntimeException implements RemoteFailure
{
    public function __construct(string $message, private readonly string $type, private readonly string $formatted, ?string $previous)
    {
        parent::__construct($message, 0, $previous === null ? null : new \RuntimeException($previous));
    }

    public function remoteType(): string
    {
        return $this->type;
    }

    public function formatted(): string
    {
        return $this->formatted;
    }

    /** @return array{permanent: bool, type: string, message: string, formatted: string, previous: ?string} */
    public static function toArray(\Throwable $error): array
    {
        return [
            'permanent' => $error instanceof PermanentJobException,
            'type' => $error::class,
            'message' => $error->getMessage(),
            'formatted' => Errors::format($error),
            'previous' => $error->getPrevious()?->getMessage(),
        ];
    }

    /** @param array<string, mixed> $data */
    public static function fromArray(array $data): \Throwable
    {
        $args = [(string) ($data['message'] ?? ''), (string) ($data['type'] ?? 'Exception'), (string) ($data['formatted'] ?? ''), $data['previous'] ?? null];

        return !empty($data['permanent']) ? new RemotePermanentJobException(...$args) : new self(...$args);
    }
}
