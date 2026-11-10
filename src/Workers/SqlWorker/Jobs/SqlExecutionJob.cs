using Dapper;
using Microsoft.Data.SqlClient;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using MySqlConnector;
using Npgsql;
using SqlWorker.Options;
using SqlWorker.Services;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlWorker.Jobs;

/// <summary>
/// SQL execution job that supports multiple database providers.
/// Uses Dapper for lightweight, high-performance database operations.
/// </summary>
public class SqlExecutionJob(ISqlConnectionFactory connectionFactory) : IAsyncJobWithResult<SqlJobData, string>
{
    private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        // 1. Deserialize job data
        var jobData = context.GetData<SqlJobData>() ?? throw new PermanentJobException("SqlJobData is required but was null");

        // 2. Validate connection exists
        if (!_connectionFactory.ConnectionExists(jobData.ConnectionName))
        {
            var available = string.Join(", ", _connectionFactory.GetAvailableConnectionNames());

            throw new PermanentJobException($"Connection '{jobData.ConnectionName}' not found. Available: {available}");
        }

        var connectionConfig = _connectionFactory.GetConnectionConfig(jobData.ConnectionName);

        SqlStatementGuard.EnsureAllowed(jobData, connectionConfig);

        context.LogInformation($"Executing SQL on '{jobData.ConnectionName}' ({_connectionFactory.GetProviderType(jobData.ConnectionName)})");
        context.LogInformation($"Query type: {jobData.QueryType}, Command type: {jobData.CommandType}");

        // 3. Create connection
        using var connection = _connectionFactory.CreateConnection(jobData.ConnectionName);

        try
        {
            await connection.OpenAsync(context.CancellationToken);
            context.LogInformation("Database connection opened");

            if (connectionConfig.ReadOnly)
                await EnforceReadOnlySessionAsync(connection, connectionConfig.Provider, context.CancellationToken);

            // 4. Setup command parameters
            var commandType = jobData.CommandType == SqlCommandType.StoredProcedure ? CommandType.StoredProcedure : CommandType.Text;

            var timeout = jobData.TimeoutSeconds > 0 ? jobData.TimeoutSeconds : _connectionFactory.GetDefaultTimeout(jobData.ConnectionName);

            var parameters = new DynamicParameters();

            if (jobData.Parameters?.Count > 0)
            {
                foreach (var (key, value) in jobData.Parameters)
                {
                    // Handle JsonElement values from deserialization
                    var paramValue = value is JsonElement je ? ConvertJsonElement(je) : value;
                    parameters.Add(key, paramValue);
                }

                context.LogInformation($"Parameters: {jobData.Parameters.Count} parameter(s)");
            }

            // 5. Execute with optional transaction
            if (jobData.UseTransaction)
            {
                return await ExecuteWithTransactionAsync(connection, jobData, connectionConfig, commandType, timeout, parameters, context);
            }

            return await ExecuteQueryAsync(connection, null, jobData, connectionConfig, commandType, timeout, parameters, context);
        }
        catch (Exception ex) when (ex is not PermanentJobException)
        {
            context.LogError($"SQL execution failed: {ex.Message}");

            // Determine if error is permanent (configuration/syntax) or transient (connection/timeout)
            if (IsPermanentError(ex))
            {
                throw new PermanentJobException($"SQL error: {ex.Message}", ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Makes the database itself refuse writes where it can. Reset when the connection returns to the pool.
    /// </summary>
    private static async Task EnforceReadOnlySessionAsync(DbConnection connection, SqlProviderType provider, CancellationToken cancellationToken)
    {
        var sql = provider switch
        {
            SqlProviderType.PostgreSql => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY",
            SqlProviderType.MySql => "SET SESSION TRANSACTION READ ONLY",
            _ => null
        };

        if (sql != null)
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static async Task<string> ExecuteWithTransactionAsync(DbConnection connection,
                                                                  SqlJobData jobData,
                                                                  SqlConnectionConfig connectionConfig,
                                                                  CommandType commandType,
                                                                  int timeout,
                                                                  DynamicParameters parameters,
                                                                  IJobContext context)
    {
        var isolationLevel = jobData.IsolationLevel switch
        {
            SqlIsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
            SqlIsolationLevel.ReadCommitted => IsolationLevel.ReadCommitted,
            SqlIsolationLevel.RepeatableRead => IsolationLevel.RepeatableRead,
            SqlIsolationLevel.Serializable => IsolationLevel.Serializable,
            SqlIsolationLevel.Snapshot => IsolationLevel.Snapshot,
            _ => IsolationLevel.ReadCommitted
        };

        using var transaction = await connection.BeginTransactionAsync(isolationLevel, context.CancellationToken);
        context.LogInformation($"Transaction started with isolation level: {isolationLevel}");

        try
        {
            var result = await ExecuteQueryAsync(connection, transaction, jobData, connectionConfig, commandType, timeout, parameters, context);

            transaction.Commit();
            context.LogInformation("Transaction committed");

            return result;
        }
        catch
        {
            transaction.Rollback();
            context.LogWarning("Transaction rolled back due to error");
            throw;
        }
    }

    private static async Task<string> ExecuteQueryAsync(DbConnection connection,
                                                        DbTransaction transaction,
                                                        SqlJobData jobData,
                                                        SqlConnectionConfig connectionConfig,
                                                        CommandType commandType,
                                                        int timeout,
                                                        DynamicParameters parameters,
                                                        IJobContext context)
    {
        var commandDefinition = new CommandDefinition(jobData.Query,
                                                      parameters,
                                                      transaction,
                                                      timeout,
                                                      commandType,
                                                      cancellationToken: context.CancellationToken);

        return jobData.QueryType switch
        {
            SqlQueryType.NonQuery => await ExecuteNonQueryAsync(connection, commandDefinition, context),
            SqlQueryType.Scalar => await ExecuteScalarAsync(connection, commandDefinition, context),
            SqlQueryType.Reader => await ExecuteReaderAsync(connection, commandDefinition, SqlStatementGuard.GetEffectiveMaxRows(jobData.MaxRows, connectionConfig.MaxRowsHardLimit), connectionConfig.MaxResultBytes, context),
            _ => throw new PermanentJobException($"Unknown query type: {jobData.QueryType}")
        };
    }

    private static async Task<string> ExecuteNonQueryAsync(IDbConnection connection,
                                                           CommandDefinition command,
                                                           IJobContext context)
    {
        var affectedRows = await connection.ExecuteAsync(command);

        context.LogInformation($"NonQuery executed: {affectedRows} row(s) affected");

        return JsonSerializer.Serialize(new
        {
            QueryType = "NonQuery",
            AffectedRows = affectedRows,
            Success = true
        }, _jsonOptions);
    }

    private static async Task<string> ExecuteScalarAsync(IDbConnection connection,
                                                         CommandDefinition command,
                                                         IJobContext context)
    {
        var result = await connection.ExecuteScalarAsync(command);

        context.LogInformation($"Scalar executed: {result ?? "null"}");

        return JsonSerializer.Serialize(new
        {
            QueryType = "Scalar",
            Value = result,
            Success = true
        }, _jsonOptions);
    }

    private static async Task<string> ExecuteReaderAsync(DbConnection connection,
                                                         CommandDefinition command,
                                                         int maxRows,
                                                         long maxResultBytes,
                                                         IJobContext context)
    {
        List<object> resultList;
        var truncated = false;

        if (maxRows > 0 || maxResultBytes > 0)
        {
            // Streamed so that only the rows being kept are ever held in memory
            resultList = [];
            long resultBytes = 0;

            var rows = connection.QueryUnbufferedAsync(command.CommandText, command.Parameters, (DbTransaction)command.Transaction, command.CommandTimeout, command.CommandType);

            await foreach (object row in rows.WithCancellation(context.CancellationToken))
            {
                if (maxRows > 0 && resultList.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                if (maxResultBytes > 0)
                {
                    resultBytes += JsonSerializer.SerializeToUtf8Bytes(row, _jsonOptions).Length + 1;

                    if (resultBytes > maxResultBytes)
                    {
                        truncated = true;
                        break;
                    }
                }

                resultList.Add(row);
            }
        }
        else
        {
            resultList = [.. await connection.QueryAsync(command)];
        }

        var rowCount = resultList.Count;

        context.LogInformation($"Reader executed: {rowCount} row(s) returned{(maxRows > 0 ? $" (max: {maxRows})" : "")}{(truncated ? " (truncated)" : "")}");

        return JsonSerializer.Serialize(new
        {
            QueryType = "Reader",
            RowCount = rowCount,
            Data = resultList,
            Truncated = truncated ? true : (bool?)null,
            Success = true
        }, _jsonOptions);
    }

    private static object ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    private static bool IsPermanentError(Exception ex)
    {
        // Error codes first: server messages can be localized, which would make the text match below miss them
        var isPermanentCode = ex switch
        {
            PostgresException pg => pg.SqlState.Length >= 2 && pg.SqlState[..2] is "22" or "23" or "28" or "3D" or "3F" or "42",
            SqlException sql => sql.Number is 102 or 105 or 156 or 207 or 208 or 229 or 230 or 262 or 515 or 547 or 2601 or 2627 or 2812 or 18456,
            MySqlException my => my.Number is 1044 or 1045 or 1049 or 1054 or 1062 or 1064 or 1142 or 1143 or 1146 or 1216 or 1217 or 1364 or 1406 or 1451 or 1452,
            _ => false
        };

        if (isPermanentCode)
            return true;

        var message = ex.Message.ToLowerInvariant();

        // SQL syntax errors, permission errors, invalid object names are permanent
        return message.Contains("syntax") ||
               message.Contains("permission") ||
               message.Contains("denied") ||
               message.Contains("invalid object") ||
               message.Contains("does not exist") ||
               message.Contains("unknown column") ||
               message.Contains("constraint") ||
               message.Contains("duplicate") ||
               message.Contains("violation");
    }
}

