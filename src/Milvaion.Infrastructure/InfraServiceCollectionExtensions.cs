using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Aspects.UserActivityLogAspect;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.HealthChecks;
using Milvaion.Infrastructure.LazyImpl;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Persistence.Repository;
using Milvaion.Infrastructure.Services;
using Milvaion.Infrastructure.Services.Monitoring;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Services.Redis;
using Milvasoft.Caching.Builder;
using Milvasoft.Caching.Redis;
using Milvasoft.Caching.Redis.Accessor;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore;
using Milvasoft.Interception.Decorator;
using Milvasoft.Interception.Ef;
using Milvasoft.Milvaion.Sdk.Utils;
using Npgsql;
using StackExchange.Redis;

namespace Milvaion.Infrastructure;

/// <summary>
/// Infrastructure service collection extensions.
/// </summary>
public static class InfraServiceCollectionExtensions
{
    /// <summary>
    /// Adds infrastructure services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configurationManager"></param>
    /// <returns></returns>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfigurationManager configurationManager)
    {
        services.AddScoped<IMilvaLogger, MilvaionLogger>();
        services.AddScoped<IPermissionManager, PermissionManager>();
        services.AddScoped<IAccountManager, AccountManager>();
        services.AddScoped<ILookupService, LookupService>();
        services.AddScoped<IUIService, UIService>();
        services.AddScoped<IDeveloperService, DeveloperService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<INotificationService, NotificationService>();

        services.AddTransient(typeof(Lazy<>), typeof(MilvaionLazy<>));

        services.AddDataAccessServices(configurationManager);

        services.AddMilvaion(configurationManager);

        services.AddMilvaCaching(configurationManager)
                .WithRedisAccessor();

        services.AddMilvaInterception(ApplicationAssembly.Assembly, configurationManager)
                .WithLogInterceptor()
                .WithResponseInterceptor()
                .WithCacheInterceptor()
                .WithTransactionInterceptor()
                .WithActivityInterceptor()
                .WithInterceptor<UserActivityLogInterceptor>()
                .PostConfigureInterceptionOptions(opt =>
                {
                    opt.Response.GenerateMetadataFunc = MilvaionExtensions.GenerateMetadata;
                    opt.Cache.CacheAccessorType = typeof(IRedisAccessor);
                })
                .PostConfigureTransactionInterceptionOptions(opt =>
                {
                    opt.DbContextType = typeof(MilvaionDbContext);
                });

        return services;
    }

    /// <summary>
    /// Adds data access services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configurationManager"></param>
    /// <returns></returns>
    private static IServiceCollection AddDataAccessServices(this IServiceCollection services, IConfigurationManager configurationManager)
    {
        var connectionString = configurationManager.GetConnectionString("DefaultConnectionString");

        services.ConfigureMilvaDataAccess(configurationManager)
                .PostConfigureMilvaDataAccess(opt =>
                {
                    opt.GetCurrentUserNameMethod = User.GetCurrentUser;
                })
                .AddInjectedDbContext<MilvaionDbContext>();

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build();

        services.AddPooledDbContextFactory<MilvaionDbContext>((provider, options) =>
        {
            options.ConfigureWarnings(warnings => { warnings.Log(RelationalEventId.PendingModelChangesWarning); });

            options.UseNpgsql(dataSource, b => b.MigrationsHistoryTable(TableNames.EfMigrationHistory).MigrationsAssembly("Milvaion.Api").EnableRetryOnFailure())
                   .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
        });

        services.AddScoped<MilvaionDbContextScopedFactory>();
        services.AddScoped(sp => sp.GetRequiredService<MilvaionDbContextScopedFactory>().CreateDbContext());

        services.AddScoped(typeof(IMilvaionRepositoryBase<>), typeof(MilvaionRepositoryBase<>));
        services.AddScoped<IMilvaionDbContextAccessor, MilvaionDbContextAccessor>();

        return services;
    }

    /// <summary>
    /// Add whole milvaion components.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddMilvaion(this IServiceCollection services, IConfiguration configuration)
    {
        // Add health checks
        services.AddHealthChecks()
                .AddCheck<DbContextHealthCheck>("PostgreSQL", tags: ["database", "sql"])
                .AddCheck<RedisHealthCheck>("Redis", tags: ["redis", "cache"])
                .AddCheck<RabbitMQHealthCheck>("RabbitMQ", tags: ["rabbitmq", "messaging"]);

        services.AddOptions<MilvaionConfig>().Bind(configuration.GetSection(nameof(MilvaionConfig))).ValidateDataAnnotations();

        // Register job cancellation service
        services.AddSingleton<IJobCancellationService, JobCancellationService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddSingleton<IMemoryStatsRegistry, MemoryStatsRegistry>();

        services.AddRedisStorage(configuration)
                .AddRabbitMQ(configuration)
                .AddJobDispatcher(configuration)
                .AddZombieOccurrenceDetector(configuration)
                .AddFailedOccurrenceHandler(configuration)
                .AddWorkerAutoDiscovery(configuration)
                .AddLogCollector(configuration)
                .AddStatusTracker(configuration);

        return services;
    }

    /// <summary>
    /// Registers job dispatcher background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddJobDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure JobDispatcherOptions from appsettings
        services.AddOptions<JobDispatcherOptions>().Bind(configuration.GetSection(JobDispatcherOptions.SectionKey)).ValidateDataAnnotations();

        // Register Dispatcher Control Service as Singleton (runtime control)
        services.AddSingleton<IDispatcherControlService, DispatcherControlService>();

        // Register the job dispatcher background service
        services.AddHostedService<JobDispatcherService>();

        return services;
    }

    /// <summary>
    /// Registers zombie occurrence detector background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddZombieOccurrenceDetector(this IServiceCollection services, IConfiguration configuration)
    {
        // Add zombie occurrence detector service
        services.AddOptions<ZombieOccurrenceDetectorOptions>().Bind(configuration.GetSection(ZombieOccurrenceDetectorOptions.SectionKey)).ValidateDataAnnotations();

        services.AddHostedService<ZombieOccurrenceDetectorService>();

        return services;
    }

    /// <summary>
    /// Registers failed job handler background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddFailedOccurrenceHandler(this IServiceCollection services, IConfiguration configuration)
    {
        // Add failed job handler service
        services.AddOptions<FailedOccurrenceHandlerOptions>().Bind(configuration.GetSection(FailedOccurrenceHandlerOptions.SectionKey)).ValidateDataAnnotations();

        services.AddHostedService<FailedOccurrenceHandler>();

        return services;
    }

    /// <summary>
    /// Registers worker auto discovery background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddWorkerAutoDiscovery(this IServiceCollection services, IConfiguration configuration)
    {
        // Add worker auto discovery service
        services.AddOptions<WorkerAutoDiscoveryOptions>().Bind(configuration.GetSection(WorkerAutoDiscoveryOptions.SectionKey)).ValidateDataAnnotations();

        services.AddHostedService<WorkerAutoDiscoveryService>();

        return services;
    }

    /// <summary>
    /// Registers log collector background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddLogCollector(this IServiceCollection services, IConfiguration configuration)
    {
        // Add log collector service
        services.AddOptions<LogCollectorOptions>().Bind(configuration.GetSection(LogCollectorOptions.SectionKey)).ValidateDataAnnotations();

        services.AddHostedService<LogCollectorService>();

        return services;
    }

    /// <summary>
    /// Registers occurrence status tracker background service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddStatusTracker(this IServiceCollection services, IConfiguration configuration)
    {
        // Add status collector service
        services.AddOptions<StatusTrackerOptions>().Bind(configuration.GetSection(StatusTrackerOptions.SectionKey)).ValidateDataAnnotations();

        // Add job auto-disable (circuit breaker) options
        services.AddOptions<JobAutoDisableOptions>().Bind(configuration.GetSection(JobAutoDisableOptions.SectionKey)).ValidateDataAnnotations();

        services.AddHostedService<StatusTrackerService>();

        return services;
    }

    /// <summary>
    /// Registers Redis services for job scheduling.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddRedisStorage(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetSection(RedisOptions.SectionKey).Get<RedisOptions>() == null)
            throw new InvalidOperationException("Redis configuration section is missing or invalid.");

        // Configure RedisOptions from appsettings
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.SectionKey)).ValidateDataAnnotations();

        // Register StackExchange.Redis IConnectionMultiplexer
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = configuration.GetSection(RedisOptions.SectionKey).Get<RedisOptions>();
            var connectionString = string.IsNullOrEmpty(config.Password) ? config.ConnectionString : $"{config.ConnectionString},password={config.Password}";

            // Parse configuration options and make connection resilient to startup ordering
            var options = ConfigurationOptions.Parse(connectionString);

            options.AbortOnConnectFail = false; // allow multiplexer to keep retrying
            options.ConnectRetry = 3;
            options.KeepAlive = 60;

            return ConnectionMultiplexer.Connect(options);
        });

        // Register RedisConnectionService as singleton (manages connection pooling)
        services.AddSingleton<RedisConnectionService>();

        // Register Circuit Breaker for Redis resilience
        services.AddSingleton<IRedisCircuitBreaker, RedisCircuitBreaker>();

        // Register Redis services as singletons (stateless, thread-safe)
        // Register Redis-based worker tracking service
        services.AddSingleton<IRedisWorkerService, RedisWorkerService>();
        services.AddSingleton<IRedisSchedulerService, RedisSchedulerService>();
        services.AddSingleton<IRedisLockService, RedisLockService>();
        services.AddSingleton<IRedisCancellationService, RedisCancellationService>();

        return services;
    }

    /// <summary>
    /// Registers RabbitMQ services for job dispatching.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddRabbitMQ(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetSection(RabbitMQOptions.SectionKey).Get<RabbitMQOptions>() == null)
            throw new InvalidOperationException("RabbitMQ configuration section is missing or invalid.");

        // Configure RabbitMQOptions from appsettings
        services.AddOptions<RabbitMQOptions>().Bind(configuration.GetSection(RabbitMQOptions.SectionKey)).ValidateDataAnnotations();

        // Register RabbitMQConnectionFactory as singleton
        services.AddSingleton<RabbitMQConnectionFactory>();

        // Register IRabbitMQPublisher as singleton (thread-safe)
        services.AddSingleton<IRabbitMQPublisher, RabbitMQPublisher>();

        // Register Queue Monitoring Service
        services.AddSingleton<IQueueDepthMonitor, QueueDepthMonitor>();

        return services;
    }
}
