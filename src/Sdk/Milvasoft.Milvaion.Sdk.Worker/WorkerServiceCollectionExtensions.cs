using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using Milvasoft.Milvaion.Sdk.Worker.Services;
using System.Net;
using System.Reflection;

namespace Milvasoft.Milvaion.Sdk.Worker;

/// <summary>
/// Service collection extensions for worker SDK.
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Milvaion Worker SDK with automatic job discovery and consumer registration.
    /// Discovers all IJob implementations in the entry assembly and registers them with their consumers.
    /// Validates that each job has corresponding configuration and vice versa.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddMilvaionWorkerWithJobs(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Register core Worker SDK services
        services.AddMilvaionWorker(configuration);

        // 2. Auto-discover IJob implementations
        var jobTypes = Assembly.GetEntryAssembly()?
                               .GetTypes()
                               .Where(t => typeof(IJobBase).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                               .ToDictionary(t => t.Name, t => t) ?? [];

        // 3. Load job consumer configurations
        var jobConsumersSection = configuration.GetSection(JobConsumerOptions.SectionKey);

        if (!jobConsumersSection.Exists())
            throw new InvalidOperationException($"Configuration section '{JobConsumerOptions.SectionKey}' not found in appsettings.json");

        var jobConfigs = new Dictionary<string, JobConsumerConfig>();

        // Get WorkerId from configuration for auto-generating RoutingPattern
        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();
        var workerId = workerOptions?.WorkerId ?? Environment.MachineName;

        foreach (var jobSection in jobConsumersSection.GetChildren())
        {
            var config = new JobConsumerConfig();

            jobSection.Bind(config);

            if (string.IsNullOrEmpty(config.ConsumerId))
                throw new InvalidOperationException($"Invalid configuration for job '{jobSection.Key}': ConsumerId is required");

            // Auto-generate RoutingPattern if not specified
            // Format: {workerId}.{jobName (without "Job" suffix, lowercase)}.*
            // Example: "email-worker-01.sendemail.*" for job "SendEmailJob"
            if (string.IsNullOrEmpty(config.RoutingPattern))
            {
                var jobName = jobSection.Key;
                var normalizedJobName = jobName.EndsWith("Job", StringComparison.OrdinalIgnoreCase)
                    ? jobName[..^3].ToLowerInvariant()
                    : jobName.ToLowerInvariant();

                config.RoutingPattern = $"{workerId.ToLowerInvariant()}.{normalizedJobName}.*";

                Console.WriteLine($"Auto-generated RoutingPattern for {jobName}: {config.RoutingPattern}");
            }

            jobConfigs[jobSection.Key] = config;
        }

        // 4. Validate: Job classes without configuration
        var jobsWithoutConfig = jobTypes.Keys.Except(jobConfigs.Keys).ToList();

        if (!jobsWithoutConfig.IsNullOrEmpty())
        {
            var jobList = string.Join(", ", jobsWithoutConfig);

            throw new InvalidOperationException($"Job implementation(s) found without configuration in 'JobConsumers' section: {jobList}. Add configuration for each job or remove the job class.");
        }

        // 5. Validate: Configurations without job classes
        var configsWithoutJob = jobConfigs.Keys.Except(jobTypes.Keys).ToList();

        if (!configsWithoutJob.IsNullOrEmpty())
        {
            var configList = string.Join(", ", configsWithoutJob);
            throw new InvalidOperationException($"Configuration found in 'JobConsumers' section without corresponding job class: {configList}. Implement the job class or remove the configuration entry.");
        }

        // 6. Register validated jobs and set JobType on configs
        foreach (var (jobName, jobType) in jobTypes)
        {
            // Register job as transient
            services.AddTransient(typeof(IJobBase), jobType);

            // Set JobType on config for job data discovery
            if (jobConfigs.TryGetValue(jobName, out var config))
                config.JobType = jobType;

            Console.WriteLine($"Registered job: {jobName} → {jobType.FullName}");
        }

        // 7. Validate and register job consumers from configuration
        services.AddJobConsumersFromConfiguration(configuration);

        Console.WriteLine($"Worker SDK initialized with {jobTypes.Count} job(s)");

        return services;
    }

    /// <summary>
    /// Registers Milvaion Worker SDK services.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    private static IServiceCollection AddMilvaionWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IMilvaLogger, MilvaionLogger>();

        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();

        // Bind WorkerOptions configuration and generate InstanceId
        services.Configure<WorkerOptions>(opts =>
        {
            workerSection.Bind(opts);

            // Generate InstanceId AFTER WorkerId is bound from config
            opts.RegenerateInstanceId();

            Console.WriteLine($"WorkerId: {opts.WorkerId}, InstanceId: {opts.InstanceId}");
            Console.WriteLine($"Machine: {Environment.MachineName}, ProcessId: {Environment.ProcessId}");
        });

        // Bind JobConsumerOptions configuration
        services.Configure<JobConsumerOptions>(configuration.GetSection(JobConsumerOptions.SectionKey));

        // Register Redis IConnectionMultiplexer for cancellation listener
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
            var connectionString = string.IsNullOrEmpty(options.Redis.Password) ? options.Redis.ConnectionString : $"{options.Redis.ConnectionString},password={options.Redis.Password}";

            return StackExchange.Redis.ConnectionMultiplexer.Connect(connectionString);
        });

        // Register core services
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<WorkerJobTracker>();

        // Register offline resilience services if enabled
        if (workerOptions.OfflineResilience.Enabled)
        {
            // Register LocalStateStore as singleton (shared by all consumers)
            services.AddSingleton<LocalStateStore>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var storagePath = options.OfflineResilience?.LocalStoragePath ?? "./worker_data";

                return new LocalStateStore(storagePath, loggerFactory);
            });

            // Register interface for LocalStateStore
            services.AddSingleton<ILocalStateStore>(sp => sp.GetRequiredService<LocalStateStore>());
        }

        // Register ConnectionMonitor as singleton (shared by all consumers)
        services.AddSingleton<ConnectionMonitor>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>();

            return new ConnectionMonitor(options, logger);
        });

        // Register interface for ConnectionMonitor
        services.AddSingleton<IConnectionMonitor>(sp => sp.GetRequiredService<ConnectionMonitor>());

        // Register StatusUpdatePublisher as singleton (shared by all consumers)
        services.AddSingleton<StatusUpdatePublisher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return new StatusUpdatePublisher(options, loggerFactory);
        });

        // Register interface for StatusUpdatePublisher
        services.AddSingleton<IStatusUpdatePublisher>(sp => sp.GetRequiredService<StatusUpdatePublisher>());

        // Register LogPublisher as singleton (shared by all consumers)
        services.AddSingleton<LogPublisher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return new LogPublisher(options, loggerFactory);
        });

        // Register interface for LogPublisher
        services.AddSingleton<ILogPublisher>(sp => sp.GetRequiredService<LogPublisher>());

        // Register OutboxService as singleton (shared by all consumers)
        services.AddSingleton<OutboxService>(sp =>
        {
            var localStore = sp.GetRequiredService<ILocalStateStore>();
            var statusPublisher = sp.GetRequiredService<IStatusUpdatePublisher>();
            var logPublisher = sp.GetRequiredService<ILogPublisher>();
            var connectionMonitor = sp.GetRequiredService<IConnectionMonitor>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>();

            return new OutboxService(localStore, statusPublisher, logPublisher, connectionMonitor, logger);
        });

        // Register SyncOrchestratorService as hosted service
        services.AddHostedService(sp =>
        {
            var outboxService = sp.GetRequiredService<OutboxService>();
            var localStore = sp.GetRequiredService<LocalStateStore>();
            var connectionMonitor = sp.GetRequiredService<ConnectionMonitor>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>();
            var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;

            return new SyncOrchestratorService(outboxService, localStore, connectionMonitor, logger, options);
        });

        Console.WriteLine("All Milvaion worker services registered successfully!");

        return services;
    }

    public static IServiceCollection AddFileHealthCheck(this IServiceCollection services, IConfiguration configuration)
    {
        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();

        // Register FileHealthCheckBackgroundService if enabled
        if (workerOptions.HealthCheck.Enabled)
        {
            services.AddHealthChecks()
                    .AddCheck<RedisHealthCheck>("Redis", tags: ["redis", "cache"])
                    .AddCheck<RabbitMQHealthCheck>("RabbitMQ", tags: ["rabbitmq", "messaging"]);

            services.AddHostedService(sp =>
            {
                var healthCheckService = sp.GetRequiredService<HealthCheckService>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;

                return new FileHealthCheckBackgroundService(healthCheckService, options, loggerFactory);
            });

            Console.WriteLine($"File-based health check enabled. File: {workerOptions.HealthCheck.LiveFilePath}, Interval: {workerOptions.HealthCheck.IntervalSeconds}s");
        }

        return services;
    }

    public static IServiceCollection AddHealthCheckEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();

        // Register FileHealthCheckBackgroundService if enabled
        if (workerOptions.HealthCheck.Enabled)
        {
            services.AddHealthChecks()
                    .AddCheck<RedisHealthCheck>("Redis", tags: ["redis", "cache"])
                    .AddCheck<RabbitMQHealthCheck>("RabbitMQ", tags: ["rabbitmq", "messaging"]);

            Console.WriteLine($"Endpoint-based health check enabled. File: {workerOptions.HealthCheck.LiveFilePath}, Interval: {workerOptions.HealthCheck.IntervalSeconds}s");
        }

        return services;
    }

    public static IApplicationBuilder UseHealthCheckEndpoints(this WebApplication app, IConfiguration configuration)
    {
        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();

        // Register FileHealthCheckBackgroundService if enabled
        if (workerOptions.HealthCheck.Enabled)
        {
            // Health check endpoints
            app.MapGet("/health", () => Results.Ok("Ok")).WithName("HealthCheck");

            app.MapGet("/health/ready", async (HealthCheckService healthCheckService, CancellationToken cancellationToken) =>
            {
                var healthReport = await healthCheckService.CheckHealthAsync(cancellationToken);

                var response = new HealthCheckResponse
                {
                    Status = healthReport.Status.ToString(),
                    Duration = healthReport.TotalDuration,
                    Timestamp = DateTime.UtcNow,
                    Checks = [.. healthReport.Entries.Select(e => new HealthCheckEntry
                    {
                        Name = e.Key,
                        Status = e.Value.Status.ToString(),
                        Description = e.Value.Description,
                        Duration = e.Value.Duration,
                        Tags = [.. e.Value.Tags],
                        Data = e.Value.Data.ToDictionary(d => d.Key, d => d.Value?.ToString())
                    })]
                };

                return healthReport.Status == HealthStatus.Healthy ? Results.Ok(response) : Results.Json(response, statusCode: (int)HttpStatusCode.ServiceUnavailable);
            }).WithName("ReadinessProbe");

            app.MapGet("/health/live", () => Results.Ok(new LivenessResponse
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Uptime = GetUptime()
            })).WithName("LivenessProbe");

            app.MapGet("/health/startup", () => Results.Ok(new LivenessResponse
            {
                Status = "Started",
                Timestamp = DateTime.UtcNow,
                Uptime = GetUptime()
            })).WithName("StartupProbe");

            Console.WriteLine($"Endpoint-based health check enabled. File: {workerOptions.HealthCheck.LiveFilePath}, Interval: {workerOptions.HealthCheck.IntervalSeconds}s");
        }

        return app;

        static TimeSpan GetUptime()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();

            return DateTime.UtcNow - process.StartTime.ToUniversalTime();
        }
    }

    /// <summary>
    /// Registers job consumers based on configuration section.
    /// Loads job-specific configurations from "JobConsumers" section in appsettings.json.
    /// Also discovers job types and populates JobType property for schema extraction.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration root</param>
    /// <returns>Service collection for chaining</returns>
    private static IServiceCollection AddJobConsumersFromConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var jobConsumersSection = configuration.GetSection(JobConsumerOptions.SectionKey);

        if (!jobConsumersSection.Exists())
            throw new InvalidOperationException($"Configuration section '{JobConsumerOptions.SectionKey}' not found in appsettings.json");

        // Get WorkerId from configuration for worker-specific routing
        var workerSection = configuration.GetSection(WorkerOptions.SectionKey);
        var workerOptions = workerSection.Get<WorkerOptions>();
        var workerId = workerOptions?.WorkerId ?? Environment.MachineName;

        // Discover job types from entry assembly
        var discoveredJobTypes = Assembly.GetEntryAssembly()?
                                         .GetTypes()
                                         .Where(t => typeof(IJobBase).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                                         .ToDictionary(t => t.Name, t => t) ?? [];

        var jobConfigs = new Dictionary<string, JobConsumerConfig>();

        // Load all job consumer configurations
        foreach (var jobSection in jobConsumersSection.GetChildren())
        {
            var config = new JobConsumerConfig();

            jobSection.Bind(config);

            // Validate that config was actually bound
            if (string.IsNullOrEmpty(config.ConsumerId))
                throw new InvalidOperationException($"Invalid configuration for job '{jobSection.Key}': ConsumerId is required");

            // Auto-generate routing pattern if not provided
            // Includes WorkerId prefix for worker-specific routing
            if (string.IsNullOrWhiteSpace(config.RoutingPattern))
            {
                var generatedPattern = GenerateRoutingPattern(jobSection.Key, workerId);

                config.RoutingPattern = generatedPattern;

                Console.WriteLine($"Auto-generated routing pattern for '{jobSection.Key}': {generatedPattern}");
            }

            // Set JobType from discovered types for schema extraction
            if (discoveredJobTypes.TryGetValue(jobSection.Key, out var jobType))
            {
                config.JobType = jobType;
            }

            jobConfigs[jobSection.Key] = config;
        }

        if (jobConfigs.Count == 0)
            throw new InvalidOperationException($"No job consumer configurations found in '{JobConsumerOptions.SectionKey}' section");

        // Register dedicated consumers for each job
        foreach (var (jobName, config) in jobConfigs)
        {
            // Capture variables for closure
            var capturedJobName = jobName;
            var capturedConfig = config;

            // Use keyed registration to allow multiple JobConsumer instances
            services.AddSingleton<IHostedService>(sp =>
            {
                var baseOptions = sp.GetRequiredService<IOptions<WorkerOptions>>();
                var jobConsumerOptions = sp.GetRequiredService<IOptions<JobConsumerOptions>>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>();

                // Get OutboxService if offline resilience is enabled (singleton, shared)
                var outboxService = sp.GetService<OutboxService>();

                var workerOptions = baseOptions.Value;

                var dedicatedOptions = new WorkerOptions
                {
                    WorkerId = workerOptions.WorkerId,
                    MaxParallelJobs = capturedConfig.MaxParallelJobs > 0 ? capturedConfig.MaxParallelJobs : workerOptions.MaxParallelJobs,
                    RabbitMQ = new RabbitMQSettings
                    {
                        Host = workerOptions.RabbitMQ.Host,
                        Port = workerOptions.RabbitMQ.Port,
                        Username = workerOptions.RabbitMQ.Username,
                        Password = workerOptions.RabbitMQ.Password,
                        VirtualHost = workerOptions.RabbitMQ.VirtualHost,
                        RoutingKeyPattern = capturedConfig.RoutingPattern
                    },
                    Redis = workerOptions.Redis,
                    Heartbeat = workerOptions.Heartbeat,
                    OfflineResilience = workerOptions.OfflineResilience // ADDED: Copy OfflineResilience settings
                };

                // Use the SAME InstanceId from base options
                dedicatedOptions.SetInstanceId(workerOptions.InstanceId);

                logger.Debug($"JobConsumer for '{capturedJobName}': InstanceId = {dedicatedOptions.InstanceId}");

                logger.Information("Registered JobConsumer '{ConsumerId}' for job type '{JobName}' with InstanceId: {InstanceId}, patterns: {Patterns}", capturedConfig.ConsumerId, capturedJobName, dedicatedOptions.InstanceId, capturedConfig.RoutingPattern);

                // Pass OutboxService (null if offline resilience disabled)
                return new JobConsumer(sp, Microsoft.Extensions.Options.Options.Create(dedicatedOptions), jobConsumerOptions, logger, outboxService);
            });
        }

        // Register single worker registration publisher for ALL job consumers
        if (jobConfigs.Count > 0)
        {
            services.AddHostedService(sp =>
            {
                var baseOptions = sp.GetRequiredService<IOptions<WorkerOptions>>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>();

                // Pass all job configs to single registration publisher
                return new WorkerListenerPublisher(baseOptions, logger, sp, jobConfigs);
            });

            // Register cancellation listener service
            services.AddHostedService<CancellationListenerService>();
        }

        return services;
    }

    /// <summary>
    /// Generates routing key pattern from job type name with WorkerId prefix.
    /// Ensures worker-specific routing - different workers with same job name won't conflict.
    /// Examples (with workerId "email-worker-01"):
    /// - SendEmailJob → email-worker-01.sendemail.*
    /// - TestJob → email-worker-01.test.*
    /// - NonParallelJob → email-worker-01.nonparallel.*
    /// </summary>
    private static string GenerateRoutingPattern(string jobTypeName, string workerId)
    {
        // Remove "Job" suffix if present
        if (jobTypeName.EndsWith("Job", StringComparison.OrdinalIgnoreCase))
        {
            jobTypeName = jobTypeName[..^3];
        }

        // Convert to lowercase WITHOUT splitting
        // "NonParallel" → "nonparallel"
        // "SendEmail" → "sendemail"
        var jobPattern = jobTypeName.ToLowerInvariant();

        // Add WorkerId prefix and wildcard suffix
        // "email-worker-01" + "sendemail" → "email-worker-01.sendemail.*"
        return $"{workerId.ToLowerInvariant()}.{jobPattern}.*";
    }
}
