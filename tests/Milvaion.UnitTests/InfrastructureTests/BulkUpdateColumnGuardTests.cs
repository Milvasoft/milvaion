using FluentAssertions;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Milvasoft.BulkExtensions' BulkUpdate writes <c>PropertiesToInclude</c> only; <c>PropertiesToIncludeOnUpdate</c> is for
/// upserts. A bulk update that names its columns in the latter alone writes every column, and from a partial projection
/// that clears the columns the projection left out (1.6.0: WorkflowStepId of workflow steps).
/// </summary>
[Trait("BE Unit Tests", "Guards against bulk updates that write every column.")]
public class BulkUpdateColumnGuardTests
{
    [Fact]
    public void EveryBulkUpdateThatNamesUpdateColumns_ShouldAlsoSetPropertiesToInclude()
    {
        var srcDirectory = FindSrcDirectory();

        var offenders = Directory.EnumerateFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
                                 .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                             && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                                 .SelectMany(path => File.ReadLines(path).Select((line, index) => (Path: path, Line: line.Trim(), Number: index + 1)))
                                 .Where(l => l.Line.Contains("PropertiesToIncludeOnUpdate =")
                                          && !l.Line.Contains("PropertiesToInclude =")
                                          && !l.Line.StartsWith("//")
                                          && !l.Line.StartsWith("///"))
                                 .Select(l => $"{Path.GetRelativePath(srcDirectory, l.Path)}:{l.Number}: {l.Line}")
                                 .ToList();

        offenders.Should().BeEmpty("a bulk update must set PropertiesToInclude, or it writes every column of the entity");
    }

    private static string FindSrcDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Milvaion.Infrastructure")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the tests run inside the repository");

        return Path.Combine(directory!.FullName, "src");
    }
}
