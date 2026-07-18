using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.WorkflowDtos;
using Milvaion.Application.Features.Workflows.CancelWorkflow;
using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Features.Workflows.UpdateWorkflow;
using Milvaion.IntegrationTests.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// Integration tests for WorkflowsController.
/// Tests workflow CRUD operations, triggering, and execution tracking.
/// According to copilot instructions, integration tests should obtain services like IMilvaLogger directly from the ServiceProvider instead of using stubs or mocks.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class WorkflowsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    /// <summary>
    /// Marks a value as supplied on an update command. Fields left out are not touched by the handler.
    /// </summary>
    private static UpdateProperty<T> Updated<T>(T value) => new() { Value = value, IsUpdated = true };

    [Fact]
    public async Task GetWorkflows_ShouldReturnPaginatedList()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        // Create test workflow
        var workflow = await SeedWorkflowAsync("Test Workflow");

        // Act
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/workflows")
        {
            Content = JsonContent.Create(new
            {
                pageNumber = 1,
                rowCount = 10
            })
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(workflow.Id.ToString());
    }

    [Fact]
    public async Task GetWorkflowById_ShouldReturnWorkflowDetails()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Test Workflow Detail");

        // Act
        var response = await client.GetAsync($"/api/v1/workflows/workflow?workflowId={workflow.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Test Workflow Detail");
        content.Should().Contain(workflow.Id.ToString());
    }

    [Fact]
    public async Task CreateWorkflow_WithValidData_ShouldCreateSuccessfully()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"Job1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"Job2_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Integration Test Workflow",
            Description = "Workflow created in integration test",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 3,
            TimeoutSeconds = 3600,
            Tags = "test,integration",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "First Step",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job1.Id,
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "step2",
                    StepName = "Second Step",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job2.Id,
                    Order = 2
                }
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "edge1",
                    SourceTempId = "step1",
                    TargetTempId = "step2",
                    Order = 1
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeEmpty();

        // Verify workflow was created in database with correct definition
        var dbContext = GetDbContext();
        var createdWorkflow = await dbContext.Workflows.FindAsync(result.Data);
        createdWorkflow.Should().NotBeNull();
        createdWorkflow!.Name.Should().Be("Integration Test Workflow");
        createdWorkflow.Description.Should().Be("Workflow created in integration test");
        createdWorkflow.IsActive.Should().BeTrue();
        createdWorkflow.FailureStrategy.Should().Be(WorkflowFailureStrategy.StopOnFirstFailure);
        createdWorkflow.MaxStepRetries.Should().Be(3);
        createdWorkflow.TimeoutSeconds.Should().Be(3600);
        createdWorkflow.Tags.Should().Be("test,integration");

        // Verify step definitions
        createdWorkflow.Definition.Should().NotBeNull();
        createdWorkflow.Definition!.Steps.Should().HaveCount(2);

        var step1 = createdWorkflow.Definition.Steps.Single(s => s.StepName == "First Step");
        step1.NodeType.Should().Be(WorkflowNodeType.Task);
        step1.JobId.Should().Be(job1.Id);
        step1.Order.Should().Be(1);

        var step2 = createdWorkflow.Definition.Steps.Single(s => s.StepName == "Second Step");
        step2.NodeType.Should().Be(WorkflowNodeType.Task);
        step2.JobId.Should().Be(job2.Id);
        step2.Order.Should().Be(2);

        // Verify edges
        createdWorkflow.Definition.Edges.Should().HaveCount(1);
        var edge = createdWorkflow.Definition.Edges[0];
        edge.SourceStepId.Should().Be(step1.Id);
        edge.TargetStepId.Should().Be(step2.Id);
        edge.Order.Should().Be(1);
    }

    [Fact]
    public async Task CreateWorkflow_WithInvalidCronExpression_ShouldReturnBadRequest()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job = await SeedScheduledJobAsync($"Job_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Invalid Cron Workflow",
            CronExpression = "invalid cron",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job.Id,
                    Order = 1
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("cron", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateWorkflow_WithValidData_ShouldUpdateSuccessfully()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Original Workflow");
        var job = await SeedScheduledJobAsync($"UpdatedJob_{Guid.CreateVersion7():N}");

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Name = Updated("Updated Workflow"),
            Description = Updated("Updated description"),
            IsActive = Updated(false),
            FailureStrategy = Updated(WorkflowFailureStrategy.ContinueOnFailure),
            MaxStepRetries = Updated(5),
            TimeoutSeconds = Updated<int?>(7200),
            Tags = Updated("updated,test"),
            Steps = Updated<List<CreateWorkflowStepDto>>(
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Updated Step",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job.Id,
                    Order = 1
                }
            ]),
            Edges = Updated(new List<CreateWorkflowEdgeDto>())
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();

        // Verify workflow was updated with all properties
        var dbContext = GetDbContext();
        var updatedWorkflow = await dbContext.Workflows.FindAsync(workflow.Id);
        updatedWorkflow.Should().NotBeNull();
        updatedWorkflow!.Name.Should().Be("Updated Workflow");
        updatedWorkflow.Description.Should().Be("Updated description");
        updatedWorkflow.IsActive.Should().BeFalse();
        updatedWorkflow.FailureStrategy.Should().Be(WorkflowFailureStrategy.ContinueOnFailure);
        updatedWorkflow.MaxStepRetries.Should().Be(5);
        updatedWorkflow.TimeoutSeconds.Should().Be(7200);
        updatedWorkflow.Tags.Should().Be("updated,test");

        // Verify step definition was replaced
        updatedWorkflow.Definition.Should().NotBeNull();
        updatedWorkflow.Definition!.Steps.Should().HaveCount(1);
        var step = updatedWorkflow.Definition.Steps[0];
        step.StepName.Should().Be("Updated Step");
        step.NodeType.Should().Be(WorkflowNodeType.Task);
        step.JobId.Should().Be(job.Id);
        step.Order.Should().Be(1);

        // Verify edges are empty
        updatedWorkflow.Definition.Edges.Should().BeEmpty();

        // Verify version was incremented (definition changed)
        updatedWorkflow.Version.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task DeleteWorkflow_ShouldDeleteSuccessfully()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow To Delete");

        // Act
        var response = await client.DeleteAsync($"/api/v1/workflows/workflow?workflowId={workflow.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify workflow is deleted
        var dbContext = GetDbContext();
        var deletedWorkflow = await dbContext.Workflows.FindAsync(workflow.Id);
        deletedWorkflow.Should().BeNull();
    }

    [Fact]
    public async Task TriggerWorkflow_ShouldCreateWorkflowRun()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow To Trigger", isActive: true);

        var command = new TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Integration test trigger"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow/trigger", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<TriggerWorkflowResponse>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();

        // Verify run was created
        var dbContext = GetDbContext();
        var createdRun = await dbContext.WorkflowRuns.FirstOrDefaultAsync(r => r.WorkflowId == workflow.Id);
        createdRun.Should().NotBeNull();
        createdRun!.TriggerReason.Should().Be("Integration test trigger");
    }

    [Fact]
    public async Task TriggerWorkflow_InactiveWorkflow_ShouldReturnBadRequest()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Inactive Workflow", isActive: false);

        var command = new TriggerWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Reason = "Should fail"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow/trigger", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<TriggerWorkflowResponse>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("not active", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetWorkflowRuns_ShouldReturnPaginatedRuns()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow With Runs");
        var run = await SeedWorkflowRunAsync(workflow.Id, WorkflowStatus.Running);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                pageNumber = 1,
                rowCount = 10
            })
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(run.Id.ToString());
    }

    [Fact]
    public async Task GetWorkflowRunDetail_ShouldReturnRunWithSteps()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow For Run Detail");
        var run = await SeedWorkflowRunAsync(workflow.Id, WorkflowStatus.Completed);

        // Act
        var response = await client.GetAsync($"/api/v1/workflows/runs/run?runId={run.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(run.Id.ToString());
    }

    [Fact]
    public async Task CancelWorkflow_ShouldCancelRunningRun()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow To Cancel");
        var run = await SeedWorkflowRunAsync(workflow.Id, WorkflowStatus.Running);

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow/cancel", new CancelWorkflowCommand { WorkflowRunId = run.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify run is cancelled
        var dbContext = GetDbContext();
        var cancelledRun = await dbContext.WorkflowRuns.FindAsync(run.Id);
        cancelledRun.Should().NotBeNull();
        cancelledRun!.Status.Should().Be(WorkflowStatus.Cancelled);
    }

    [Fact]
    public async Task CreateWorkflow_WithConditionalBranching_ShouldCreateSuccessfully()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"TruePathJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"FalsePathJob_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Conditional Workflow",
            Description = "Workflow with conditional branching",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "condition",
                    StepName = "Check Condition",
                    NodeType = WorkflowNodeType.Condition,
                    NodeConfigJson = @"{""expression"": ""result.status == 'success'""}",
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "trueTask",
                    StepName = "True Path",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job1.Id,
                    Order = 2
                },
                new CreateWorkflowStepDto
                {
                    TempId = "falseTask",
                    StepName = "False Path",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job2.Id,
                    Order = 3
                }
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "edgeTrue",
                    SourceTempId = "condition",
                    TargetTempId = "trueTask",
                    SourcePort = "true",
                    Order = 1
                },
                new CreateWorkflowEdgeDto
                {
                    TempId = "edgeFalse",
                    SourceTempId = "condition",
                    TargetTempId = "falseTask",
                    SourcePort = "false",
                    Order = 2
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();

        // Verify workflow was created with correct node types and edges
        var dbContext = GetDbContext();
        var createdWorkflow = await dbContext.Workflows.FindAsync(result.Data);
        createdWorkflow.Should().NotBeNull();
        createdWorkflow!.Name.Should().Be("Conditional Workflow");

        createdWorkflow.Definition.Should().NotBeNull();
        createdWorkflow.Definition!.Steps.Should().HaveCount(3);

        // Verify condition node
        var conditionStep = createdWorkflow.Definition.Steps.Single(s => s.StepName == "Check Condition");
        conditionStep.NodeType.Should().Be(WorkflowNodeType.Condition);
        conditionStep.JobId.Should().BeNull();
        conditionStep.NodeConfigJson.Should().Contain("expression");
        conditionStep.Order.Should().Be(1);

        // Verify task nodes
        var trueStep = createdWorkflow.Definition.Steps.Single(s => s.StepName == "True Path");
        trueStep.NodeType.Should().Be(WorkflowNodeType.Task);
        trueStep.JobId.Should().Be(job1.Id);

        var falseStep = createdWorkflow.Definition.Steps.Single(s => s.StepName == "False Path");
        falseStep.NodeType.Should().Be(WorkflowNodeType.Task);
        falseStep.JobId.Should().Be(job2.Id);

        // Verify edges with source ports
        createdWorkflow.Definition.Edges.Should().HaveCount(2);

        var trueEdge = createdWorkflow.Definition.Edges.Single(e => e.SourcePort == "true");
        trueEdge.SourceStepId.Should().Be(conditionStep.Id);
        trueEdge.TargetStepId.Should().Be(trueStep.Id);
        trueEdge.Order.Should().Be(1);

        var falseEdge = createdWorkflow.Definition.Edges.Single(e => e.SourcePort == "false");
        falseEdge.SourceStepId.Should().Be(conditionStep.Id);
        falseEdge.TargetStepId.Should().Be(falseStep.Id);
        falseEdge.Order.Should().Be(2);
    }

    [Fact]
    public async Task CreateWorkflow_WithDataMapping_ShouldCreateSuccessfully()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"ExtractJob_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"TransformJob_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Data Mapping Workflow",
            Description = "Workflow with data mappings between steps",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "extract",
                    StepName = "Extract Data",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job1.Id,
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "transform",
                    StepName = "Transform Data",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job2.Id,
                    Order = 2,
                    DataMappings = @"{""extract:result.userId"": ""inputUserId"", ""extract:result.data"": ""inputData""}"
                }
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "edge1",
                    SourceTempId = "extract",
                    TargetTempId = "transform",
                    Order = 1
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();

        // Verify workflow was created with data mappings
        var dbContext = GetDbContext();
        var createdWorkflow = await dbContext.Workflows.FindAsync(result.Data);
        createdWorkflow.Should().NotBeNull();
        createdWorkflow!.Name.Should().Be("Data Mapping Workflow");

        createdWorkflow.Definition.Should().NotBeNull();
        createdWorkflow.Definition!.Steps.Should().HaveCount(2);

        // Verify extract step has no mappings
        var extractStep = createdWorkflow.Definition.Steps.Single(s => s.StepName == "Extract Data");
        extractStep.NodeType.Should().Be(WorkflowNodeType.Task);
        extractStep.DataMappings.Should().BeNull();

        // Verify transform step has data mappings
        var transformStep = createdWorkflow.Definition.Steps.Single(s => s.StepName == "Transform Data");
        transformStep.NodeType.Should().Be(WorkflowNodeType.Task);
        transformStep.DataMappings.Should().NotBeNullOrEmpty();
        transformStep.DataMappings.Should().Contain("inputUserId");
        transformStep.DataMappings.Should().Contain("inputData");

        // Verify edge
        createdWorkflow.Definition.Edges.Should().HaveCount(1);
        var edge = createdWorkflow.Definition.Edges[0];
        edge.SourceStepId.Should().Be(extractStep.Id);
        edge.TargetStepId.Should().Be(transformStep.Id);
    }

    [Fact]
    public async Task CreateWorkflow_WithAllStepProperties_ShouldPersistAllFields()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job = await SeedScheduledJobAsync($"FullPropsJob_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Full Properties Workflow",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Full Step",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job.Id,
                    Order = 1,
                    DelaySeconds = 30,
                    JobDataOverride = @"{""key"": ""value""}",
                    PositionX = 100.5,
                    PositionY = 200.75,
                    NodeConfigJson = @"{""timeout"": 60}"
                }
            ],
            Edges = []
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var workflow = await dbContext.Workflows.FindAsync(result.Data);
        workflow.Should().NotBeNull();

        var step = workflow!.Definition!.Steps.Single();
        step.NodeType.Should().Be(WorkflowNodeType.Task);
        step.JobId.Should().Be(job.Id);
        step.StepName.Should().Be("Full Step");
        step.Order.Should().Be(1);
        step.DelaySeconds.Should().Be(30);
        step.JobDataOverride.Should().Contain("key");
        step.PositionX.Should().Be(100.5);
        step.PositionY.Should().Be(200.75);
        step.NodeConfigJson.Should().Contain("timeout");
    }

    [Fact]
    public async Task CreateWorkflow_WithMergeNode_ShouldPersistNodeType()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"BranchA_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"BranchB_{Guid.CreateVersion7():N}");
        var job3 = await SeedScheduledJobAsync($"AfterMerge_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Merge Node Workflow",
            Steps =
            [
                new CreateWorkflowStepDto { TempId = "a", StepName = "Branch A", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                new CreateWorkflowStepDto { TempId = "b", StepName = "Branch B", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
                new CreateWorkflowStepDto { TempId = "merge", StepName = "Merge Point", NodeType = WorkflowNodeType.Merge, Order = 3 },
                new CreateWorkflowStepDto { TempId = "after", StepName = "After Merge", NodeType = WorkflowNodeType.Task, JobId = job3.Id, Order = 4 },
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto { TempId = "e1", SourceTempId = "a", TargetTempId = "merge", Order = 1 },
                new CreateWorkflowEdgeDto { TempId = "e2", SourceTempId = "b", TargetTempId = "merge", Order = 2 },
                new CreateWorkflowEdgeDto { TempId = "e3", SourceTempId = "merge", TargetTempId = "after", Order = 3 },
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var workflow = await dbContext.Workflows.FindAsync(result.Data);
        workflow!.Definition!.Steps.Should().HaveCount(4);

        var mergeStep = workflow.Definition.Steps.Single(s => s.StepName == "Merge Point");
        mergeStep.NodeType.Should().Be(WorkflowNodeType.Merge);
        mergeStep.JobId.Should().BeNull();

        // Verify edges converge into the merge node
        workflow.Definition.Edges.Should().HaveCount(3);
        var mergeIncomingEdges = workflow.Definition.Edges.Where(e => e.TargetStepId == mergeStep.Id).ToList();
        mergeIncomingEdges.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateWorkflow_WithEdgeLabelsAndConfig_ShouldPersistEdgeFields()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"EdgeJob1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"EdgeJob2_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Edge Properties Workflow",
            Steps =
            [
                new CreateWorkflowStepDto { TempId = "s1", StepName = "Step 1", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                new CreateWorkflowStepDto { TempId = "s2", StepName = "Step 2", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "e1",
                    SourceTempId = "s1",
                    TargetTempId = "s2",
                    SourcePort = "output",
                    TargetPort = "input",
                    Label = "data-flow",
                    Order = 1,
                    EdgeConfigJson = @"{""weight"": 1}"
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var workflow = await dbContext.Workflows.FindAsync(result.Data);
        var edge = workflow!.Definition!.Edges.Single();
        edge.SourcePort.Should().Be("output");
        edge.TargetPort.Should().Be("input");
        edge.Label.Should().Be("data-flow");
        edge.EdgeConfigJson.Should().Contain("weight");
        edge.Order.Should().Be(1);
    }

    [Fact]
    public async Task UpdateWorkflow_WithEdges_ShouldPersistEdgesAndCreateVersion()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Workflow Before Edge Update");
        var job1 = await SeedScheduledJobAsync($"EdgeUpdateJob1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"EdgeUpdateJob2_{Guid.CreateVersion7():N}");

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Name = Updated("Workflow After Edge Update"),
            Description = Updated(workflow.Description),
            IsActive = Updated(true),
            FailureStrategy = Updated(WorkflowFailureStrategy.StopOnFirstFailure),
            MaxStepRetries = Updated(3),
            TimeoutSeconds = Updated<int?>(3600),
            Tags = Updated("test"),
            Steps = Updated<List<CreateWorkflowStepDto>>(
            [
                new CreateWorkflowStepDto { TempId = "s1", StepName = "Step A", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                new CreateWorkflowStepDto { TempId = "s2", StepName = "Step B", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
            ]),
            Edges = Updated<List<CreateWorkflowEdgeDto>>(
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "e1",
                    SourceTempId = "s1",
                    TargetTempId = "s2",
                    Label = "updated-edge",
                    Order = 1
                }
            ])
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var updated = await dbContext.Workflows.FindAsync(workflow.Id);
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Workflow After Edge Update");

        // Verify steps
        updated.Definition!.Steps.Should().HaveCount(2);
        updated.Definition.Steps.Should().Contain(s => s.StepName == "Step A" && s.NodeType == WorkflowNodeType.Task);
        updated.Definition.Steps.Should().Contain(s => s.StepName == "Step B" && s.NodeType == WorkflowNodeType.Task);

        // Verify edges persisted
        updated.Definition.Edges.Should().HaveCount(1);
        var edge = updated.Definition.Edges.Single();
        edge.Label.Should().Be("updated-edge");

        var stepA = updated.Definition.Steps.Single(s => s.StepName == "Step A");
        var stepB = updated.Definition.Steps.Single(s => s.StepName == "Step B");
        edge.SourceStepId.Should().Be(stepA.Id);
        edge.TargetStepId.Should().Be(stepB.Id);

        // Verify version snapshot was created
        updated.Version.Should().BeGreaterThan(1);
        updated.Versions.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateWorkflow_WithCircularDependency_ShouldFail()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var job1 = await SeedScheduledJobAsync($"CycleJob1_{Guid.CreateVersion7():N}");
        var job2 = await SeedScheduledJobAsync($"CycleJob2_{Guid.CreateVersion7():N}");

        var command = new CreateWorkflowCommand
        {
            Name = "Circular Workflow",
            Steps =
            [
                new CreateWorkflowStepDto { TempId = "s1", StepName = "Step 1", NodeType = WorkflowNodeType.Task, JobId = job1.Id, Order = 1 },
                new CreateWorkflowStepDto { TempId = "s2", StepName = "Step 2", NodeType = WorkflowNodeType.Task, JobId = job2.Id, Order = 2 },
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto { TempId = "e1", SourceTempId = "s1", TargetTempId = "s2", Order = 1 },
                new CreateWorkflowEdgeDto { TempId = "e2", SourceTempId = "s2", TargetTempId = "s1", Order = 2 },
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("circular", StringComparison.OrdinalIgnoreCase)
                                             || m.Message.Contains("DAG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateWorkflow_WithNonExistentJob_ShouldFail()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var command = new CreateWorkflowCommand
        {
            Name = "Bad Job Workflow",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "s1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ],
            Edges = []
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                             || m.Message.Contains("Jobs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateWorkflow_WithNodeTypeChange_ShouldPersistNewNodeType()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("NodeType Change Workflow");
        var job = await SeedScheduledJobAsync($"NTJob_{Guid.CreateVersion7():N}");

        // Update: replace the single Task step with a Condition + two Task steps
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Name = Updated(workflow.Name),
            Description = Updated(workflow.Description),
            IsActive = Updated(true),
            FailureStrategy = Updated(WorkflowFailureStrategy.StopOnFirstFailure),
            MaxStepRetries = Updated(3),
            TimeoutSeconds = Updated<int?>(3600),
            Tags = Updated("test"),
            Steps = Updated<List<CreateWorkflowStepDto>>(
            [
                new CreateWorkflowStepDto
                {
                    TempId = "cond",
                    StepName = "Condition Step",
                    NodeType = WorkflowNodeType.Condition,
                    NodeConfigJson = @"{""expression"": ""$.count > 0""}",
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "task",
                    StepName = "Task Step",
                    NodeType = WorkflowNodeType.Task,
                    JobId = job.Id,
                    Order = 2
                }
            ]),
            Edges = Updated<List<CreateWorkflowEdgeDto>>(
            [
                new CreateWorkflowEdgeDto { TempId = "e1", SourceTempId = "cond", TargetTempId = "task", SourcePort = "true", Order = 1 }
            ])
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var updated = await dbContext.Workflows.FindAsync(workflow.Id);

        updated!.Definition!.Steps.Should().HaveCount(2);

        var condStep = updated.Definition.Steps.Single(s => s.StepName == "Condition Step");
        condStep.NodeType.Should().Be(WorkflowNodeType.Condition);
        condStep.JobId.Should().BeNull();
        condStep.NodeConfigJson.Should().Contain("$.count > 0");

        var taskStep = updated.Definition.Steps.Single(s => s.StepName == "Task Step");
        taskStep.NodeType.Should().Be(WorkflowNodeType.Task);
        taskStep.JobId.Should().Be(job.Id);

        // Verify edge with source port
        updated.Definition.Edges.Should().HaveCount(1);
        updated.Definition.Edges[0].SourcePort.Should().Be("true");
    }

    /// <summary>
    /// A field that is not sent must survive the update untouched. This is the whole point of wrapping the
    /// command: the previous full-replace shape blanked anything the caller forgot to include.
    /// </summary>
    [Fact]
    public async Task UpdateWorkflow_WithOnlyName_ShouldLeaveEverythingElseIntact()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Before Rename");
        var originalStepCount = workflow.Definition.Steps.Count;

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Name = Updated("After Rename")
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var dbContext = GetDbContext();
        var updated = await dbContext.Workflows.FindAsync(workflow.Id);

        updated!.Name.Should().Be("After Rename");
        updated.Description.Should().Be(workflow.Description);
        updated.Tags.Should().Be(workflow.Tags);
        updated.MaxStepRetries.Should().Be(workflow.MaxStepRetries);
        updated.TimeoutSeconds.Should().Be(workflow.TimeoutSeconds);
        updated.FailureStrategy.Should().Be(workflow.FailureStrategy);
        updated.Definition.Steps.Should().HaveCount(originalStepCount);
    }

    /// <summary>
    /// Steps and edges are replaced as one unit. Accepting steps alone would silently drop every edge.
    /// </summary>
    [Fact]
    public async Task UpdateWorkflow_WithStepsButNoEdges_ShouldFail()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Unit Guard Workflow");
        var job = await SeedScheduledJobAsync($"UnitGuardJob_{Guid.CreateVersion7():N}");

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Steps = Updated<List<CreateWorkflowStepDto>>(
            [
                new CreateWorkflowStepDto { TempId = "s1", StepName = "Lonely Step", NodeType = WorkflowNodeType.Task, JobId = job.Id, Order = 1 }
            ])
            // Edges deliberately left untouched.
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeFalse();
    }

    /// <summary>
    /// Pausing a workflow while it is running used to be impossible: steps were mandatory on every update, so the
    /// active-run guard rejected even a metadata-only change. Metadata updates now skip that guard.
    /// </summary>
    [Fact]
    public async Task UpdateWorkflow_PausingWhileRunActive_ShouldSucceed()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Running Workflow");

        var dbContext = GetDbContext();

        dbContext.Set<WorkflowRun>().Add(new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = workflow.Id,
            Status = WorkflowStatus.Running,
            StartTime = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            IsActive = Updated(false)
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeTrue();

        var verifyContext = GetDbContext();
        var updated = await verifyContext.Workflows.FindAsync(workflow.Id);
        updated!.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// The counterpart to the test above: changing the definition while a run is active is still refused.
    /// </summary>
    [Fact]
    public async Task UpdateWorkflow_ChangingDefinitionWhileRunActive_ShouldFail()
    {
        // Arrange
        await InitializeAsync();
        var client = await GetClient();

        var workflow = await SeedWorkflowAsync("Busy Workflow");
        var job = await SeedScheduledJobAsync($"BusyJob_{Guid.CreateVersion7():N}");

        var dbContext = GetDbContext();

        dbContext.Set<WorkflowRun>().Add(new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = workflow.Id,
            Status = WorkflowStatus.Running,
            StartTime = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();

        var command = new UpdateWorkflowCommand
        {
            WorkflowId = workflow.Id,
            Steps = Updated<List<CreateWorkflowStepDto>>(
            [
                new CreateWorkflowStepDto { TempId = "s1", StepName = "New Step", NodeType = WorkflowNodeType.Task, JobId = job.Id, Order = 1 }
            ]),
            Edges = Updated(new List<CreateWorkflowEdgeDto>())
        };

        // Act
        var response = await client.PutAsJsonAsync("/api/v1/workflows/workflow", command);

        // Assert
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();
        result!.IsSuccess.Should().BeFalse();
    }

    private async Task<HttpClient> GetClient()
    {
        var client = _factory.CreateClient();

        // Seed root user if not exists
        await SeedRootUserAndSuperAdminRoleAsync();

        // Login to get auth token
        await client.LoginAsync();

        return client;
    }

    private async Task<Workflow> SeedWorkflowAsync(string name, bool isActive = true, WorkflowFailureStrategy strategy = WorkflowFailureStrategy.StopOnFirstFailure)
    {
        var dbContext = GetDbContext();

        var job = await SeedScheduledJobAsync($"WorkflowJob_{Guid.CreateVersion7():N}");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = $"Test workflow: {name}",
            IsActive = isActive,
            FailureStrategy = strategy,
            MaxStepRetries = 3,
            TimeoutSeconds = 3600,
            Version = 1,
            Tags = "test",
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

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();

        return workflow;
    }

    private async Task<WorkflowRun> SeedWorkflowRunAsync(Guid workflowId, WorkflowStatus status)
    {
        var dbContext = GetDbContext();

        var run = new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = workflowId,
            Status = status,
            TriggerReason = "Test run",
            StartTime = status != WorkflowStatus.Pending ? DateTime.UtcNow.AddMinutes(-10) : null,
            EndTime = status == WorkflowStatus.Completed || status == WorkflowStatus.Failed || status == WorkflowStatus.Cancelled ? DateTime.UtcNow : null
        };

        if (run.StartTime.HasValue && run.EndTime.HasValue)
            run.DurationMs = (int)(run.EndTime.Value - run.StartTime.Value).TotalMilliseconds;

        dbContext.WorkflowRuns.Add(run);
        await dbContext.SaveChangesAsync();

        return run;
    }
}
