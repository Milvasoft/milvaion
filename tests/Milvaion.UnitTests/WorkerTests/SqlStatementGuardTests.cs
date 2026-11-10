using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using SqlWorker.Jobs;
using SqlWorker.Options;
using SqlWorker.Services;

namespace Milvaion.UnitTests.WorkerTests;

[Trait("Worker Unit Tests", "SqlWorker statement guard.")]
public class SqlStatementGuardTests
{
    private static SqlJobData Job(string query = "SELECT 1", SqlQueryType queryType = SqlQueryType.Reader, SqlCommandType commandType = SqlCommandType.Text)
        => new() { ConnectionName = "Db", Query = query, QueryType = queryType, CommandType = commandType };

    [Theory]
    [InlineData("DELETE FROM users")]
    [InlineData("DROP TABLE users; SELECT 1")]
    public void Defaults_ShouldAllowAnything(string query)
        => FluentActions.Invoking(() => SqlStatementGuard.EnsureAllowed(Job(query, SqlQueryType.NonQuery), new SqlConnectionConfig())).Should().NotThrow();

    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("  select 1;  ")]
    [InlineData("WITH a AS (SELECT 1) SELECT * FROM a")]
    [InlineData("SELECT 'DELETE; DROP' AS text")]
    [InlineData("SELECT \"update\" FROM t -- DELETE\n")]
    [InlineData("/* DROP */ SELECT REPLACE(name, 'a', 'b') FROM t")]
    public void ReadOnly_ShouldAcceptSingleReads(string sql)
        => SqlStatementGuard.FindReadOnlyViolation(sql, SqlProviderType.PostgreSql).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("UPDATE users SET a = 1")]
    [InlineData("SELECT 1; DELETE FROM users")]
    [InlineData("WITH d AS (DELETE FROM users RETURNING *) SELECT * FROM d")]
    [InlineData("SELECT * INTO copy FROM users")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM users")]
    [InlineData("SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE")]
    public void ReadOnly_ShouldRejectEverythingElse(string sql)
        => SqlStatementGuard.FindReadOnlyViolation(sql, SqlProviderType.PostgreSql).Should().NotBeNull();

    [Fact]
    public void ReadOnly_ShouldNotBeFooledByMySqlBackslashEscape()
        => SqlStatementGuard.FindReadOnlyViolation(@"SELECT '\'' ; DROP TABLE users; -- '", SqlProviderType.MySql).Should().NotBeNull();

    [Fact]
    public void EnsureAllowed_ShouldRefuseNonQuery_OnReadOnlyConnection()
        => FluentActions.Invoking(() => SqlStatementGuard.EnsureAllowed(Job(queryType: SqlQueryType.NonQuery), new SqlConnectionConfig { ReadOnly = true })).Should().Throw<PermanentJobException>();

    [Fact]
    public void EnsureAllowed_ShouldRefuseStoredProcedure_WhenDisallowed()
        => FluentActions.Invoking(() => SqlStatementGuard.EnsureAllowed(Job("sp_x", commandType: SqlCommandType.StoredProcedure), new SqlConnectionConfig { AllowStoredProcedures = false })).Should().Throw<PermanentJobException>();

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 0, 10)]
    [InlineData(0, 100, 100)]
    [InlineData(10, 100, 10)]
    [InlineData(500, 100, 100)]
    public void GetEffectiveMaxRows_ShouldTakeTheStricterLimit(int jobMaxRows, int hardLimit, int expected)
        => SqlStatementGuard.GetEffectiveMaxRows(jobMaxRows, hardLimit).Should().Be(expected);
}
