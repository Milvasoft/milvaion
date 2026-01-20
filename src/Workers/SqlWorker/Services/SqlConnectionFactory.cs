using SqlWorker.Options;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using MySqlConnector;
using Microsoft.Extensions.Options;

namespace SqlWorker.Services;

/// <summary>
/// Factory for creating database connections based on configuration.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Creates a database connection for the specified connection name.
    /// </summary>
    /// <param name="connectionName">The connection alias from configuration</param>
    /// <returns>An open database connection</returns>
    /// <exception cref="ArgumentException">If connection name is not found in configuration</exception>
    DbConnection CreateConnection(string connectionName);

    /// <summary>
    /// Gets the available connection names from configuration.
    /// </summary>
    IReadOnlyList<string> GetAvailableConnectionNames();

    /// <summary>
    /// Validates that a connection name exists in configuration.
    /// </summary>
    bool ConnectionExists(string connectionName);

    /// <summary>
    /// Gets the provider type for a connection.
    /// </summary>
    SqlProviderType GetProviderType(string connectionName);

    /// <summary>
    /// Gets the default timeout for a connection.
    /// </summary>
    int GetDefaultTimeout(string connectionName);
}

/// <summary>
/// Default implementation of ISqlConnectionFactory.
/// </summary>
public class SqlConnectionFactory(IOptions<SqlWorkerOptions> options) : ISqlConnectionFactory
{
    private readonly SqlWorkerOptions _options = options.Value;

    public DbConnection CreateConnection(string connectionName)
    {
        if (!_options.Connections.TryGetValue(connectionName, out var config))
        {
            var available = string.Join(", ", _options.Connections.Keys);
            throw new ArgumentException(
                $"Connection '{connectionName}' not found in configuration. Available connections: {available}",
                nameof(connectionName));
        }

        DbConnection connection = config.Provider switch
        {
            SqlProviderType.SqlServer => new SqlConnection(config.ConnectionString),
            SqlProviderType.PostgreSql => new NpgsqlConnection(config.ConnectionString),
            SqlProviderType.MySql => new MySqlConnection(config.ConnectionString),
            _ => throw new NotSupportedException($"Database provider '{config.Provider}' is not supported")
        };

        return connection;
    }

    public IReadOnlyList<string> GetAvailableConnectionNames() => _options.GetConnectionNames();

    public bool ConnectionExists(string connectionName) => _options.Connections.ContainsKey(connectionName);

    public SqlProviderType GetProviderType(string connectionName)
    {
        if (!_options.Connections.TryGetValue(connectionName, out var config))
            throw new ArgumentException($"Connection '{connectionName}' not found", nameof(connectionName));

        return config.Provider;
    }

    public int GetDefaultTimeout(string connectionName)
    {
        if (!_options.Connections.TryGetValue(connectionName, out var config))
            throw new ArgumentException($"Connection '{connectionName}' not found", nameof(connectionName));

        return config.DefaultTimeoutSeconds;
    }
}
