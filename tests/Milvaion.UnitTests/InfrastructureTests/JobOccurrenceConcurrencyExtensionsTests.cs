using FluentAssertions;
using Milvaion.Infrastructure.Persistence;
using Milvasoft.BulkExtensions;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Unit tests for the column restriction of guarded occurrence bulk updates.
/// </summary>
public class JobOccurrenceConcurrencyExtensionsTests
{
    [Fact]
    public void RestrictToIncludedColumns_WithOnlyIncludeOnUpdate_ShouldCopyItToInclude()
    {
        var config = new BulkConfig();

        JobOccurrenceConcurrencyExtensions.RestrictToIncludedColumns(config, bc => bc.PropertiesToIncludeOnUpdate = ["Status", "StepStatus"]);

        config.PropertiesToInclude.Should().BeEquivalentTo(["Status", "StepStatus"]);
    }

    [Fact]
    public void RestrictToIncludedColumns_WithInclude_ShouldKeepIt()
    {
        var config = new BulkConfig();

        JobOccurrenceConcurrencyExtensions.RestrictToIncludedColumns(config, bc =>
        {
            bc.PropertiesToInclude = ["Status"];
            bc.PropertiesToIncludeOnUpdate = ["Status", "Result"];
        });

        config.PropertiesToInclude.Should().BeEquivalentTo(["Status"]);
    }
}
