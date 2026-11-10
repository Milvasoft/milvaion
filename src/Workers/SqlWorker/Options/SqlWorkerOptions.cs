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

    /// <summary>
    /// Allows only single read-only statements. PostgreSQL and MySQL additionally run them in a read-only session.
    /// Stored procedures and NonQuery jobs are refused. Default: false.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Whether jobs may call stored procedures on this connection. Default: true.
    /// </summary>
    public bool AllowStoredProcedures { get; set; } = true;

    /// <summary>
    /// Most rows a Reader job can return, whatever its own MaxRows says. 0 = no limit. Default: 0.
    /// </summary>
    public int MaxRowsHardLimit { get; set; }

    /// <summary>
    /// Approximate limit on the size of a Reader job's rows as JSON; rows past it are left out and the result is marked
    /// as truncated. 0 = no limit. Default: 0.
    /// </summary>
    public long MaxResultBytes { get; set; }
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
