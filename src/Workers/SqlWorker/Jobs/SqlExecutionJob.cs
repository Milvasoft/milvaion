using Dapper;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using SqlWorker.Services;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlWorker.Jobs;

/// <summary>
/// SQL execution job that supports multiple database providers.
/// Uses Dapper for lightweight, high-performance database operations.
/// </summary>
public class SqlExecutionJob(ISqlConnectionFactory connectionFactory) : IAsyncJobWithResult<SqlJobData>
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

        context.LogInformation($"Executing SQL on '{jobData.ConnectionName}' ({_connectionFactory.GetProviderType(jobData.ConnectionName)})");
        context.LogInformation($"Query type: {jobData.QueryType}, Command type: {jobData.CommandType}");

        // 3. Create connection
        using var connection = _connectionFactory.CreateConnection(jobData.ConnectionName);

        try
        {
            await connection.OpenAsync(context.CancellationToken);
            context.LogInformation("Database connection opened");

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
                return await ExecuteWithTransactionAsync(connection, jobData, commandType, timeout, parameters, context);
            }

            return await ExecuteQueryAsync(connection, null, jobData, commandType, timeout, parameters, context);
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

    private static async Task<string> ExecuteWithTransactionAsync(IDbConnection connection,
                                                                  SqlJobData jobData,
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

        using var transaction = connection.BeginTransaction(isolationLevel);
        context.LogInformation($"Transaction started with isolation level: {isolationLevel}");

        try
        {
            var result = await ExecuteQueryAsync(connection, transaction, jobData, commandType, timeout, parameters, context);

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

    private static async Task<string> ExecuteQueryAsync(IDbConnection connection,
                                                        IDbTransaction transaction,
                                                        SqlJobData jobData,
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
            SqlQueryType.Reader => await ExecuteReaderAsync(connection, commandDefinition, jobData.MaxRows, context),
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

    private static async Task<string> ExecuteReaderAsync(IDbConnection connection,
                                                         CommandDefinition command,
                                                         int maxRows,
                                                         IJobContext context)
    {
        IEnumerable<dynamic> results;

        if (maxRows > 0)
        {
            results = (await connection.QueryAsync(command)).Take(maxRows);
        }
        else
        {
            results = await connection.QueryAsync(command);
        }

        var resultList = results.ToList();
        var rowCount = resultList.Count;

        context.LogInformation($"Reader executed: {rowCount} row(s) returned{(maxRows > 0 ? $" (max: {maxRows})" : "")}");

        return JsonSerializer.Serialize(new
        {
            QueryType = "Reader",
            RowCount = rowCount,
            Data = resultList,
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

