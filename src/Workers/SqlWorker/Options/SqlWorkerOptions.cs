namespace SqlWorker.Options;

/// <summary>
/// Configuration options for SQL Worker.
/// </summary>
public class SqlWorkerOptions
{
    /// <summary>
    /// Configuration section key in appsettings.json.
    /// </summary>
    public const string SectionKey = "SqlExecutorConfig";

    /// <summary>
    /// Named database connections available to jobs.
    /// Key is the connection alias (e.g., "MainDatabase"), value is the connection configuration.
    /// </summary>
    public Dictionary<string, SqlConnectionConfig> Connections { get; set; } = [];

    /// <summary>
    /// Gets the list of available connection names (aliases).
    /// </summary>
    public IReadOnlyList<string> GetConnectionNames() => [.. Connections.Keys];
}

/// <summary>
/// Individual database connection configuration.
/// </summary>
public class SqlConnectionConfig
{
    /// <summary>
    /// Database connection string.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// Database provider type.
    /// </summary>
    public SqlProviderType Provider { get; set; }

    /// <summary>
    /// Default command timeout in seconds.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Supported SQL database providers.
/// </summary>
public enum SqlProviderType
{
    /// <summary>
    /// Microsoft SQL Server (Microsoft.Data.SqlClient)
    /// </summary>
    SqlServer,

    /// <summary>
    /// PostgreSQL (Npgsql)
    /// </summary>
    PostgreSql,

    /// <summary>
    /// MySQL/MariaDB (MySqlConnector)
    /// </summary>
    MySql
}
