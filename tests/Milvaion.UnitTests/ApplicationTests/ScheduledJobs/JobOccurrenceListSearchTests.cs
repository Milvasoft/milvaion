using FluentAssertions;
using Milvaion.Application.Features.ScheduledJobs;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.Request;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Moq;

namespace Milvaion.UnitTests.ApplicationTests.ScheduledJobs;

public class JobOccurrenceListSearchTests
{
    private readonly Mock<IRedisStatsService> _stats = new();

    public JobOccurrenceListSearchTests()
        => _stats.Setup(s => s.GetStatisticsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Dictionary<string, long> { ["Total"] = 100, ["Failed"] = 7 });

    [Fact]
    public async Task GetCountFromStatsAsync_WithoutFilters_ShouldUseTotal()
        => (await Count(null, null)).Should().Be(100);

    [Fact]
    public async Task GetCountFromStatsAsync_WithOnlyStatusEquality_ShouldUseStatusCounter()
        => (await Count(Filter(Criteria(nameof(JobOccurrence.Status), JobOccurrenceStatus.Failed)), null)).Should().Be(7);

    [Fact]
    public async Task GetCountFromStatsAsync_WithSearchTerm_ShouldLeaveCountToTheDatabase()
        => (await Count(Filter(Criteria(nameof(JobOccurrence.Status), JobOccurrenceStatus.Failed)), "term")).Should().BeNull();

    [Fact]
    public async Task GetCountFromStatsAsync_WithOtherFilter_ShouldLeaveCountToTheDatabase()
        => (await Count(Filter(Criteria(nameof(JobOccurrence.WorkerId), "worker")), null)).Should().BeNull();

    [Fact]
    public async Task GetCountFromStatsAsync_WithStatusAndOtherFilter_ShouldLeaveCountToTheDatabase()
        => (await Count(Filter(Criteria(nameof(JobOccurrence.Status), JobOccurrenceStatus.Failed), Criteria(nameof(JobOccurrence.WorkerId), "worker")), null)).Should().BeNull();

    [Fact]
    public async Task GetCountFromStatsAsync_WithNonEqualityStatusFilter_ShouldLeaveCountToTheDatabase()
    {
        var criteria = Criteria(nameof(JobOccurrence.Status), JobOccurrenceStatus.Failed);
        criteria.Type = FilterType.GreaterThan;

        (await Count(Filter(criteria), null)).Should().BeNull();
    }

    private Task<int?> Count(FilterRequest filtering, string searchTerm)
        => JobOccurrenceListSearch.GetCountFromStatsAsync(filtering, searchTerm, _stats.Object, CancellationToken.None);

    private static FilterCriteria Criteria(string field, object value) => new() { FilterBy = field, Value = value, Type = FilterType.EqualTo };

    private static FilterRequest Filter(params FilterCriteria[] criterias) => new() { Criterias = [.. criterias] };
}
