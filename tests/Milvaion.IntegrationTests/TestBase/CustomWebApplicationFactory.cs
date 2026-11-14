using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Milvaion.Api.AppStartup;
using Milvaion.Domain;
using Npgsql;
using Respawn;
using System.Security.Cryptography;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Milvaion.IntegrationTests.TestBase;

public class CustomWebApplicationFactory : WebApplicationFactory<IApiMarker>, IAsyncLifetime
{
    private Respawner _respawner;
    private NpgsqlConnection _connection;

    /// <summary>
    /// Database name for this factory instance. Override in subclasses to create isolated databases per collection.
    /// </summary>
    protected virtual string DatabaseName => "testDb";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("testDb")
        .WithUsername("root")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _redisContainer = new RedisBuilder("redis:7-alpine")
        .WithCleanUp(true)
        .Build();

    private readonly RabbitMqContainer _rabbitMqContainer = new RabbitMqBuilder("rabbitmq:3-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .WithCleanUp(true)
        .Build();

    public const string ResetAutoIncrementQuery = @"
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

    // Lock ensures parallel collection fixtures don't race on env vars during host build.
    // Each factory sets env vars and triggers host build atomically.
    private static readonly SemaphoreSlim _hostBuildLock = new(1, 1);

    // Shared so a host rebuilt through WithWebHostBuilder accepts tokens signed by the original one.
    private static readonly string _testJwtKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static readonly string _testApiKeySecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task InitializeAsync()
    {
        // Start all containers in parallel
        var startTasks = new List<Task>();

        if (_dbContainer.State != TestcontainersStates.Running)
            startTasks.Add(_dbContainer.StartAsync());

        if (_redisContainer.State != TestcontainersStates.Running)
            startTasks.Add(_redisContainer.StartAsync());

        if (_rabbitMqContainer.State != TestcontainersStates.Running)
            startTasks.Add(_rabbitMqContainer.StartAsync());

        startTasks.Add(StartAdditionalContainersAsync());

        await Task.WhenAll(startTasks);

        // Create isolated database if this collection uses a different DB name
        if (DatabaseName != "testDb")
        {
            await using var adminConn = new NpgsqlConnection($"{_dbContainer.GetConnectionString()};Timeout=30;");
            await adminConn.OpenAsync();
            await using var cmd = adminConn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{DatabaseName}'";
            var exists = await cmd.ExecuteScalarAsync();
            if (exists == null)
            {
                cmd.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Setup PostgreSQL connection to the target database
        _connection = new NpgsqlConnection($"{GetConnectionString()};Timeout=30;");
        await _connection.OpenAsync();

        // Set env vars and trigger host build under lock.
        // Env vars are needed because Program.cs reads configuration eagerly
        // (e.g. builder.Configuration.GetSection(...).Get<MilvaionConfig>() and
        // NpgsqlDataSourceBuilder) BEFORE ConfigureWebHost.ConfigureAppConfiguration runs.
        await _hostBuildLock.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("IsTestEnv", "true");
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnectionString", $"{GetConnectionString()};Pooling=false;");
            Environment.SetEnvironmentVariable("MilvaionConfig__Redis__ConnectionString", GetRedisConnectionString());
            Environment.SetEnvironmentVariable("MilvaionConfig__RabbitMQ__Host", GetRabbitMqHost());
            Environment.SetEnvironmentVariable("MilvaionConfig__RabbitMQ__Port", GetRabbitMqPort().ToString());
            Environment.SetEnvironmentVariable("MilvaionConfig__RabbitMQ__Username", "guest");
            Environment.SetEnvironmentVariable("MilvaionConfig__RabbitMQ__Password", "guest");
            Environment.SetEnvironmentVariable("MilvaionConfig__StatusTracker__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__WorkerAutoDiscovery__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__ZombieOccurrenceDetector__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__FailedOccurrenceHandler__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__WorkflowEngine__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__LogCollector__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__Logging__Seq__Enabled", "false");
            Environment.SetEnvironmentVariable("MilvaionConfig__Developer__EndpointsEnabled", "true");
            Environment.SetEnvironmentVariable("MilvaionConfig__RateLimiting__LoginPermitPerMinute", "0");
            Environment.SetEnvironmentVariable("Milvasoft__Identity__Token__SymmetricPublicKey", _testJwtKey);
            Environment.SetEnvironmentVariable("MilvaionConfig__ApiKey__Secret", _testApiKeySecret);

            // Allow subclasses to inject additional env vars before the host build.
            ConfigureAdditionalEnvironmentVariables();

            // Trigger host build — this reads env vars during Program.cs execution
            _ = Services;
        }
        finally
        {
            // Environment variables are process-wide: undo this collection's own ones before another host is built.
            ClearAdditionalEnvironmentVariables();

            _hostBuildLock.Release();
        }
    }

    /// <summary>
    /// Override in subclasses to set additional environment variables before the host is built.
    /// Called inside the host-build lock, after all base env vars are set.
    /// </summary>
    protected virtual void ConfigureAdditionalEnvironmentVariables() { }

    /// <summary>
    /// Override to remove what <see cref="ConfigureAdditionalEnvironmentVariables"/> set, once the host has read it.
    /// Called inside the host-build lock.
    /// </summary>
    protected virtual void ClearAdditionalEnvironmentVariables() { }

    /// <summary>
    /// Override in subclasses to start extra containers alongside the shared ones.
    /// </summary>
    protected virtual Task StartAdditionalContainersAsync() => Task.CompletedTask;

    public async Task CreateRespawner() => _respawner ??= await Respawner.CreateAsync(_connection, new RespawnerOptions
    {
        DbAdapter = DbAdapter.Postgres,
        SchemasToInclude = ["public"],
        TablesToIgnore = [TableNames.EfMigrationHistory]
    });

    public async Task ResetDatabase()
    {
        if (_respawner != null)
        {
            await _respawner.ResetAsync(_connection);
            await _dbContainer.ExecScriptAsync(ResetAutoIncrementQuery);
            await EnsureDefaultProjectAsync();
        }
    }

    /// <summary>
    /// Respawn empties every table, including the Default project row that the migration inserts and every project
    /// foreign key points at. Put it back.
    /// </summary>
    public async Task EnsureDefaultProjectAsync()
    {
        await using var command = _connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Projects" ("Id", "Slug", "Name", "IsDefault", "Settings", "CreationDate", "CreatorUserName")
            OVERRIDING SYSTEM VALUE
            VALUES (1, 'default', 'Default', TRUE, '{}', now() at time zone 'utc', 'System')
            ON CONFLICT ("Id") DO NOTHING;
            """;

        await command.ExecuteNonQueryAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            var stopTasks = new List<Task>
            {
                _connection?.CloseAsync() ?? Task.CompletedTask,
                _dbContainer.StopAsync(),
                _redisContainer.StopAsync(),
                _rabbitMqContainer.StopAsync()
            };

            await Task.WhenAny(Task.WhenAll(stopTasks), Task.Delay(5000));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Dispose error: {ex.Message}");
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <summary>
    /// Gets PostgreSQL connection string for the target database.
    /// </summary>
    public string GetConnectionString()
    {
        var baseConnStr = _dbContainer.GetConnectionString();
        if (DatabaseName == "testDb")
            return baseConnStr;

        // Replace the default database name with the collection-specific one
        var builder = new NpgsqlConnectionStringBuilder(baseConnStr)
        {
            Database = DatabaseName
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Gets Redis connection string.
    /// </summary>
    public string GetRedisConnectionString() => _redisContainer.GetConnectionString();

    /// <summary>
    /// Gets RabbitMQ host name.
    /// </summary>
    public string GetRabbitMqHost() => _rabbitMqContainer.Hostname;

    /// <summary>
    /// Gets RabbitMQ port.
    /// </summary>
    public int GetRabbitMqPort() => _rabbitMqContainer.GetMappedPublicPort(5672);

    /// <summary>
    /// Gets RabbitMQ connection string in format host:port.
    /// </summary>
    public string GetRabbitMqConnectionString() => $"{GetRabbitMqHost()}:{GetRabbitMqPort()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((context, config) =>
                                                                         {
                                                                             config.AddInMemoryCollection(new Dictionary<string, string>
                                                                             {
                                                                                 ["ConnectionStrings:DefaultConnectionString"] = $"{GetConnectionString()};Pooling=false;",
                                                                                 ["MilvaionConfig:Redis:ConnectionString"] = GetRedisConnectionString(),
                                                                                 ["MilvaionConfig:RabbitMQ:Host"] = GetRabbitMqHost(),
                                                                                 ["MilvaionConfig:RabbitMQ:Port"] = GetRabbitMqPort().ToString(),
                                                                                 ["MilvaionConfig:RabbitMQ:Username"] = "guest",
                                                                                 ["MilvaionConfig:RabbitMQ:Password"] = "guest",
                                                                                 ["MilvaionConfig:StatusTracker:Enabled"] = "false",
                                                                                 ["MilvaionConfig:WorkerAutoDiscovery:Enabled"] = "false",
                                                                                 ["MilvaionConfig:ZombieOccurrenceDetector:Enabled"] = "false",
                                                                                 ["MilvaionConfig:FailedOccurrenceHandler:Enabled"] = "false",
                                                                                 ["MilvaionConfig:LogCollector:Enabled"] = "false",
                                                                                 ["MilvaionConfig:Alerting"] = "{}",
                                                                                 ["MilvaionConfig:Logging:Seq:Enabled"] = "false",
                                                                             });
                                                                         });

    public virtual HttpClient CreateClientWithHeaders(params KeyValuePair<string, string>[] headers)
    {
        var client = CreateClient();

        foreach (var header in headers)
        {
            client.DefaultRequestHeaders.Add(header.Key, header.Value);
        }

        return client;
    }

    public virtual HttpClient CreateClientWithLanguageHeader(string languageCode, bool login = true)
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Add("Accept-Language", languageCode);

        return client;
    }
}