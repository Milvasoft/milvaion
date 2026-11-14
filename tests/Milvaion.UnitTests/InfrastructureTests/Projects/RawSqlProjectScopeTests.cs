using FluentAssertions;
using System.Text.RegularExpressions;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

/// <summary>
/// Raw SQL is not covered by the EF project filter. Every file of the server that runs raw SQL is listed here with the
/// reason it is safe, so a new raw query cannot appear without someone deciding how it treats projects.
/// </summary>
[Trait("Category", "Unit")]
public partial class RawSqlProjectScopeTests
{
    /// <summary>
    /// Reviewed files, relative to the repository root, and why their SQL does not need a project condition.
    /// </summary>
    private static readonly Dictionary<string, string> _reviewed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Milvaion.Infrastructure/HealthChecks/DbContextHealthCheck.cs"] = "SELECT 1 health probe, reads no data.",
        ["src/Milvaion.Infrastructure/Persistence/DatabaseMigrator.cs"] = "Schema setup and seeding, runs outside any request.",
        ["src/Milvaion.Infrastructure/Persistence/JobOccurrenceConcurrencyExtensions.cs"] = "Locks occurrence rows by id for background services, which work across projects.",
        ["src/Milvaion.Infrastructure/Services/AdminService.cs"] = "Instance-level database statistics behind SystemAdministration permissions.",
        ["src/Milvaion.Infrastructure/Services/Redis/RedisStatsService.cs"] = "Corrects the instance-wide counters from table estimates, runs as an internal job.",
    };

    [GeneratedRegex(@"SqlQueryRaw|ExecuteSqlRaw|FromSqlRaw|FromSql\(|FromSqlInterpolated|SqlQuery<|SqlQuery\(|ExecuteSql\(|ExecuteSqlInterpolated")]
    private static partial Regex RawSqlCall();

    [Fact]
    public void EveryRawSqlCallSite_HasBeenReviewedForProjects()
    {
        var root = FindRepositoryRoot();

        var found = new[] { "src/Milvaion.Api", "src/Milvaion.Application", "src/Milvaion.Infrastructure" }
                    .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                    .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    .Where(file => RawSqlCall().IsMatch(File.ReadAllText(file)))
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                    .ToList();

        found.Except(_reviewed.Keys, StringComparer.OrdinalIgnoreCase)
             .Should()
             .BeEmpty("raw SQL bypasses the project filter: add a project condition, then list the file in _reviewed with the reason it is safe");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Milvaion.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Milvaion.slnx) not found.");
    }
}
