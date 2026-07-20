using FluentAssertions;
using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Features.Workflows.UpdateWorkflow;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Types.Structs;

namespace Milvaion.UnitTests.ApplicationTests.Workflows;

/// <summary>
/// Unit tests for workflow command validators.
/// Tests validation rules for workflow create, update, and trigger commands.
/// </summary>
public class WorkflowCommandValidatorTests
{
    [Fact]
    public void CreateWorkflowCommandValidator_WithValidCommand_ShouldPass()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            Description = "Test workflow description",
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 3,
            TimeoutSeconds = 3600,
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ],
            Edges = []
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithEmptyName_ShouldFail()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateWorkflowCommand.Name));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithNoSteps_ShouldFail()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            Steps = []
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateWorkflowCommand.Steps));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithInvalidCronExpression_ShouldFail()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            CronExpression = "invalid cron",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateWorkflowCommand.CronExpression));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithValidSixPartCronExpression_ShouldPass()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            CronExpression = "0 0 9 * * *", // Every day at 9 AM (6-part format: second minute hour day month dayOfWeek)
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    private static UpdateProperty<T> Updated<T>(T value) => new() { Value = value, IsUpdated = true };

    private static List<CreateWorkflowStepDto> OneValidStep()
    => [
        new CreateWorkflowStepDto
        {
            TempId = "step1",
            StepName = "Step 1",
            NodeType = WorkflowNodeType.Task,
            JobId = Guid.CreateVersion7(),
            Order = 1
        }
    ];

    [Fact]
    public void UpdateWorkflowCommandValidator_WithValidCommand_ShouldPass()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            Name = Updated("Updated Workflow"),
            Description = Updated("Updated description"),
            FailureStrategy = Updated(WorkflowFailureStrategy.ContinueOnFailure),
            MaxStepRetries = Updated(5),
            Steps = Updated(OneValidStep()),
            Edges = Updated(new List<CreateWorkflowEdgeDto>())
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void UpdateWorkflowCommandValidator_WithEmptyId_ShouldFail()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.Empty,
            Name = Updated("Updated Workflow"),
            Steps = Updated(OneValidStep()),
            Edges = Updated(new List<CreateWorkflowEdgeDto>())
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateWorkflowCommand.WorkflowId));
    }

    /// <summary>
    /// The point of wrapping every field: pausing a workflow must not require resending its definition.
    /// </summary>
    [Fact]
    public void UpdateWorkflowCommandValidator_WithOnlyIsActive_ShouldPass()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            IsActive = Updated(false)
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A field that was never sent must not be validated - otherwise a rename would fail for a missing name.
    /// </summary>
    [Fact]
    public void UpdateWorkflowCommandValidator_WithUntouchedInvalidLookingFields_ShouldPass()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            Tags = Updated("nightly,billing")
            // Name, Steps, Edges, CronExpression and MaxStepRetries are all left untouched.
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void UpdateWorkflowCommandValidator_WithUpdatedButEmptySteps_ShouldFail()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            Steps = Updated(new List<CreateWorkflowStepDto>()),
            Edges = Updated(new List<CreateWorkflowEdgeDto>())
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void UpdateWorkflowCommandValidator_WithUpdatedEmptyName_ShouldFail()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            Name = Updated(string.Empty)
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void UpdateWorkflowCommandValidator_WithUpdatedInvalidCron_ShouldFail()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            CronExpression = Updated("not a cron expression")
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void UpdateWorkflowCommandValidator_WithUpdatedNegativeMaxStepRetries_ShouldFail()
    {
        // Arrange
        var validator = new UpdateWorkflowCommandValidator();
        var command = new UpdateWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            MaxStepRetries = Updated(-1)
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void TriggerWorkflowCommandValidator_WithValidCommand_ShouldPass()
    {
        // Arrange
        var validator = new TriggerWorkflowCommandValidator();
        var command = new TriggerWorkflowCommand
        {
            WorkflowId = Guid.CreateVersion7(),
            Reason = "Manual trigger"
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TriggerWorkflowCommandValidator_WithEmptyWorkflowId_ShouldFail()
    {
        // Arrange
        var validator = new TriggerWorkflowCommandValidator();
        var command = new TriggerWorkflowCommand
        {
            WorkflowId = Guid.Empty,
            Reason = "Manual trigger"
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(TriggerWorkflowCommand.WorkflowId));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithNegativeTimeout_ShouldFail()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            TimeoutSeconds = -100,
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateWorkflowCommand.TimeoutSeconds));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithNegativeMaxRetries_ShouldFail()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            MaxStepRetries = -1,
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "step1",
                    StepName = "Step 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateWorkflowCommand.MaxStepRetries));
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithConditionNodeAndExpression_ShouldPass()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "condition1",
                    StepName = "Check Status",
                    NodeType = WorkflowNodeType.Condition,
                    NodeConfigJson = @"{""expression"": ""result.status == 'success'""}",
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "task1",
                    StepName = "Process",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 2
                }
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "edge1",
                    SourceTempId = "condition1",
                    TargetTempId = "task1",
                    SourcePort = "true",
                    Order = 1
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateWorkflowCommandValidator_WithMergeNode_ShouldPass()
    {
        // Arrange
        var validator = new CreateWorkflowCommandValidator();
        var command = new CreateWorkflowCommand
        {
            Name = "Test Workflow",
            Steps =
            [
                new CreateWorkflowStepDto
                {
                    TempId = "task1",
                    StepName = "Task 1",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 1
                },
                new CreateWorkflowStepDto
                {
                    TempId = "task2",
                    StepName = "Task 2",
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7(),
                    Order = 2
                },
                new CreateWorkflowStepDto
                {
                    TempId = "merge1",
                    StepName = "Merge",
                    NodeType = WorkflowNodeType.Merge,
                    Order = 3
                }
            ],
            Edges =
            [
                new CreateWorkflowEdgeDto
                {
                    TempId = "edge1",
                    SourceTempId = "task1",
                    TargetTempId = "merge1",
                    Order = 1
                },
                new CreateWorkflowEdgeDto
                {
                    TempId = "edge2",
                    SourceTempId = "task2",
                    TargetTempId = "merge1",
                    Order = 2
                }
            ]
        };

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }
}
