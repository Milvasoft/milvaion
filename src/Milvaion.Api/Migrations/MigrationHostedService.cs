using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using Milvasoft.Core.MultiLanguage.Manager;
using Serilog;

namespace Milvaion.Api.Migrations;

/// <summary>
/// Applies migrations when the application starts.
/// </summary>
/// <param name="scopeFactory"></param>
public class MigrationHostedService(IServiceScopeFactory scopeFactory) : IHostedService
{
    readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// Starts hosted service.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        try
        {
            var languages = await context.Languages.ToListAsync(cancellationToken);

            var languageSeed = languages.Cast<ILanguage>().ToList();

            MultiLanguageManager.UpdateLanguagesList(languageSeed);

            var permissionManager = scope.ServiceProvider.GetRequiredService<IPermissionManager>();

            var permissions = await permissionManager.GetAllPermissionsAsync(cancellationToken);

            foreach (var permission in permissions)
                PermissionCatalog.Permissions.Add(permission);

        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "An error occurred while initializing language list.");
        }

        try
        {
            Log.Logger.Information("Applying migrations..");

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);

            if (!pendingMigrations.IsNullOrEmpty())
                await context.Database.MigrateAsync(cancellationToken);

            var migrator = new DatabaseMigrator(scope.ServiceProvider);

            var rootPassword = Environment.GetEnvironmentVariable("MILVAION_ROOT_PASSWORD");

            var permissionManager = scope.ServiceProvider.GetRequiredService<IPermissionManager>();

            var isTestEnv = Environment.GetEnvironmentVariable("IsTestEnv");

            if (string.IsNullOrWhiteSpace(isTestEnv) || isTestEnv == "false")
                await migrator.InitDatabaseAsync(permissionManager, rootPassword, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "An error occurred while applying migrations.");
        }
    }

    /// <summary>
    /// Stops hosted service.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}