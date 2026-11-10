using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Runs periodic work on a single instance at a time.
/// </summary>
public static class NamedLockExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> if the named lock is free, renewing the lock until the work ends.
    /// The TTL can stay short because of the renewal, so a crashed instance only blocks the others for a moment.
    /// </summary>
    /// <returns>True if the work ran, false if another instance holds the lock or Redis is unavailable.</returns>
    public static async Task<bool> RunExclusiveAsync(this IRedisLockService locks,
                                                     string lockName,
                                                     string owner,
                                                     TimeSpan ttl,
                                                     Func<CancellationToken, Task> work,
                                                     IMilvaLogger logger,
                                                     CancellationToken cancellationToken)
    {
        if (!await locks.TryAcquireNamedLockAsync(lockName, owner, ttl, cancellationToken))
            return false;

        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var renewal = RenewAsync(locks, lockName, owner, ttl, logger, renewalCts.Token);

        try
        {
            await work(cancellationToken);

            return true;
        }
        finally
        {
            await renewalCts.CancelAsync();
            await renewal;
            await locks.ReleaseNamedLockAsync(lockName, owner, CancellationToken.None);
        }
    }

    private static async Task RenewAsync(IRedisLockService locks, string lockName, string owner, TimeSpan ttl, IMilvaLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ttl / 3, cancellationToken);

                if (!await locks.ExtendNamedLockAsync(lockName, owner, ttl, cancellationToken))
                    logger.Warning("Lost named lock '{LockName}' while still working. Another instance may start the same work.", lockName);
            }
        }
        catch (OperationCanceledException)
        {
            // The work has ended.
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Renewal of named lock '{LockName}' stopped", lockName);
        }
    }
}
