using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Milvaion.Api.AppStartup;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Milvaion.IntegrationTests.TestBase;

public class CustomWebApplicationFactory : WebApplicationFactory<IApiMarker>, IAsyncLifetime
{
    private Respawner _respawner;
    private NpgsqlConnection _connection;

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:latest")
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

        await Task.WhenAll(startTasks);

        // Setup PostgreSQL connection
        _connection = new NpgsqlConnection($"{_dbContainer.GetConnectionString()};Timeout=30;");
        await _connection.OpenAsync();
    }

    public async Task CreateRespawner() => _respawner ??= await Respawner.CreateAsync(_connection, new RespawnerOptions
    {
        DbAdapter = DbAdapter.Postgres,
        SchemasToInclude = ["public"],
        TablesToIgnore = ["__EFMigrationsHistory"]
    });

    public async Task ResetDatabase()
    {
        if (_respawner != null)
        {
            await _respawner.ResetAsync(_connection);
            await _dbContainer.ExecScriptAsync(ResetAutoIncrementQuery);
        }
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
    /// Gets PostgreSQL connection string.
    /// </summary>
    public string GetConnectionString() => _dbContainer.GetConnectionString();

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