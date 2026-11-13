using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <summary>
/// Milvasoft 11 <c>MilvaNormalize</c> is culture-invariant and folds 'ı'/'İ' to 'I'. Values written by the old version under a
/// Turkish culture can still contain those letters, so they would no longer match freshly normalized input (user name checks,
/// permission sync). This rewrites only the affected rows and skips a row when the fixed value already exists on another row.
/// </summary>
public partial class FixTurkishNormalization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FixColumnSql("Users", "NormalizedUserName"));
        migrationBuilder.Sql(FixColumnSql("Users", "NormalizedEmail"));
        migrationBuilder.Sql(FixColumnSql("Permissions", "NormalizedName"));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty: the old values were inconsistent normalizations and cannot be restored.
    }

    private static string FixColumnSql(string table, string column) => $"""
        UPDATE "{table}" u
        SET "{column}" = REPLACE(REPLACE(u."{column}", 'ı', 'I'), 'İ', 'I')
        WHERE u."{column}" ~ '[ıİ]'
          AND NOT EXISTS (
              SELECT 1 FROM "{table}" x
              WHERE x."{column}" = REPLACE(REPLACE(u."{column}", 'ı', 'I'), 'İ', 'I')
                AND x."Id" <> u."Id");
        """;
}
