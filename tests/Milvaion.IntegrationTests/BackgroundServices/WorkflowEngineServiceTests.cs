using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for WorkflowEngineService.
/// Tests workflow execution, step scheduling, dependency resolution, and failure handling.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class WorkflowEngineServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task WorkflowEngine_ShouldStartPendingWorkflowRun()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var workflow = await SeedWorkflowWithSingleStepAsync("Test Workflow");

        // Trigger workflow using the command handler to properly create step occurrences
        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Test run"
        });

        var runId = triggerResult.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();

        var engineTask = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _output.WriteLine("Workflow engine cancelled");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Workflow engine error: {ex}");
                throw;
            }
        }, cts.Token);

        // Wait for run to start (it may complete quickly, so accept Running or Completed)
        var started = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var updatedRun = await dbContext.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Running;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await cts.CancelAsync();
        await engine.StopAsync(CancellationToken.None);

        // Check if engine task had exception
        if (engineTask.IsFaulted)
        {
            throw engineTask.Exception!.GetBaseException();
        }

        // Assert
        started.Should().BeTrue("workflow run should transition from Pending to Running/Completed");

        var dbContextAssert = GetDbContext();
        var finalRun = await dbContextAssert.WorkflowRuns.FindAsync(runId);
        finalRun.Should().NotBeNull();
        finalRun!.Status.Should().BeOneOf(WorkflowStatus.Running, WorkflowStatus.Completed);
        finalRun.StartTime.Should().NotBeNull("workflow should have a start time");
    }

    [Fact]
    public async Task WorkflowEngine_ShouldDispatchReadySteps()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"WorkflowJob_{Guid.CreateVersion7():N}");
        var workflow = await SeedWorkflowWithStepsAsync("Multi-Step Workflow", [job.Id]);

        // Trigger workflow using the command handler to properly create step occurrences
        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Test run"
        });

        var runId = triggerResult.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for step occurrence to be dispatched (Status changed to Queued)
        var dispatched = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occurrences = await dbContext.JobOccurrences
                    .Where(o => o.WorkflowRunId == runId)
                    .ToListAsync(cts.Token);
                return occurrences.Count > 0 && occurrences.Any(o => o.Status == JobOccurrenceStatus.Queued);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        dispatched.Should().BeTrue("workflow engine should dispatch ready steps");

        var dbContextAssert = GetDbContext();
        var stepOccurrences = await dbContextAssert.JobOccurrences
            .Where(o => o.WorkflowRunId == runId)
            .ToListAsync(cts.Token);

        stepOccurrences.Should().NotBeEmpty();
        stepOccurrences.Should().Contain(o => o.Status == JobOccurrenceStatus.Queued);
    }

    [Fact]
    public async Task WorkflowEngine_ShouldHandleCronScheduledWorkflows()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"CronJob_{Guid.CreateVersion7():N}");

        // Create workflow with cron that should have already executed
        var workflow = await SeedWorkflowAsync("Cron Workflow", isActive: true, cronExpression: "0 0 * * * *"); // Every hour
        workflow.LastScheduledRunAt = DateTime.UtcNow.AddHours(-2); // Last run was 2 hours ago

        var dbContext = GetDbContext();
        dbContext.Workflows.Update(workflow);
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for workflow run to be created by cron trigger
        var triggered = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var runs = await db.WorkflowRuns
                    .Where(r => r.WorkflowId == workflow.Id)
                    .ToListAsync(cts.Token);
                return runs.Count > 0;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        triggered.Should().BeTrue("workflow should be triggered by cron schedule");

        var dbContextAssert = GetDbContext();
        var cronRuns = await dbContextAssert.WorkflowRuns
            .Where(r => r.WorkflowId == workflow.Id)
            .ToListAsync(cts.Token);

        cronRuns.Should().NotBeEmpty();
        cronRuns.Should().Contain(r => r.TriggerReason == "Cron schedule");
    }

    [Fact]
    public async Task WorkflowEngine_ShouldRespectStepDependencies()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"Step1Job_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Step2Job_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Dependency Test Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition
                    {
                        Id = step1Id,
                        StepName = "Step 1",
                        NodeType = WorkflowNodeType.Task,
                        JobId = job1.Id,
                        Order = 1
                    },
                    new WorkflowStepDefinition
                    {
                        Id = step2Id,
                        StepName = "Step 2 (depends on Step 1)",
                        NodeType = WorkflowNodeType.Task,
                        JobId = job2.Id,
                        Order = 2
                    }
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition
                    {
                        SourceStepId = step1Id,
                        TargetStepId = step2Id,
                        Order = 1
                    }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        // Trigger workflow using the command handler to properly create step occurrences
        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Test run"
        });

        var runId = triggerResult.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for first step to be dispatched
        var step1Dispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var occurrences = await db.JobOccurrences
                    .Where(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id)
                    .ToListAsync(cts.Token);
                return occurrences.Count > 0 && occurrences.Any(o => o.Status == JobOccurrenceStatus.Queued);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert - Step 1 should be dispatched, but Step 2 should not (because Step 1 is not completed)
        step1Dispatched.Should().BeTrue("first step should be dispatched");

        var dbContextAssert = GetDbContext();
        var step1Occurrences = await dbContextAssert.JobOccurrences
            .Where(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id)
            .ToListAsync(cts.Token);

        var step2Occurrences = await dbContextAssert.JobOccurrences
            .Where(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id)
            .ToListAsync(cts.Token);

        step1Occurrences.Should().NotBeEmpty("Step 1 should be dispatched");
        step1Occurrences.Should().Contain(o => o.Status == JobOccurrenceStatus.Queued, "Step 1 should be queued");
        step2Occurrences.Should().OnlyContain(o => o.StepStatus == WorkflowStepStatus.Pending, "Step 2 should remain pending until Step 1 completes");
    }

    [Fact]
    public async Task WorkflowEngine_ShouldHandleWorkflowTimeout()
    {
        // Arrange
        await InitializeAsync();

        var workflow = await SeedWorkflowWithSingleStepAsync("Timeout Test Workflow", timeoutSeconds: 5);
        var run = await SeedWorkflowRunAsync(workflow.Id, WorkflowStatus.Running, startTime: DateTime.UtcNow.AddSeconds(-10)); // Started 10 seconds ago

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for run to be cancelled due to timeout
        var timedOut = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var updatedRun = await dbContext.WorkflowRuns.FindAsync(run.Id);
                return updatedRun?.Status == WorkflowStatus.Cancelled;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        timedOut.Should().BeTrue("workflow should be cancelled when timeout is exceeded");

        var dbContextAssert = GetDbContext();
        var finalRun = await dbContextAssert.WorkflowRuns.FindAsync(run.Id);
        finalRun.Should().NotBeNull();
        finalRun!.Status.Should().Be(WorkflowStatus.Cancelled);
        finalRun.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task WorkflowEngine_WithStopOnFirstFailure_ShouldStopWorkflow()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"FailingJob_{Guid.CreateVersion7():N}");
        var workflow = await SeedWorkflowWithStepsAsync("Failure Test Workflow", [job.Id], failureStrategy: WorkflowFailureStrategy.StopOnFirstFailure);
        var run = await SeedWorkflowRunAsync(workflow.Id, WorkflowStatus.Running);

        // Create a failed step occurrence
        var stepId = workflow.Definition.Steps.First().Id;
        var failedOccurrence = new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            JobId = job.Id,
            WorkflowRunId = run.Id,
            WorkflowStepId = stepId,
            StepStatus = WorkflowStepStatus.Failed,
            StepRetryCount = 0,
            Status = JobOccurrenceStatus.Failed,
            Exception = "Test failure",
            JobName = job.JobNameInWorker
        };

        var dbContext = GetDbContext();
        dbContext.JobOccurrences.Add(failedOccurrence);
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start workflow engine
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for workflow to fail
        var failed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(run.Id);
                return updatedRun?.Status == WorkflowStatus.Failed;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        failed.Should().BeTrue("workflow should fail when a step fails with StopOnFirstFailure strategy");

        var dbContextAssert = GetDbContext();
        var finalRun = await dbContextAssert.WorkflowRuns.FindAsync(run.Id);
        finalRun.Should().NotBeNull();
        finalRun!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task WorkflowEngine_WithRetryThenStop_ShouldRetryFailedStepThenFail()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"RetryJob_{Guid.CreateVersion7():N}");
        var workflow = await SeedWorkflowWithStepsAsync("Retry Then Stop Workflow", [job.Id], failureStrategy: WorkflowFailureStrategy.StopOnFirstFailure, maxStepRetries: 2);

        // Trigger workflow to create step occurrences properly
        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Retry test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate first failure (retry count 0, max retries 2 → should be retried)
        var stepId = workflow.Definition.Steps.First().Id;
        var dbCtx = GetDbContext();
        var occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
        occ.StepStatus = WorkflowStepStatus.Failed;
        occ.StepRetryCount = 0;
        occ.Status = JobOccurrenceStatus.Failed;
        occ.Exception = "Transient error";
        dbCtx.JobOccurrences.Update(occ);

        // Set run to Running so engine processes it
        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for retry (step should be reset to Pending with incremented retry count)
        var retried = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedOcc = await db.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
                return updatedOcc.StepRetryCount >= 1 && updatedOcc.StepStatus is WorkflowStepStatus.Pending or WorkflowStepStatus.Running;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        retried.Should().BeTrue("step should be retried when retry count < maxRetries");

        var dbAssert = GetDbContext();
        var finalOcc = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
        finalOcc.StepRetryCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task WorkflowEngine_WithRetryExhausted_ShouldFailWorkflow()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"ExhaustedJob_{Guid.CreateVersion7():N}");
        var workflow = await SeedWorkflowWithStepsAsync("Retry Exhausted Workflow", [job.Id], failureStrategy: WorkflowFailureStrategy.StopOnFirstFailure, maxStepRetries: 1);

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Exhaust retries"
        });

        var runId = triggerResult.Data.Id;
        var stepId = workflow.Definition.Steps.First().Id;

        // Simulate failure with retries already exhausted
        var dbCtx = GetDbContext();
        var occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
        occ.StepStatus = WorkflowStepStatus.Failed;
        occ.StepRetryCount = 1; // Already retried once, max is 1 → exhausted
        occ.Status = JobOccurrenceStatus.Failed;
        occ.Exception = "Permanent error";
        dbCtx.JobOccurrences.Update(occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var failed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Failed;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        failed.Should().BeTrue("workflow should fail when step retries are exhausted");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.Failed);
        finalRun.Error.Should().NotBeNullOrEmpty();
        finalRun.EndTime.Should().NotBeNull();
        finalRun.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WorkflowEngine_WithContinueOnFailure_ShouldContinueAfterStepFails()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"FailJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"NextJob_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "ContinueOnFailure Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.ContinueOnFailure,
            MaxStepRetries = 0,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Failing Step", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Continuation Step", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Continue test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate step 1 failure
        var dbCtx = GetDbContext();
        var step1Occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus = WorkflowStepStatus.Failed;
        step1Occ.Status = JobOccurrenceStatus.Failed;
        step1Occ.Exception = "Step 1 failed";
        dbCtx.JobOccurrences.Update(step1Occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for step 2 to be dispatched despite step 1 failure
        var step2Dispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var step2Occ = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
                return step2Occ?.StepStatus == WorkflowStepStatus.Running || step2Occ?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        step2Dispatched.Should().BeTrue("step 2 should be dispatched even though step 1 failed (ContinueOnFailure strategy)");
    }

    [Fact]
    public async Task WorkflowEngine_WithConditionNode_ShouldRouteToCorrectBranch()
    {
        // Arrange
        await InitializeAsync();

        var sourceJob = await SeedScheduledJobAsync($"SourceJob_{Guid.CreateVersion7():N}");
        var trueJob = await SeedScheduledJobAsync($"TrueJob_{Guid.CreateVersion7():N}");
        var falseJob = await SeedScheduledJobAsync($"FalseJob_{Guid.CreateVersion7():N}");

        var sourceStepId = Guid.CreateVersion7();
        var conditionStepId = Guid.CreateVersion7();
        var trueStepId = Guid.CreateVersion7();
        var falseStepId = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Condition Routing Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = sourceStepId, StepName = "Source", NodeType = WorkflowNodeType.Task, JobId = sourceJob.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = conditionStepId, StepName = "Check Result", NodeType = WorkflowNodeType.Condition, NodeConfigJson = @"{""expression"": ""@status == 'Completed'""}", Order = 2 },
                    new WorkflowStepDefinition { Id = trueStepId, StepName = "True Branch", NodeType = WorkflowNodeType.Task, JobId = trueJob.Id, Order = 3 },
                    new WorkflowStepDefinition { Id = falseStepId, StepName = "False Branch", NodeType = WorkflowNodeType.Task, JobId = falseJob.Id, Order = 4 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = sourceStepId, TargetStepId = conditionStepId, Order = 1 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = trueStepId, SourcePort = "true", Order = 2 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = falseStepId, SourcePort = "false", Order = 3 },
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Condition test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate source step completed successfully
        var dbCtx = GetDbContext();
        var sourceOcc = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == sourceStepId);
        sourceOcc.StepStatus = WorkflowStepStatus.Completed;
        sourceOcc.Status = JobOccurrenceStatus.Completed;
        sourceOcc.Result = @"{""status"": ""ok""}";
        dbCtx.JobOccurrences.Update(sourceOcc);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for condition to be evaluated and true branch dispatched
        var trueDispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var trueOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == trueStepId);
                return trueOcc?.StepStatus == WorkflowStepStatus.Running || trueOcc?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        // Also wait for false branch to be skipped
        var falseSkipped = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var falseOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == falseStepId);
                return falseOcc?.StepStatus == WorkflowStepStatus.Skipped;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        trueDispatched.Should().BeTrue("true branch should be dispatched when condition evaluates to true");
        falseSkipped.Should().BeTrue("false branch should be skipped when condition evaluates to true");
    }

    [Fact]
    public async Task WorkflowEngine_WithMergeNode_ShouldWaitForAllBranches()
    {
        // Arrange
        await InitializeAsync();

        var jobA = await SeedScheduledJobAsync($"BranchA_{Guid.CreateVersion7():N}");
        var jobB = await SeedScheduledJobAsync($"BranchB_{Guid.CreateVersion7():N}");
        var jobAfterMerge = await SeedScheduledJobAsync($"AfterMerge_{Guid.CreateVersion7():N}");

        var branchAId = Guid.CreateVersion7();
        var branchBId = Guid.CreateVersion7();
        var mergeId = Guid.CreateVersion7();
        var afterMergeId = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Merge Node Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = branchAId, StepName = "Branch A", NodeType = WorkflowNodeType.Task, JobId = jobA.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = branchBId, StepName = "Branch B", NodeType = WorkflowNodeType.Task, JobId = jobB.Id, Order = 2 },
                    new WorkflowStepDefinition { Id = mergeId, StepName = "Merge", NodeType = WorkflowNodeType.Merge, Order = 3 },
                    new WorkflowStepDefinition { Id = afterMergeId, StepName = "After Merge", NodeType = WorkflowNodeType.Task, JobId = jobAfterMerge.Id, Order = 4 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = branchAId, TargetStepId = mergeId, Order = 1 },
                    new WorkflowEdgeDefinition { SourceStepId = branchBId, TargetStepId = mergeId, Order = 2 },
                    new WorkflowEdgeDefinition { SourceStepId = mergeId, TargetStepId = afterMergeId, Order = 3 },
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Merge test"
        });

        var runId = triggerResult.Data.Id;

        // Only complete branch A, leave branch B pending → after merge should NOT be dispatched
        var dbCtx = GetDbContext();
        var branchAOcc = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == branchAId);
        branchAOcc.StepStatus = WorkflowStepStatus.Completed;
        branchAOcc.Status = JobOccurrenceStatus.Completed;
        dbCtx.JobOccurrences.Update(branchAOcc);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - first pass: only branch A complete
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Give engine a few iterations
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        // Verify after-merge is NOT dispatched yet (merge waits for all branches)
        var dbCheck = GetDbContext();
        var afterMergeOcc = await dbCheck.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == afterMergeId);
        afterMergeOcc?.StepStatus.Should().Be(WorkflowStepStatus.Pending, "After Merge should not dispatch until all branches complete");

        // Now complete branch B
        var dbCtx2 = GetDbContext();
        var branchBOcc = await dbCtx2.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == branchBId);
        branchBOcc.StepStatus = WorkflowStepStatus.Completed;
        branchBOcc.Status = JobOccurrenceStatus.Completed;
        dbCtx2.JobOccurrences.Update(branchBOcc);
        await dbCtx2.SaveChangesAsync();

        // Wait for after-merge to be dispatched
        var afterMergeDispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var occ = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == afterMergeId);
                return occ?.StepStatus == WorkflowStepStatus.Running || occ?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        afterMergeDispatched.Should().BeTrue("after-merge step should be dispatched once both branches complete");
    }

    [Fact]
    public async Task WorkflowEngine_WithDelayedStep_ShouldWaitBeforeDispatching()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"DelayedJob_{Guid.CreateVersion7():N}");

        var stepId = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Delayed Step Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition
                    {
                        Id = stepId,
                        StepName = "Delayed Step",
                        NodeType = WorkflowNodeType.Task,
                        JobId = job.Id,
                        Order = 1,
                        DelaySeconds = 3
                    }
                ],
                Edges = []
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Delay test"
        });

        var runId = triggerResult.Data.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // First, step should transition to Delayed with a scheduled time
        var delayed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var occ = await db.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
                return occ.StepStatus == WorkflowStepStatus.Delayed && occ.StepScheduledAt.HasValue;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        delayed.Should().BeTrue("step should be in Delayed status with a scheduled time");

        // Then wait for it to be dispatched after the delay
        var dispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var occ = await db.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
                return occ.StepStatus == WorkflowStepStatus.Running || occ.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        dispatched.Should().BeTrue("delayed step should eventually be dispatched after the delay period");
    }

    [Fact]
    public async Task WorkflowEngine_AllStepsComplete_ShouldMarkRunAsCompleted()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"CompleteJob_{Guid.CreateVersion7():N}");
        var workflow = await SeedWorkflowWithStepsAsync("Completion Workflow", [job.Id]);

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Completion test"
        });

        var runId = triggerResult.Data.Id;
        var stepId = workflow.Definition.Steps.First().Id;

        // Simulate step completed
        var dbCtx = GetDbContext();
        var occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == stepId);
        occ.StepStatus = WorkflowStepStatus.Completed;
        occ.Status = JobOccurrenceStatus.Completed;
        occ.EndTime = DateTime.UtcNow;
        dbCtx.JobOccurrences.Update(occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow.AddSeconds(-5);
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var completed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Completed;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        completed.Should().BeTrue("workflow run should be Completed when all steps complete");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.Completed);
        finalRun.EndTime.Should().NotBeNull();
        finalRun.DurationMs.Should().BeGreaterThan(0);
        finalRun.Error.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowEngine_WithSkippedAndCompletedSteps_ShouldMarkRunAsPartiallyCompleted()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"Job1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Job2_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Partial Complete Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.ContinueOnFailure,
            MaxStepRetries = 0,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Step 1", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Step 2", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Partial complete test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate step 1 failed, step 2 completed (ContinueOnFailure allows this)
        var dbCtx = GetDbContext();
        var step1Occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus = WorkflowStepStatus.Failed;
        step1Occ.Status = JobOccurrenceStatus.Failed;
        step1Occ.Exception = "Step 1 failed";
        dbCtx.JobOccurrences.Update(step1Occ);

        var step2Occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
        step2Occ.StepStatus = WorkflowStepStatus.Completed;
        step2Occ.Status = JobOccurrenceStatus.Completed;
        dbCtx.JobOccurrences.Update(step2Occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow.AddSeconds(-5);
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var partiallyCompleted = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.PartiallyCompleted;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        partiallyCompleted.Should().BeTrue("workflow should be PartiallyCompleted when some steps fail and others complete");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.PartiallyCompleted);
        finalRun.Error.Should().Contain("failed");
    }

#pragma warning disable xUnit1004 // Test methods should not be skipped
    [Fact(Skip = "CI flaky test")]
#pragma warning restore xUnit1004 // Test methods should not be skipped
    public async Task WorkflowEngine_Disabled_ShouldNotProcess()
    {
        // Arrange
        await InitializeAsync();

        // Act
        // This ensures any leaked engines from prior tests have completed their last iteration before the Pending run exists in the database.
        var disabledEngine = new WorkflowEngineService(
            _serviceProvider,
            Options.Create(new WorkflowEngineOptions
            {
                Enabled = false,
                PollingIntervalSeconds = 1,
            }),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await disabledEngine.StartAsync(cts.Token);

        // Wait until any running workflow runs from prior tests have fully settled
        await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                return !await db.WorkflowRuns.AnyAsync(r => r.Status == WorkflowStatus.Running);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        // Now create the workflow run — no other enabled engine should be polling at this point
        var workflow = await SeedWorkflowWithSingleStepAsync("Disabled Engine Workflow");

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Disabled engine test"
        });

        var runId = triggerResult.Data.Id;

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        await disabledEngine.StopAsync(CancellationToken.None);

        // Assert - Run should still be Pending (engine didn't process it)
        var dbContext = GetDbContext();
        var finalRun = await dbContext.WorkflowRuns.FindAsync(runId);
        finalRun.Should().NotBeNull();
        finalRun!.Status.Should().Be(WorkflowStatus.Pending, "disabled engine should not process workflow runs");
    }

    [Fact]
    public async Task WorkflowEngine_WithStopOnFirstFailure_ShouldSkipPendingSteps()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"FailJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"PendingJob_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Stop And Skip Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 0,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Failing Step", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Pending Step", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Skip pending test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate step 1 failed
        var dbCtx = GetDbContext();
        var step1Occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus = WorkflowStepStatus.Failed;
        step1Occ.Status = JobOccurrenceStatus.Failed;
        step1Occ.Exception = "Step 1 permanent failure";
        dbCtx.JobOccurrences.Update(step1Occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var failed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Failed;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        failed.Should().BeTrue("workflow should fail with StopOnFirstFailure");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.Failed);

        // Verify pending step was skipped
        var step2Occ = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
        step2Occ.StepStatus.Should().Be(WorkflowStepStatus.Skipped, "pending step should be skipped when workflow fails");
        step2Occ.Exception.Should().Contain("skipped");
    }

    [Fact]
    public async Task WorkflowEngine_TimeoutShouldCancelRunningStepsAndSkipPending()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"RunningJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"PendingJob_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Timeout Cancel Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            TimeoutSeconds = 5,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Running Step", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Pending Step", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        // Create run and occurrences manually to simulate running state
        var runId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        var workflowRun = new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflow.Id,
            Status = WorkflowStatus.Running,
            TriggerReason = "Timeout cancel test",
            StartTime = now.AddSeconds(-10), // Started 10s ago, timeout is 5s
        };

        dbContext.WorkflowRuns.Add(workflowRun);

        dbContext.JobOccurrences.Add(new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            WorkflowRunId = runId,
            WorkflowStepId = step1Id,
            JobId = job1.Id,
            StepStatus = WorkflowStepStatus.Running,
            Status = JobOccurrenceStatus.Queued,
            CreatedAt = now,
            JobName = job1.JobNameInWorker
        });

        dbContext.JobOccurrences.Add(new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            WorkflowRunId = runId,
            WorkflowStepId = step2Id,
            JobId = job2.Id,
            StepStatus = WorkflowStepStatus.Pending,
            Status = JobOccurrenceStatus.Queued,
            CreatedAt = now,
            JobName = job2.JobNameInWorker
        });

        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var cancelled = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Cancelled;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        cancelled.Should().BeTrue("workflow should be cancelled on timeout");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.Cancelled);
        finalRun.Error.Should().Contain("timed out");

        var step1Occ = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus.Should().Be(WorkflowStepStatus.Cancelled, "running step should be cancelled on timeout");

        var step2Occ = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
        step2Occ.StepStatus.Should().Be(WorkflowStepStatus.Skipped, "pending step should be skipped on timeout");
    }

    [Fact]
    public async Task WorkflowEngine_InactiveCronWorkflow_ShouldNotTrigger()
    {
        // Arrange
        await InitializeAsync();

        // Create inactive workflow with cron
        var workflow = await SeedWorkflowAsync("Inactive Cron Workflow", isActive: false, cronExpression: "0 0 * * * *");
        workflow.LastScheduledRunAt = DateTime.UtcNow.AddHours(-2);

        var dbContext = GetDbContext();
        dbContext.Workflows.Update(workflow);
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        await engine.StopAsync(cts.Token);

        // Assert - No runs should be created
        var dbAssert = GetDbContext();
        var runs = await dbAssert.WorkflowRuns.Where(r => r.WorkflowId == workflow.Id).ToListAsync();
        runs.Should().BeEmpty("inactive workflow should not be triggered by cron");
    }

    [Fact]
    public async Task WorkflowEngine_WithConditionNode_JsonFieldExpression_ShouldRouteToFalseBranch()
    {
        // Arrange
        await InitializeAsync();

        var sourceJob = await SeedScheduledJobAsync($"SourceJob_{Guid.CreateVersion7():N}");
        var trueJob = await SeedScheduledJobAsync($"TrueJob_{Guid.CreateVersion7():N}");
        var falseJob = await SeedScheduledJobAsync($"FalseJob_{Guid.CreateVersion7():N}");

        var sourceStepId = Guid.CreateVersion7();
        var conditionStepId = Guid.CreateVersion7();
        var trueStepId = Guid.CreateVersion7();
        var falseStepId = Guid.CreateVersion7();

        // Condition: $.price > 50. Source will have price = 10, so condition evaluates to FALSE → false branch dispatched
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "JSON Field Condition Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = sourceStepId, StepName = "Source", NodeType = WorkflowNodeType.Task, JobId = sourceJob.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = conditionStepId, StepName = "Price Check", NodeType = WorkflowNodeType.Condition, NodeConfigJson = @"{""expression"": ""$.price > 50""}", Order = 2 },
                    new WorkflowStepDefinition { Id = trueStepId, StepName = "Expensive Path", NodeType = WorkflowNodeType.Task, JobId = trueJob.Id, Order = 3 },
                    new WorkflowStepDefinition { Id = falseStepId, StepName = "Cheap Path", NodeType = WorkflowNodeType.Task, JobId = falseJob.Id, Order = 4 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = sourceStepId, TargetStepId = conditionStepId, Order = 1 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = trueStepId, SourcePort = "true", Order = 2 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = falseStepId, SourcePort = "false", Order = 3 },
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "JSON field condition test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate source step completed with price = 10 (below threshold of 50)
        var dbCtx = GetDbContext();
        var sourceOcc = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == sourceStepId);
        sourceOcc.StepStatus = WorkflowStepStatus.Completed;
        sourceOcc.Status = JobOccurrenceStatus.Completed;
        sourceOcc.Result = @"{""price"": 10}";
        dbCtx.JobOccurrences.Update(sourceOcc);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for false branch to be dispatched (condition evaluates to false since price < 50)
        var falseDispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var falseOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == falseStepId);
                return falseOcc?.StepStatus == WorkflowStepStatus.Running || falseOcc?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        // Also check that true branch is skipped
        var trueSkipped = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var trueOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == trueStepId);
                return trueOcc?.StepStatus == WorkflowStepStatus.Skipped;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        falseDispatched.Should().BeTrue("false branch should be dispatched when $.price condition evaluates to false");
        trueSkipped.Should().BeTrue("true branch should be skipped when $.price condition evaluates to false");
    }

    [Fact]
    public async Task WorkflowEngine_WithConditionNode_SpecificStepStatusNotEquals_ShouldRouteToFalseBranch()
    {
        // Arrange
        await InitializeAsync();

        var sourceJob = await SeedScheduledJobAsync($"SourceJob_{Guid.CreateVersion7():N}");
        var trueJob = await SeedScheduledJobAsync($"TrueJob_{Guid.CreateVersion7():N}");
        var falseJob = await SeedScheduledJobAsync($"FalseJob_{Guid.CreateVersion7():N}");

        var sourceStepId = Guid.CreateVersion7();
        var conditionStepId = Guid.CreateVersion7();
        var trueStepId = Guid.CreateVersion7();
        var falseStepId = Guid.CreateVersion7();

        // Condition targets specific step by ID: stepId:@status != 'Completed'
        // Source step IS Completed, so != Completed evaluates to false → false branch dispatched
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Specific Step Status Condition Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = sourceStepId, StepName = "Source", NodeType = WorkflowNodeType.Task, JobId = sourceJob.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = conditionStepId, StepName = "Status Check", NodeType = WorkflowNodeType.Condition, NodeConfigJson = $@"{{""expression"": ""{sourceStepId}:@status != 'Completed'""}}", Order = 2 },
                    new WorkflowStepDefinition { Id = trueStepId, StepName = "True Branch", NodeType = WorkflowNodeType.Task, JobId = trueJob.Id, Order = 3 },
                    new WorkflowStepDefinition { Id = falseStepId, StepName = "False Branch", NodeType = WorkflowNodeType.Task, JobId = falseJob.Id, Order = 4 },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = sourceStepId, TargetStepId = conditionStepId, Order = 1 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = trueStepId, SourcePort = "true", Order = 2 },
                    new WorkflowEdgeDefinition { SourceStepId = conditionStepId, TargetStepId = falseStepId, SourcePort = "false", Order = 3 },
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Specific step status test"
        });

        var runId = triggerResult.Data.Id;

        // Simulate source step completed
        var dbCtx = GetDbContext();
        var sourceOcc = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == sourceStepId);
        sourceOcc.StepStatus = WorkflowStepStatus.Completed;
        sourceOcc.Status = JobOccurrenceStatus.Completed;
        sourceOcc.Result = @"{""status"": ""ok""}";
        dbCtx.JobOccurrences.Update(sourceOcc);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Condition: stepId:@status != 'Completed' → source IS Completed → condition false → false branch dispatched
        var falseDispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var falseOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == falseStepId);
                return falseOcc?.StepStatus == WorkflowStepStatus.Running || falseOcc?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        var trueSkipped = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var trueOcc = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == trueStepId);
                return trueOcc?.StepStatus == WorkflowStepStatus.Skipped;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        falseDispatched.Should().BeTrue("false branch should be dispatched when @status != 'Completed' evaluates to false (source IS Completed)");
        trueSkipped.Should().BeTrue("true branch should be skipped when condition evaluates to false");
    }

    [Fact]
    public async Task WorkflowEngine_WithDataMappings_ShouldDispatchWithMappedData()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"SourceJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"MappedJob_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        // DataMappings: map from specific step (stepId:jsonPath) and from first completed parent (jsonPath)
        var dataMappings = $@"{{""{step1Id}:Title"": ""MappedTitle"", ""Description"": ""MappedDescription""}}";

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Data Mapping Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Source Step", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Mapped Step", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2, DataMappings = dataMappings },
                ],
                Edges =
                [
                    new WorkflowEdgeDefinition { SourceStepId = step1Id, TargetStepId = step2Id, Order = 1 }
                ]
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        var mediator = _serviceProvider.GetRequiredService<MediatR.IMediator>();
        var triggerResult = await mediator.Send(new Milvaion.Application.Features.Workflows.TriggerWorkflow.TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Data mapping test"
        });

        var runId = triggerResult.Data.Id;

        // Complete step 1 with JSON result containing fields to map
        var dbCtx = GetDbContext();
        var step1Occ = await dbCtx.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus = WorkflowStepStatus.Completed;
        step1Occ.Status = JobOccurrenceStatus.Completed;
        step1Occ.Result = @"{""Title"": ""Hello World"", ""Description"": ""Test description""}";
        dbCtx.JobOccurrences.Update(step1Occ);

        var run = await dbCtx.WorkflowRuns.FindAsync(runId);
        run!.Status = WorkflowStatus.Running;
        run.StartTime = DateTime.UtcNow;
        dbCtx.WorkflowRuns.Update(run);
        await dbCtx.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for step 2 to be dispatched (ApplyDataMappings is called during dispatch resolution)
        var dispatched = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var step2Occ = await db.JobOccurrences.FirstOrDefaultAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
                return step2Occ?.StepStatus == WorkflowStepStatus.Running || step2Occ?.Status == JobOccurrenceStatus.Queued;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        dispatched.Should().BeTrue("step 2 should be dispatched with mapped data from step 1");
    }

    [Fact]
    public async Task WorkflowEngine_WithStopOnFirstFailure_ShouldCancelRunningStepsOnFail()
    {
        // Arrange
        await InitializeAsync();

        var job1 = await SeedScheduledJobAsync($"RunningJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"FailedJob_{Guid.CreateVersion7():N}");

        var step1Id = Guid.CreateVersion7();
        var step2Id = Guid.CreateVersion7();

        // Two parallel root steps (no edges between them)
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Fail Cancel Running Workflow",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 0,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition { Id = step1Id, StepName = "Running Step", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                    new WorkflowStepDefinition { Id = step2Id, StepName = "Failed Step", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                ],
                Edges = []
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        // Manually create run and occurrences to simulate mid-execution state
        var runId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;

        var workflowRun = new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflow.Id,
            Status = WorkflowStatus.Running,
            TriggerReason = "Fail cancel running test",
            StartTime = now.AddSeconds(-5),
        };
        dbContext.WorkflowRuns.Add(workflowRun);

        // Step 1: Running (already dispatched and being executed)
        dbContext.JobOccurrences.Add(new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            WorkflowRunId = runId,
            WorkflowStepId = step1Id,
            JobId = job1.Id,
            StepStatus = WorkflowStepStatus.Running,
            Status = JobOccurrenceStatus.Queued,
            CreatedAt = now,
            JobName = job1.JobNameInWorker
        });

        // Step 2: Failed (execution failed, retries exhausted since maxRetries=0)
        dbContext.JobOccurrences.Add(new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            WorkflowRunId = runId,
            WorkflowStepId = step2Id,
            JobId = job2.Id,
            StepStatus = WorkflowStepStatus.Failed,
            StepRetryCount = 0,
            Status = JobOccurrenceStatus.Failed,
            Exception = "Job execution failed",
            CreatedAt = now,
            JobName = job2.JobNameInWorker
        });

        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var engine = CreateWorkflowEngineService();
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        var failed = await WaitForConditionAsync(
            async () =>
            {
                var db = GetDbContext();
                var updatedRun = await db.WorkflowRuns.FindAsync(runId);
                return updatedRun?.Status == WorkflowStatus.Failed;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await engine.StopAsync(cts.Token);

        // Assert
        failed.Should().BeTrue("workflow should fail when a step fails with StopOnFirstFailure");

        var dbAssert = GetDbContext();
        var finalRun = await dbAssert.WorkflowRuns.FindAsync(runId);
        finalRun!.Status.Should().Be(WorkflowStatus.Failed);
        finalRun.EndTime.Should().NotBeNull();
        finalRun.DurationMs.Should().BeGreaterThan(0);

        // Running step should be cancelled by FailRun
        var step1Occ = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step1Id);
        step1Occ.StepStatus.Should().Be(WorkflowStepStatus.Cancelled, "running step should be cancelled when workflow fails");
        step1Occ.Status.Should().Be(JobOccurrenceStatus.Cancelled);
        step1Occ.EndTime.Should().NotBeNull();

        // Failed step should remain failed
        var step2Occ = await dbAssert.JobOccurrences.FirstAsync(o => o.WorkflowRunId == runId && o.WorkflowStepId == step2Id);
        step2Occ.StepStatus.Should().Be(WorkflowStepStatus.Failed, "failed step should remain failed");
    }

    private WorkflowEngineService CreateWorkflowEngineService() => new(
        _serviceProvider,
        Options.Create(new WorkflowEngineOptions
        {
            Enabled = true,
            PollingIntervalSeconds = 1,
        }),
        _serviceProvider.GetRequiredService<ILoggerFactory>(),
        null
        );

    private async Task<Workflow> SeedWorkflowWithSingleStepAsync(string name, bool isActive = true, int timeoutSeconds = 3600)
    {
        var job = await SeedScheduledJobAsync($"WorkflowJob_{Guid.CreateVersion7():N}");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            IsActive = isActive,
            TimeoutSeconds = timeoutSeconds,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 0,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition
                    {
                        Id = Guid.CreateVersion7(),
                        StepName = "Test Step",
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

    private async Task<Workflow> SeedWorkflowWithStepsAsync(string name, List<Guid> jobIds, WorkflowFailureStrategy failureStrategy = WorkflowFailureStrategy.StopOnFirstFailure, int maxStepRetries = 0)
    {
        var steps = jobIds.Select((jobId, index) => new WorkflowStepDefinition
        {
            Id = Guid.CreateVersion7(),
            StepName = $"Step {index + 1}",
            NodeType = WorkflowNodeType.Task,
            JobId = jobId,
            Order = index + 1
        }).ToList();

        var edges = new List<WorkflowEdgeDefinition>();
        for (int i = 0; i < steps.Count - 1; i++)
        {
            edges.Add(new WorkflowEdgeDefinition
            {
                SourceStepId = steps[i].Id,
                TargetStepId = steps[i + 1].Id,
                Order = i + 1
            });
        }

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            IsActive = true,
            FailureStrategy = failureStrategy,
            MaxStepRetries = maxStepRetries,
            Definition = new WorkflowDefinition
            {
                Steps = steps,
                Edges = edges
            }
        };

        var dbContext = GetDbContext();
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        return workflow;
    }

    private async Task<Workflow> SeedWorkflowAsync(string name, bool isActive = true, string cronExpression = null)
    {
        var job = await SeedScheduledJobAsync($"CronJob_{Guid.CreateVersion7():N}");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            IsActive = isActive,
            CronExpression = cronExpression,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            Definition = new WorkflowDefinition
            {
                Steps =
                [
                    new WorkflowStepDefinition
                    {
                        Id = Guid.CreateVersion7(),
                        StepName = "Test Step",
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

    private async Task<WorkflowRun> SeedWorkflowRunAsync(Guid workflowId, WorkflowStatus status, DateTime? startTime = null)
    {
        var run = new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = workflowId,
            Status = status,
            TriggerReason = "Test run",
            StartTime = startTime ?? (status != WorkflowStatus.Pending ? DateTime.UtcNow : null)
        };

        var dbContext = GetDbContext();
        dbContext.WorkflowRuns.Add(run);
        await dbContext.SaveChangesAsync();

        return run;
    }
}
