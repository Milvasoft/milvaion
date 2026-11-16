using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Workflow runs end to end against real Postgres and RabbitMQ, and the engine's recovery from runs whose step
/// occurrences were left in a state it could not move on from.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class WorkflowEngineRecoveryTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    /// <summary>
    /// The 1.6.0 regression: the status tracker's partial update cleared WorkflowStepId, so the engine never
    /// dispatched the second step. Runs both services and lets the worker side report through RabbitMQ.
    /// </summary>
    [Fact]
    public async Task TwoStepWorkflow_ThroughStatusTracker_ShouldRunBothStepsAndComplete()
    {
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job1 = await SeedScheduledJobAsync($"E2EStep1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"E2EStep2_{Guid.CreateVersion7():N}");
        var (workflow, step1Id, step2Id) = await SeedTwoStepWorkflowAsync(job1.Id, job2.Id);

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var trigger = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand { WorkflowId = workflow.Id, Reason = "E2E" });
        var runId = trigger.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var engine = CreateWorkflowEngineService();
        var tracker = CreateStatusTrackerService();
        StartInBackground(engine, cts.Token);
        StartInBackground(tracker, cts.Token);

        var step1 = await WaitForStepAsync(runId, step1Id, o => o.StepStatus == WorkflowStepStatus.Running, cts.Token);
        step1.Should().NotBeNull("the engine should dispatch the first step");

        await ReportAsync(step1, JobOccurrenceStatus.Running, cts.Token);
        await ReportAsync(step1, JobOccurrenceStatus.Completed, cts.Token, result: "{\"ok\":true}");

        var step2 = await WaitForStepAsync(runId, step2Id, o => o.StepStatus == WorkflowStepStatus.Running, cts.Token);
        step2.Should().NotBeNull("the second step should be dispatched once the first completes");

        // The tracker's write must not have unbound the completed step.
        var completedStep1 = await GetDbContext().JobOccurrences.AsNoTracking().FirstAsync(o => o.Id == step1.Id, cts.Token);
        completedStep1.WorkflowStepId.Should().Be(step1Id);
        completedStep1.StepStatus.Should().Be(WorkflowStepStatus.Completed);

        await ReportAsync(step2, JobOccurrenceStatus.Running, cts.Token);
        await ReportAsync(step2, JobOccurrenceStatus.Completed, cts.Token);

        var completed = await WaitForRunAsync(runId, WorkflowStatus.Completed, cts.Token);

        await engine.StopAsync(CancellationToken.None);
        await tracker.StopAsync(CancellationToken.None);

        completed.Should().BeTrue("the run should complete once both steps completed");

        var occurrences = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.WorkflowRunId == runId).ToListAsync(CancellationToken.None);
        occurrences.Should().HaveCount(2);
        occurrences.Should().OnlyContain(o => o.WorkflowStepId != null && o.StepStatus == WorkflowStepStatus.Completed);
        occurrences.Should().OnlyContain(o => o.JobVersion > 0, "the tracker must not reset columns it does not own");
    }

    [Fact]
    public async Task CompletedStepThatLostItsBinding_ShouldBeRelinkedAndRunShouldContinue()
    {
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"Unbound1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Unbound2_{Guid.CreateVersion7():N}");
        var (workflow, step1Id, step2Id) = await SeedTwoStepWorkflowAsync(job1.Id, job2.Id);

        var runId = await SeedRunningRunAsync(workflow,
                                              (null, job1, WorkflowStepStatus.Completed, JobOccurrenceStatus.Completed),
                                              (step2Id, job2, WorkflowStepStatus.Pending, JobOccurrenceStatus.Queued));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        var step2 = await WaitForStepAsync(runId, step2Id, o => o.StepStatus == WorkflowStepStatus.Running, cts.Token);

        await engine.StopAsync(CancellationToken.None);

        step2.Should().NotBeNull("the engine should relink the completed step and dispatch the next one");

        var relinked = await GetDbContext().JobOccurrences.AsNoTracking().FirstAsync(o => o.WorkflowRunId == runId && o.JobId == job1.Id, CancellationToken.None);
        relinked.WorkflowStepId.Should().Be(step1Id);
    }

    [Fact]
    public async Task UnboundStepThatCannotBeMatched_ShouldFailRunInsteadOfBlockingIt()
    {
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"Ambiguous_{Guid.CreateVersion7():N}");

        // Both steps run the same job, so an unbound occurrence of it cannot be told apart.
        var (workflow, _, _) = await SeedTwoStepWorkflowAsync(job.Id, job.Id);

        var runId = await SeedRunningRunAsync(workflow,
                                              (null, job, WorkflowStepStatus.Completed, JobOccurrenceStatus.Completed),
                                              (null, job, WorkflowStepStatus.Pending, JobOccurrenceStatus.Queued));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        var failed = await WaitForRunAsync(runId, WorkflowStatus.Failed, cts.Token);

        await engine.StopAsync(CancellationToken.None);

        failed.Should().BeTrue();
        (await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId)).Error.Should().Contain("not bound to a step");
    }

    /// <summary>
    /// The zombie detector marks a lost occurrence Unknown. Before 1.6.2 it left the step Running, and the run with it.
    /// </summary>
    [Fact]
    public async Task StepWhoseOccurrenceWasMarkedUnknown_ShouldFailRun()
    {
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"Zombie1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Zombie2_{Guid.CreateVersion7():N}");
        var (workflow, step1Id, step2Id) = await SeedTwoStepWorkflowAsync(job1.Id, job2.Id);

        var runId = await SeedRunningRunAsync(workflow,
                                              (step1Id, job1, WorkflowStepStatus.Running, JobOccurrenceStatus.Unknown),
                                              (step2Id, job2, WorkflowStepStatus.Pending, JobOccurrenceStatus.Queued));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        var failed = await WaitForRunAsync(runId, WorkflowStatus.Failed, cts.Token);

        await engine.StopAsync(CancellationToken.None);

        failed.Should().BeTrue("a step whose occurrence ended Unknown has failed");

        var db = GetDbContext();
        (await db.JobOccurrences.AsNoTracking().FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id)).StepStatus.Should().Be(WorkflowStepStatus.Failed);
        (await db.JobOccurrences.AsNoTracking().FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id)).StepStatus.Should().Be(WorkflowStepStatus.Skipped);
    }

    [Fact]
    public async Task RunWaitingOnACancelledStep_ShouldFailInsteadOfStayingRunning()
    {
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"Deadlock1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Deadlock2_{Guid.CreateVersion7():N}");
        var (workflow, step1Id, step2Id) = await SeedTwoStepWorkflowAsync(job1.Id, job2.Id);

        // The first step's occurrence was cancelled on its own (not through the run), so the second can never start.
        var runId = await SeedRunningRunAsync(workflow,
                                              (step1Id, job1, WorkflowStepStatus.Cancelled, JobOccurrenceStatus.Cancelled),
                                              (step2Id, job2, WorkflowStepStatus.Pending, JobOccurrenceStatus.Queued));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        var failed = await WaitForRunAsync(runId, WorkflowStatus.Failed, cts.Token);

        await engine.StopAsync(CancellationToken.None);

        failed.Should().BeTrue();
        (await GetDbContext().WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == runId)).Error.Should().Contain("No step can run");
    }

    [Fact]
    public async Task RunOfAnOlderVersion_ShouldUseThatVersionsSteps()
    {
        await InitializeAsync();

        var oldJob = await SeedScheduledJobAsync($"OldVersion_{Guid.CreateVersion7():N}");
        var newJob = await SeedScheduledJobAsync($"NewVersion_{Guid.CreateVersion7():N}");

        var oldStepId = Guid.CreateVersion7();
        var newStepId = Guid.CreateVersion7();

        // Version 2 replaced the only step; the run was triggered on version 1.
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = $"Versioned_{Guid.CreateVersion7():N}",
            IsActive = true,
            Version = 2,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps = [new WorkflowStepDefinition { Id = newStepId, StepName = "New", NodeType = WorkflowNodeType.Task, JobId = newJob.Id, Order = 1 }],
                Edges = []
            },
            Versions =
            [
                new WorkflowSnapshot
                {
                    Version = 1,
                    Steps = [new WorkflowStepSnapshot { Id = oldStepId, StepName = "Old", NodeType = WorkflowNodeType.Task, JobId = oldJob.Id, Order = 1 }],
                    Edges = []
                }
            ]
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var runId = await SeedRunningRunAsync(workflow, workflowVersion: 1, (oldStepId, oldJob, WorkflowStepStatus.Pending, JobOccurrenceStatus.Queued));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        var dispatched = await WaitForStepAsync(runId, oldStepId, o => o.StepStatus == WorkflowStepStatus.Running, cts.Token);

        await engine.StopAsync(CancellationToken.None);

        dispatched.Should().NotBeNull("the step of the run's own version should be dispatched");
    }

    /// <summary>
    /// A cron workflow that is far behind fires once for the newest slot, and the slot is stored before the run is
    /// triggered, so the next passes do not trigger it again.
    /// </summary>
    [Fact]
    public async Task CronWorkflowBehindSchedule_ShouldTriggerOnceAndStoreTheSlot()
    {
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"Cron_{Guid.CreateVersion7():N}");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = $"HourlyCron_{Guid.CreateVersion7():N}",
            IsActive = true,
            CronExpression = "0 0 * * * *",
            LastScheduledRunAt = DateTime.UtcNow.AddHours(-5),
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps = [new WorkflowStepDefinition { Id = Guid.CreateVersion7(), StepName = "Hourly", NodeType = WorkflowNodeType.Task, JobId = job.Id, Order = 1 }],
                Edges = []
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = CreateWorkflowEngineService();
        StartInBackground(engine, cts.Token);

        // Several passes at one second each.
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        await engine.StopAsync(CancellationToken.None);

        var db = GetDbContext();
        (await db.WorkflowRuns.AsNoTracking().CountAsync(r => r.WorkflowId == workflow.Id)).Should().Be(1);

        var now = DateTime.UtcNow;
        var expectedSlot = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        (await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == workflow.Id)).LastScheduledRunAt.Should().Be(expectedSlot);
    }

    #region Helpers

    private async Task<(Workflow Workflow, Guid Step1Id, Guid Step2Id)> SeedTwoStepWorkflowAsync(Guid job1Id, Guid job2Id)
    {
        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = $"TwoStep_{Guid.CreateVersion7():N}",
            IsActive = true,
            TimeoutSeconds = 3600,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "First", NodeType = WorkflowNodeType.Task, JobId = job1Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Second", NodeType = WorkflowNodeType.Task, JobId = job2Id, Order = 2 },
                ],
                Edges = [new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        return (workflow, step1Id, step2Id);
    }

    private Task<Guid> SeedRunningRunAsync(Workflow workflow, params (Guid? StepId, ScheduledJob Job, WorkflowStepStatus StepStatus, JobOccurrenceStatus Status)[] steps)
        => SeedRunningRunAsync(workflow, workflow.Version, steps);

    private async Task<Guid> SeedRunningRunAsync(Workflow workflow, int workflowVersion, params (Guid? StepId, ScheduledJob Job, WorkflowStepStatus StepStatus, JobOccurrenceStatus Status)[] steps)
    {
        var now = DateTime.UtcNow;
        var runId = Guid.CreateVersion7();

        var dbContext = GetDbContext();

        dbContext.WorkflowRuns.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflow.Id,
            WorkflowVersion = workflowVersion,
            Status = WorkflowStatus.Running,
            TriggerReason = "Recovery test",
            StartTime = now.AddSeconds(-5),
        });

        foreach (var (stepId, job, stepStatus, status) in steps)
        {
            dbContext.JobOccurrences.Add(new JobOccurrence
            {
                Id = Guid.CreateVersion7(),
                WorkflowRunId = runId,
                WorkflowStepId = stepId,
                JobId = job.Id,
                JobName = job.JobNameInWorker,
                StepStatus = stepStatus,
                Status = status,
                CreatedAt = now,
                EndTime = status is JobOccurrenceStatus.Queued or JobOccurrenceStatus.Running ? null : now,
            });
        }

        await dbContext.SaveChangesAsync();

        return runId;
    }

    private async Task<JobOccurrence> WaitForStepAsync(Guid runId, Guid stepId, Func<JobOccurrence, bool> predicate, CancellationToken cancellationToken)
    {
        JobOccurrence found = null;

        await WaitForConditionAsync(async () =>
        {
            var occ = await GetDbContext().JobOccurrences.AsNoTracking().FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId, cancellationToken);

            if (occ != null && predicate(occ))
                found = occ;

            return found != null;
        }, timeout: TimeSpan.FromSeconds(20), pollInterval: TimeSpan.FromMilliseconds(300), cancellationToken: cancellationToken);

        return found;
    }

    private Task<bool> WaitForRunAsync(Guid runId, WorkflowStatus status, CancellationToken cancellationToken)
        => WaitForConditionAsync(async () => (await GetDbContext().WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken))?.Status == status,
                                 timeout: TimeSpan.FromSeconds(20),
                                 pollInterval: TimeSpan.FromMilliseconds(300),
                                 cancellationToken: cancellationToken);

    private void StartInBackground(Microsoft.Extensions.Hosting.BackgroundService service, CancellationToken cancellationToken)
        => _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _output.WriteLine($"{service.GetType().Name} error: {ex}");
            }
        }, cancellationToken);

    private WorkflowEngineService CreateWorkflowEngineService() => new(_serviceProvider,
                                                                       Options.Create(new WorkflowEngineOptions { Enabled = true, PollingIntervalSeconds = 1 }),
                                                                       _serviceProvider.GetRequiredService<ILoggerFactory>(),
                                                                       null);

    private StatusTrackerService CreateStatusTrackerService() => new(_serviceProvider,
                                                                     _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
                                                                     _serviceProvider.GetRequiredService<IRedisStatsService>(),
                                                                     _serviceProvider.GetRequiredService<IAlertNotifier>(),
                                                                     _serviceProvider.GetRequiredService<IMessageConsumerTransport>(),
                                                                     Options.Create(new StatusTrackerOptions { Enabled = true, BatchSize = 10, BatchIntervalMs = 300 }),
                                                                     Options.Create(new JobAutoDisableOptions { Enabled = false }),
                                                                     _serviceProvider.GetRequiredService<ILoggerFactory>(),
                                                                     _serviceProvider.GetRequiredService<BackgroundServiceMetrics>());

    private async Task ReportAsync(JobOccurrence occurrence, JobOccurrenceStatus status, CancellationToken cancellationToken, string result = null)
    {
        var now = DateTime.UtcNow;

        var message = new JobStatusUpdateMessage
        {
            OccurrenceId = occurrence.Id,
            JobId = occurrence.JobId,
            WorkerId = "e2e-worker",
            InstanceId = "e2e-worker-1",
            Status = status,
            StartTime = status == JobOccurrenceStatus.Running ? now : null,
            EndTime = status == JobOccurrenceStatus.Running ? null : now,
            DurationMs = status == JobOccurrenceStatus.Running ? null : 10,
            Result = result,
        };

        var connectionFactory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(queue: WorkerConstant.Queues.StatusUpdates, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive));

        await channel.BasicPublishAsync(exchange: "", routingKey: WorkerConstant.Queues.StatusUpdates, mandatory: false, basicProperties: new BasicProperties { Persistent = true }, body: body, cancellationToken: cancellationToken);

        // Running and Completed of one occurrence in one batch is applied in order, but give the tracker a beat.
        await Task.Delay(500, cancellationToken);
    }

    #endregion
}
