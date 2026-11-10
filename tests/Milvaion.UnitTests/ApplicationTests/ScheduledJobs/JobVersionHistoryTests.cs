using FluentAssertions;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvasoft.Milvaion.Sdk.Domain;
using System.Text.Json;

namespace Milvaion.UnitTests.ApplicationTests.ScheduledJobs;

public class JobVersionHistoryTests
{
    [Fact]
    public void Append_ShouldNotEmbedEarlierSnapshots()
    {
        var job = new ScheduledJob { Id = Guid.CreateVersion7(), DisplayName = "Job", JobVersions = [] };

        for (var i = 0; i < 30; i++)
            job.JobVersions = JobVersionHistory.Append(job.JobVersions, job);

        job.JobVersions.Should().HaveCount(30);
        job.JobVersions.Should().OnlyContain(v => !v.Contains(nameof(ScheduledJob.JobVersions)));
        job.JobVersions.Max(v => v.Length).Should().Be(job.JobVersions.Min(v => v.Length));
    }

    [Fact]
    public void Append_ShouldKeepTheSnapshotFieldsReadByTheUi()
    {
        var job = new ScheduledJob { Id = Guid.CreateVersion7(), DisplayName = "Job", CronExpression = "0 * * * * *", JobData = "{}" };

        using var snapshot = JsonDocument.Parse(JobVersionHistory.Append([], job)[0]);

        snapshot.RootElement.GetProperty(nameof(ScheduledJob.DisplayName)).GetString().Should().Be("Job");
        snapshot.RootElement.GetProperty(nameof(ScheduledJob.CronExpression)).GetString().Should().Be("0 * * * * *");
    }

    [Fact]
    public void Append_ShouldCompactSnapshotsThatAlreadyEmbedHistory()
    {
        var bloated = JsonSerializer.Serialize(new { DisplayName = "Old", JobVersions = new[] { new string('x', 10_000) } });

        var history = JobVersionHistory.Append([bloated], new ScheduledJob { DisplayName = "New" });

        history[0].Should().NotContain(nameof(ScheduledJob.JobVersions)).And.Contain("Old");
    }

    [Fact]
    public void Append_ShouldLeaveDamagedSnapshotsAlone()
    {
        const string damaged = "{\"JobVersions\": [";

        JobVersionHistory.Append([damaged], new ScheduledJob())[0].Should().Be(damaged);
    }

    [Fact]
    public void Append_ShouldTolerateMissingHistory()
        => JobVersionHistory.Append(null, new ScheduledJob()).Should().HaveCount(1);
}
