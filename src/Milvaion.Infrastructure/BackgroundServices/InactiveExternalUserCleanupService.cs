using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvaion.Infrastructure.Persistence.Context;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Prunes externally provisioned users who have not signed in for the configured retention period.
/// Only external (OIDC/LDAP) shadow users are considered; local accounts are never touched. Access is
/// already revoked at the provider the moment a user is disabled there, so this is housekeeping rather
/// than a security control: it keeps the local user list from accumulating stale shadow records.
///
/// Silent and resilient like the other monitors: a failure is swallowed at debug level and the next
/// pass retries. Disabled when the retention is 0.
/// </summary>
public class InactiveExternalUserCleanupService(IServiceScopeFactory scopeFactory,
                                                MilvaionConfig config,
                                                ILogger<InactiveExternalUserCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly int _retentionDays = config?.Authentication?.InactiveUserRetentionDays ?? 0;
    private readonly ILogger<InactiveExternalUserCleanupService> _logger = logger;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionDays <= 0)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Inactive external user cleanup failed.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Hard delete so a later sign-in re-provisions a fresh record. Related rows (role links, sessions) go with it through the database's cascade.
        await dbContext.Users.Where(u => u.Provider != ExternalProvider.Local && u.LastLoginDate != null && u.LastLoginDate < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
