using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using SqlWorker.Jobs;
using SqlWorker.Options;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlWorker.Services;

/// <summary>
/// Applies a connection's guardrails to a job before it runs.
/// </summary>
/// <remarks>
/// The read-only check is text based and only a first line of defence. PostgreSQL and MySQL also run read-only sessions;
/// SQL Server has no equivalent, so a least-privilege database user remains the actual boundary.
/// </remarks>
public static partial class SqlStatementGuard
{
    private static readonly string[] _readOnlyStatements = ["SELECT", "WITH", "VALUES", "SHOW"];

    /// <summary>
    /// Throws if the job is not allowed on the connection.
    /// </summary>
    public static void EnsureAllowed(SqlJobData jobData, SqlConnectionConfig config)
    {
        if (jobData.CommandType == SqlCommandType.StoredProcedure && !config.AllowStoredProcedures)
            throw new PermanentJobException($"Stored procedures are not allowed on connection '{jobData.ConnectionName}'.");

        if (!config.ReadOnly)
            return;

        if (jobData.CommandType == SqlCommandType.StoredProcedure)
            throw new PermanentJobException($"Connection '{jobData.ConnectionName}' is read-only, and what a stored procedure does cannot be checked.");

        if (jobData.QueryType == SqlQueryType.NonQuery)
            throw new PermanentJobException($"Connection '{jobData.ConnectionName}' is read-only, so QueryType NonQuery is not allowed.");

        var violation = FindReadOnlyViolation(jobData.Query, config.Provider);

        if (violation != null)
            throw new PermanentJobException($"Connection '{jobData.ConnectionName}' is read-only: {violation}");
    }

    /// <summary>
    /// The row limit in effect: the smaller of what the job asks for and the connection's hard limit, where 0 means no limit.
    /// </summary>
    public static int GetEffectiveMaxRows(int jobMaxRows, int hardLimit)
    {
        if (hardLimit <= 0)
            return jobMaxRows;

        return jobMaxRows > 0 ? Math.Min(jobMaxRows, hardLimit) : hardLimit;
    }

    /// <summary>
    /// Returns why the SQL is not a single read-only statement, or null if it is.
    /// </summary>
    public static string FindReadOnlyViolation(string sql, SqlProviderType provider)
    {
        var code = StripLiteralsAndComments(sql ?? string.Empty, provider).Trim().TrimEnd(';').TrimEnd();

        if (code.Length == 0)
            return "the query is empty.";

        if (code.Contains(';'))
            return "only a single statement is allowed.";

        var firstWord = FirstWordRegex().Match(code).Value;

        if (!_readOnlyStatements.Contains(firstWord, StringComparer.OrdinalIgnoreCase))
            return $"'{firstWord}' statements are not allowed.";

        var forbidden = ForbiddenKeywordRegex().Match(code);

        return forbidden.Success ? $"'{forbidden.Value}' is not allowed." : null;
    }

    /// <summary>
    /// Blanks out strings, quoted identifiers and comments so that only SQL keywords are inspected.
    /// </summary>
    private static string StripLiteralsAndComments(string sql, SqlProviderType provider)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                i = end < 0 ? sql.Length : end + 2;
                result.Append(' ');
            }
            else if (c is '\'' or '"' or '`' || (c == '[' && provider == SqlProviderType.SqlServer))
            {
                i = SkipQuoted(sql, i, c == '[' ? ']' : c, provider);
                result.Append(" '' ");
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static int SkipQuoted(string sql, int start, char close, SqlProviderType provider)
    {
        var i = start + 1;

        while (i < sql.Length)
        {
            // MySQL also escapes with a backslash; reading that differently would let SQL hide inside a "string"
            if (provider == SqlProviderType.MySql && sql[i] == '\\')
            {
                i += 2;
            }
            else if (sql[i] != close)
            {
                i++;
            }
            else if (i + 1 < sql.Length && sql[i + 1] == close)
            {
                i += 2;
            }
            else
            {
                return i + 1;
            }
        }

        return sql.Length;
    }

    [GeneratedRegex(@"^\w*")]
    private static partial Regex FirstWordRegex();

    // INTO covers SELECT ... INTO, which creates a table on SQL Server and PostgreSQL. A WITH may wrap the data-modifying ones.
    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|UPSERT|DROP|ALTER|CREATE|TRUNCATE|GRANT|REVOKE|EXEC|EXECUTE|INTO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenKeywordRegex();
}
