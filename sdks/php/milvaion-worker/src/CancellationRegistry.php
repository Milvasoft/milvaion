<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/** Abort handles of the jobs running in this process, keyed by occurrence id. @internal */
final class CancellationRegistry
{
    /** @var array<string, Abort> */
    private array $running = [];

    public function register(string $occurrenceId, Abort $abort): void
    {
        $this->running[strtolower($occurrenceId)] = $abort;
    }

    public function unregister(string $occurrenceId): void
    {
        unset($this->running[strtolower($occurrenceId)]);
    }

    /** Returns false when the job is not running in this process. */
    public function cancel(string $occurrenceId, ?string $reason = null): bool
    {
        $abort = $this->running[strtolower($occurrenceId)] ?? null;

        if ($abort === null) {
            return false;
        }

        $abort->trigger(AbortReason::Cancelled, $reason ? "Job was cancelled: {$reason}" : 'Job was cancelled.');

        return true;
    }

    public function abortAll(AbortReason $reason, string $message): void
    {
        foreach ($this->running as $abort) {
            $abort->trigger($reason, $message);
        }
    }
}
