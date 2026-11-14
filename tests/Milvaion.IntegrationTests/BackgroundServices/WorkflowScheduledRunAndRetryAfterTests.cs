using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Features.Workflows.CancelWorkflow;
using Milvaion.Application.Features.Workflows.RescheduleWorkflowRun;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for workflow runs triggered for a later time (executeAt) and for steps whose job asked to be
/// run again at a given time (retry-after).
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class WorkflowScheduledRunAndRetryAfterTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task ScheduledRun_ShouldStayPendingUntilItsTime_ThenStart()
    {
        await InitializeAsync();

        var workflow = await SeedWorkflowAsync("Scheduled Run Workflow");
        var mediator = _serviceProvider.GetRequiredService<IMediator>();

        var executeAt = DateTime.UtcNow.AddSeconds(8);

        var trigger = await mediator.Send(new TriggerWorkflowCommand { WorkflowId = workflow.Id, Reason = "Scheduled test", ExecuteAt = executeAt });

        trigger.IsSuccess.Should().BeTrue();
        trigger.Data.ScheduledAt.Should().BeCloseTo(executeAt, TimeSpan.FromMilliseconds(1));

        var runId = trigger.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var engine = StartEngine(cts.Token);

        // Well before its time the engine leaves the run alone.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var early = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cts.Token);
        early.Status.Should().Be(WorkflowStatus.Pending);
        early.StartTime.Should().BeNull();
        early.ScheduledAt.Should().NotBeNull();

        var started = await WaitForConditionAsync(async () =>
        {
            var run = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            return run.Status != WorkflowStatus.Pending;
        }, timeout: TimeSpan.FromSeconds(25), pollInterval: TimeSpan.FromMilliseconds(500), cancellationToken: cts.Token);

        await engine.StopAsync(CancellationToken.None);

        started.Should().BeTrue("the run should start once its scheduled time has come");

        var final = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId, CancellationToken.None);
        final.StartTime.Should().NotBeNull();
        final.StartTime!.Value.Should().BeOnOrAfter(executeAt.AddSeconds(-1));
    }

    [Fact]
    public async Task ScheduledRun_CanBeRescheduledAndCancelled_BeforeItStarts()
    {
        await InitializeAsync();

        var workflow = await SeedWorkflowAsync("Reschedule Workflow");
        var mediator = _serviceProvider.GetRequiredService<IMediator>();

        var trigger = await mediator.Send(new TriggerWorkflowCommand { WorkflowId = workflow.Id, ExecuteAt = DateTime.UtcNow.AddHours(1) });
        var runId = trigger.Data.Id;

        var newTime = DateTime.UtcNow.AddHours(3);
        var rescheduled = await mediator.Send(new RescheduleWorkflowRunCommand { WorkflowRunId = runId, ExecuteAt = newTime });

        rescheduled.IsSuccess.Should().BeTrue();

        var run = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        run.ScheduledAt.Should().BeCloseTo(newTime, TimeSpan.FromMilliseconds(1));
        run.Status.Should().Be(WorkflowStatus.Pending);

        var cancelled = await mediator.Send(new CancelWorkflowCommand { WorkflowRunId = runId, Reason = "Post deleted" });
        cancelled.IsSuccess.Should().BeTrue();

        run = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        run.Status.Should().Be(WorkflowStatus.Cancelled);

        var steps = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.WorkflowRunId == runId).ToListAsync();
        steps.Should().NotBeEmpty();
        steps.Should().OnlyContain(o => o.StepStatus == WorkflowStepStatus.Skipped);

        // Cancelling must only touch the status columns, not detach the steps from their run.
        steps.Should().OnlyContain(o => o.CorrelationId != Guid.Empty && o.WorkflowStepId != null);

        // A run that is no longer waiting cannot be moved.
        var again = await mediator.Send(new RescheduleWorkflowRunCommand { WorkflowRunId = runId, ExecuteAt = DateTime.UtcNow.AddHours(4) });
        again.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Reschedule_ShouldReject_RunThatWasNotScheduled()
    {
        await InitializeAsync();

        var workflow = await SeedWorkflowAsync("Immediate Workflow");
        var mediator = _serviceProvider.GetRequiredService<IMediator>();

        var trigger = await mediator.Send(new TriggerWorkflowCommand { WorkflowId = workflow.Id });

        trigger.Data.ScheduledAt.Should().BeNull();

        var result = await mediator.Send(new RescheduleWorkflowRunCommand { WorkflowRunId = trigger.Data.Id, ExecuteAt = DateTime.UtcNow.AddHours(1) });

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task RetryAfter_ShouldDelayStepUntilRequestedTime_WithoutUsingStepRetries()
    {
        await InitializeAsync();

        // MaxStepRetries = 0: an ordinary failure would fail the run straight away.
        var workflow = await SeedWorkflowAsync("Retry After Workflow", maxRetryAfter: 3);
        var retryAt = DateTime.UtcNow.AddHours(2);
        var (runId, stepId) = await TriggerAndFailStepAsync(workflow, retryAt, retryAfterCount: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = StartEngine(cts.Token);

        var delayed = await WaitForConditionAsync(async () =>
        {
            var occ = await GetStepAsync(runId, stepId);
            return occ.StepStatus == WorkflowStepStatus.Delayed;
        }, timeout: TimeSpan.FromSeconds(15), pollInterval: TimeSpan.FromMilliseconds(500), cancellationToken: cts.Token);

        await engine.StopAsync(CancellationToken.None);

        delayed.Should().BeTrue("a step that asked to be retried later should wait instead of failing the run");

        var step = await GetStepAsync(runId, stepId);
        step.StepScheduledAt.Should().BeCloseTo(retryAt, TimeSpan.FromMilliseconds(1));
        step.StepRetryAfterCount.Should().Be(1);
        step.StepRetryCount.Should().Be(0);
        step.RetryAfter.Should().BeNull();

        var run = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        run.Status.Should().Be(WorkflowStatus.Running);
    }

    [Fact]
    public async Task RetryAfter_InThePast_ShouldDispatchStepAgain()
    {
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var workflow = await SeedWorkflowAsync("Retry After Past Workflow", maxRetryAfter: 3);
        var (runId, stepId) = await TriggerAndFailStepAsync(workflow, DateTime.UtcNow.AddSeconds(-1), retryAfterCount: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = StartEngine(cts.Token);

        var dispatched = await WaitForConditionAsync(async () =>
        {
            var occ = await GetStepAsync(runId, stepId);
            return occ.StepStatus == WorkflowStepStatus.Running && occ.Status == JobOccurrenceStatus.Queued;
        }, timeout: TimeSpan.FromSeconds(15), pollInterval: TimeSpan.FromMilliseconds(500), cancellationToken: cts.Token);

        await engine.StopAsync(CancellationToken.None);

        dispatched.Should().BeTrue("a retry time that has already passed should dispatch the step right away");

        var step = await GetStepAsync(runId, stepId);
        step.StepRetryAfterCount.Should().Be(1);
        step.LastHeartbeat.Should().NotBeNull("the dispatch time is what the zombie detector measures a queued step from");
    }

    [Fact]
    public async Task RetryAfter_LimitReached_ShouldHandleFailureNormally()
    {
        await InitializeAsync();

        var workflow = await SeedWorkflowAsync("Retry After Limit Workflow", maxRetryAfter: 2);
        var (runId, stepId) = await TriggerAndFailStepAsync(workflow, DateTime.UtcNow.AddHours(1), retryAfterCount: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = StartEngine(cts.Token);

        var failed = await WaitForConditionAsync(async () =>
        {
            var run = await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            return run.Status == WorkflowStatus.Failed;
        }, timeout: TimeSpan.FromSeconds(15), pollInterval: TimeSpan.FromMilliseconds(500), cancellationToken: cts.Token);

        await engine.StopAsync(CancellationToken.None);

        failed.Should().BeTrue("once MaxRetryAfter is used up the step's failure fails the run as usual");

        var step = await GetStepAsync(runId, stepId);
        step.StepStatus.Should().Be(WorkflowStepStatus.Failed);
        step.Exception.Should().Contain("Retry-after limit reached");
    }

    private async Task<(Guid RunId, Guid StepId)> TriggerAndFailStepAsync(Workflow workflow, DateTime retryAfter, int retryAfterCount)
    {
        var mediator = _serviceProvider.GetRequiredService<IMediator>();
        var trigger = await mediator.Send(new TriggerWorkflowCommand { WorkflowId = workflow.Id, Reason = "Retry-after test" });
        var runId = trigger.Data.Id;
        var stepId = workflow.Definition.Steps.First().Id;

        // What the status tracker leaves behind when the step's job throws a retry-after error.
        var db = GetDbContext();
        var occ = await db.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
        occ.Status = JobOccurrenceStatus.Failed;
        occ.StepStatus = WorkflowStepStatus.Failed;
        occ.Exception = "Quota used up";
        occ.RetryAfter = retryAfter;
        occ.StepRetryAfterCount = retryAfterCount;
        db.JobOccurrences.Update(occ);

        var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
        run.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        db.WorkflowRuns.Update(run);

        await db.SaveChangesAsync();

        return (runId, stepId);
    }

    private async Task<JobOccurrence> GetStepAsync(Guid runId, Guid stepId)
        => await GetDbContext().JobOccurrences.AsNoTracking().FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);

    private WorkflowEngineService StartEngine(CancellationToken cancellationToken)
    {
        var engine = new WorkflowEngineService(_serviceProvider,
                                               Options.Create(new WorkflowEngineOptions { Enabled = true, PollingIntervalSeconds = 1 }),
                                               _serviceProvider.GetRequiredService<ILoggerFactory>(),
                                               null);

        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) { }
        }, cancellationToken);

        return engine;
    }

    private async Task<Workflow> SeedWorkflowAsync(string name, int maxRetryAfter = 10)
    {
        var job = await SeedScheduledJobAsync($"WorkflowJob_{Guid.CreateVersion7():N}");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 0,
            MaxRetryAfter = maxRetryAfter,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition
                    {
                        Id = Guid.CreateVersion7(),
                        StepName = "Publish",
                        NodeType = WorkflowNodeType.Task,
                        JobId = job.Id,
                        Order = 1
                    }
                ],
                Edges = []
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        return workflow;
    }
}
