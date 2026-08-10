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
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
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
        var connectionString = $"{_factory.GetConnectionString()};Pooling=false;";

        // Host is already built in the fixture's InitializeAsync (with correct env vars).
        // ConfigureWebHost adds in-memory config as an additional override for late-bound reads.
        if (configureServices != null || configureApp != null)
        {
            var waf = _factory.WithWebHostBuilder(builder =>
            {
                if (configureServices != null)
                    builder.ConfigureTestServices(services => configureServices(services));

                if (configureApp != null)
                    builder.Configure(app => configureApp(app));
            });

            _serviceProvider = waf.Services.CreateScope().ServiceProvider;
        }
        else
        {
            _serviceProvider = _factory.Services.CreateScope().ServiceProvider;
        }

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
        await SetupTestJobQueueAsync();
    }

    /// <summary>
    /// Declares a catch-all queue bound to the jobs topic exchange so that mandatory publishes
    /// in tests always route successfully, even though no real worker is running. Without this,
    /// <c>RabbitMQPublisher</c> would treat every publish as unroutable and return <c>false</c>.
    /// </summary>
    private async Task SetupTestJobQueueAsync()
    {
        try
        {
            var rabbitFactory = new ConnectionFactory
            {
                HostName = _factory.GetRabbitMqHost(),
                Port = _factory.GetRabbitMqPort(),
                UserName = "guest",
                Password = "guest"
            };

            await using var connection = await rabbitFactory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            await channel.ExchangeDeclareAsync(
                exchange: WorkerConstant.ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false);

            await channel.QueueDeclareAsync(
                queue: "test-job-dispatch-queue",
                durable: false,
                exclusive: false,
                autoDelete: false);

            await channel.QueueBindAsync(
                queue: "test-job-dispatch-queue",
                exchange: WorkerConstant.ExchangeName,
                routingKey: "#");
        }
        catch
        {
            // Non-critical: tests that don't publish jobs still pass even if this setup fails.
        }
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