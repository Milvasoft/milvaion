using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Helpers;
using Milvasoft.Identity.Abstract;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.TestBase;

public abstract class IntegrationTestBase(CustomWebApplicationFactory factory, ITestOutputHelper output) : IAsyncLifetime
{
    protected readonly CustomWebApplicationFactory _factory = factory;
    protected IServiceProvider _serviceProvider;
    protected IServiceScope _serviceScope;
    protected ITestOutputHelper _output = output;

    public virtual Task InitializeAsync() => InitializeAsync(null, null);

    public virtual async Task InitializeAsync(Action<IServiceCollection> configureServices = null, Action<IApplicationBuilder> configureApp = null)
    {
        Environment.SetEnvironmentVariable("IsTestEnv", "true");

        var connectionString = $"{_factory.GetConnectionString()};Pooling=false;";

        // Set PostgreSQL connection string
        Environment.SetEnvironmentVariable("ConnectionStrings:DefaultConnectionString", connectionString);

        // Set Redis connection string
        Environment.SetEnvironmentVariable("MilvaionConfig:Redis:ConnectionString", _factory.GetRedisConnectionString());

        // Set RabbitMQ connection settings
        Environment.SetEnvironmentVariable("MilvaionConfig:RabbitMQ:Host", _factory.GetRabbitMqHost());
        Environment.SetEnvironmentVariable("MilvaionConfig:RabbitMQ:Port", _factory.GetRabbitMqPort().ToString());
        Environment.SetEnvironmentVariable("MilvaionConfig:RabbitMQ:Username", "guest");
        Environment.SetEnvironmentVariable("MilvaionConfig:RabbitMQ:Password", "guest");

        // Disable background services that might interfere with tests
        Environment.SetEnvironmentVariable("MilvaionConfig:StatusTracker:Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig:WorkerAutoDiscovery:Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig:ZombieOccurrenceDetector:Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig:FailedOccurrenceHandler:Enabled", "false");
        Environment.SetEnvironmentVariable("MilvaionConfig:LogCollector:Enabled", "false");

        var waf = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Internal cron jobs have been moved to MilvaionMaintenanceWorker
                // No need to remove them from hosted services anymore

                configureServices?.Invoke(services);
            });

            builder.Configure(app =>
            {
                configureApp?.Invoke(app);
            });
        });

        _serviceProvider = waf.Services.CreateScope().ServiceProvider;

        try
        {
            var opt = new DbContextOptionsBuilder<MilvaionDbContext>()
                  .UseNpgsql(connectionString, b => b.MigrationsHistoryTable(TableNames.EfMigrationHistory).MigrationsAssembly("Milvaion.Api").EnableRetryOnFailure())
                  .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

            using var context = new MilvaionDbContext(opt.Options);

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

            if (!pendingMigrations.IsNullOrEmpty())
                await context.Database.MigrateAsync();
        }
        catch (Exception)
        {
            // Handle exception
        }

        var dbContext = _serviceProvider.GetService<MilvaionDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync(_resetAutoIncrementQuery);

        await _factory.CreateRespawner();
    }
    private const string _resetAutoIncrementQuery = @"
        DO $$
        DECLARE
            seq RECORD;
        BEGIN
            FOR seq IN
                SELECT sequencename, schemaname
                FROM pg_sequences
                WHERE schemaname = 'public'
            LOOP
                EXECUTE format('ALTER SEQUENCE %I.%I RESTART WITH 5000', seq.schemaname, seq.sequencename);
            END LOOP;
        END
        $$;
    ";
    public virtual Task DisposeAsync() => _factory.ResetDatabase();

    public virtual MilvaionDbContext GetDbContext()
    {
        var scope = _serviceProvider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        return dbContext;
    }

    public virtual async Task<User> SeedRootUserAndSuperAdminRoleAsync(string rootPassword = "defaultpass")
    {
        var dbContext = _serviceProvider.GetService<MilvaionDbContext>();

        var superAdminPermission = new Permission
        {
            Id = 1,
            Name = nameof(PermissionCatalog.App.SuperAdmin),
            Description = "Provides access to the entire system.",
            NormalizedName = nameof(PermissionCatalog.App.SuperAdmin).MilvaNormalize(),
            PermissionGroup = nameof(PermissionCatalog.App),
            PermissionGroupDescription = "Application-wide permissions."
        };

        await dbContext.Permissions.AddAsync(superAdminPermission);

        var superAdminRole = new Role
        {
            Name = nameof(PermissionCatalog.App.SuperAdmin),
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            RolePermissionRelations =
            [
                new()
                {
                    PermissionId = 1
                }
            ]
        };

        await dbContext.Roles.AddAsync(superAdminRole);

        await dbContext.SaveChangesAsync();

        var rootUser = new User
        {
            UserName = GlobalConstant.RootUsername,
            NormalizedUserName = "ROOTUSER",
            Email = "rootuser@milvasoft.com",
            NormalizedEmail = "ROOTUSER@MILVASOFT.COM",
            Name = "Administrator",
            Surname = "User",
            UserType = Domain.Enums.UserType.Manager,
            CreationDate = DateTime.Now,
            CreatorUserName = GlobalConstant.SystemUsername,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            TwoFactorEnabled = false,
            LockoutEnabled = false,
            AccessFailedCount = 0,
            RoleRelations =
            [
                new()
                {
                    RoleId = superAdminRole.Id
                }
            ]
        };

        dbContext.ServiceProvider.GetService<IMilvaUserManager<User, int>>().SetPasswordHash(rootUser, rootPassword);

        await dbContext.Users.AddAsync(rootUser);

        await dbContext.SaveChangesAsync();

        return rootUser;
    }
}