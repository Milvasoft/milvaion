using Milvasoft.Milvaion.Sdk.Worker.Attributes;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SqlWorker.Jobs;

/// <summary>
/// Job data for SQL execution.
/// </summary>
public class SqlJobData
{
    /// <summary>
    /// Configuration key for dynamic enum values (connection aliases).
    /// </summary>
    public const string ConnectionsConfigKey = "SqlExecutorConfig:Connections";

    /// <summary>
    /// Connection alias from worker configuration.
    /// Must match a key in SqlExecutorConfig.Connections section.
    /// </summary>
    [Required]
    [Description("Database connection alias (configured in worker's appsettings.json)")]
    [DynamicEnum(ConnectionsConfigKey)]
    public string ConnectionName { get; set; }

    /// <summary>
    /// SQL query or stored procedure name to execute.
    /// </summary>
    [Required]
    [Description("SQL query or stored procedure name to execute")]
    public string Query { get; set; }

    /// <summary>
    /// Query parameters as key-value pairs.
    /// Parameter names should match @paramName in query.
    /// </summary>
    [Description("Query parameters as key-value pairs (e.g., { \"Id\": 123, \"Name\": \"John\" })")]
    public Dictionary<string, object> Parameters { get; set; }

    /// <summary>
    /// Type of SQL command.
    /// </summary>
    [DefaultValue(SqlCommandType.Text)]
    [Description("Command type: Text (raw SQL) or StoredProcedure")]
    public SqlCommandType CommandType { get; set; } = SqlCommandType.Text;

    /// <summary>
    /// Type of query execution and expected result.
    /// </summary>
    [DefaultValue(SqlQueryType.NonQuery)]
    [Description("Query type: NonQuery (INSERT/UPDATE/DELETE), Scalar (single value), or Reader (result set as JSON)")]
    public SqlQueryType QueryType { get; set; } = SqlQueryType.NonQuery;

    /// <summary>
    /// Command timeout in seconds. Overrides connection default if specified.
    /// </summary>
    [Description("Command timeout in seconds (0 = use connection default)")]
    public int TimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Whether to wrap the execution in a transaction.
    /// </summary>
    [DefaultValue(false)]
    [Description("Wrap execution in a database transaction")]
    public bool UseTransaction { get; set; } = false;

    /// <summary>
    /// Transaction isolation level when UseTransaction is true.
    /// </summary>
    [DefaultValue(SqlIsolationLevel.ReadCommitted)]
    [Description("Transaction isolation level")]
    public SqlIsolationLevel IsolationLevel { get; set; } = SqlIsolationLevel.ReadCommitted;

    /// <summary>
    /// Maximum number of rows to return for Reader queries.
    /// 0 = no limit.
    /// </summary>
    [DefaultValue(0)]
    [Description("Maximum rows to return (0 = unlimited)")]
    public int MaxRows { get; set; } = 0;
}

/// <summary>
/// SQL command types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlCommandType
{
    /// <summary>
    /// Raw SQL text query.
    /// </summary>
    Text,

    /// <summary>
    /// Stored procedure call.
    /// </summary>
    StoredProcedure
}

/// <summary>
/// SQL query execution types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlQueryType
{
    /// <summary>
    /// Execute non-query (INSERT, UPDATE, DELETE).
    /// Returns affected row count.
    /// </summary>
    NonQuery,

    /// <summary>
    /// Execute scalar query.
    /// Returns first column of first row.
    /// </summary>
    Scalar,

    /// <summary>
    /// Execute reader query.
    /// Returns result set as JSON array.
    /// </summary>
    Reader
}

/// <summary>
/// Transaction isolation levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlIsolationLevel
{
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
    Snapshot
}
