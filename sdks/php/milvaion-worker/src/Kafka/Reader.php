<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/** Reads big-endian Kafka protocol fields. @internal */
final class Reader
{
    private int $offset = 0;

    public function __construct(private readonly string $data)
    {
    }

    public function int16(): int
    {
        $value = unpack('n', $this->take(2))[1];

        return $value >= 0x8000 ? $value - 0x10000 : $value;
    }

    public function int32(): int
    {
        $value = unpack('N', $this->take(4))[1];

        return $value >= 0x80000000 ? $value - 0x100000000 : $value;
    }

    public function nullableString(): ?string
    {
        $length = $this->int16();

        return $length < 0 ? null : $this->take($length);
    }

    public function bytes(): string
    {
        $length = $this->int32();

        return $length < 0 ? '' : $this->take($length);
    }

    public function rest(): string
    {
        return $this->take(strlen($this->data) - $this->offset);
    }

    private function take(int $length): string
    {
        if ($this->offset + $length > strlen($this->data)) {
            throw new \RuntimeException('Truncated Kafka response.');
        }

        $chunk = substr($this->data, $this->offset, $length);
        $this->offset += $length;

        return $chunk;
    }
}
