using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Milvaion.Api.Mcp;
using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvaion.Application.Features.Workflows.UpdateWorkflow;
using Milvaion.Application.Utils.PermissionManager;
using Milvasoft.Components.Rest.MilvaResponse;
using ModelContextProtocol.Server;
using Moq;
using System.Security.Claims;
using System.Text.Json;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// The workflow MCP tools must expose and forward <c>maxRetryAfter</c> the same way the REST API does.
/// </summary>
[Trait("Infrastructure Unit Tests", "Unit tests for the workflow MCP tools.")]
public class McpWorkflowToolsTests
{
    [Theory]
    [InlineData(nameof(MilvaionOpsTools.CreateWorkflowAsync), "integer")]
    [InlineData(nameof(MilvaionOpsTools.UpdateWorkflowAsync), "null")]
    public void ToolSchema_ShouldExposeMaxRetryAfter_WithItsDescription(string methodName, string expectedTypePart)
    {
        var tool = McpServerTool.Create(typeof(MilvaionOpsTools).GetMethod(methodName)!, CreateTools(new Mock<IMediator>().Object));

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        properties.TryGetProperty("maxRetryAfter", out var maxRetryAfter).Should().BeTrue($"{tool.ProtocolTool.Name} must accept maxRetryAfter");
        maxRetryAfter.GetProperty("type").ToString().Should().Contain("integer").And.Contain(expectedTypePart);
        maxRetryAfter.GetProperty("description").GetString().Should().Contain("RetryAfterError").And.Contain("maxStepRetries");

        if (tool.ProtocolTool.InputSchema.TryGetProperty("required", out var required))
            required.EnumerateArray().Select(r => r.GetString()).Should().NotContain("maxRetryAfter", "it is optional on both tools");
    }

    [Fact]
    public async Task UpdateWorkflow_WithMaxRetryAfter_ShouldSendItAsAnUpdatedField()
    {
        UpdateWorkflowCommand sent = null;
        var tools = CreateTools(CaptureMediator<UpdateWorkflowCommand>(c => sent = c));
        var workflowId = Guid.CreateVersion7();

        await tools.UpdateWorkflowAsync(workflowId, maxRetryAfter: 30);

        sent.Should().NotBeNull();
        sent.WorkflowId.Should().Be(workflowId);
        sent.MaxRetryAfter.IsUpdated.Should().BeTrue();
        sent.MaxRetryAfter.Value.Should().Be(30);
        sent.MaxStepRetries.IsUpdated.Should().BeFalse("only the supplied arguments are changed");
        sent.TimeoutSeconds.IsUpdated.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateWorkflow_WithoutMaxRetryAfter_ShouldLeaveItUnchanged()
    {
        UpdateWorkflowCommand sent = null;
        var tools = CreateTools(CaptureMediator<UpdateWorkflowCommand>(c => sent = c));

        await tools.UpdateWorkflowAsync(Guid.CreateVersion7(), name: "renamed");

        sent.MaxRetryAfter.IsUpdated.Should().BeFalse();
    }

    [Fact]
    public async Task CreateWorkflow_ShouldForwardMaxRetryAfter_AndDefaultToTheApiDefault()
    {
        var sent = new List<CreateWorkflowCommand>();
        var tools = CreateTools(CaptureMediator<CreateWorkflowCommand>(sent.Add));

        await tools.CreateWorkflowAsync("with limit", [], [], maxRetryAfter: 50);
        await tools.CreateWorkflowAsync("default", [], []);

        sent[0].MaxRetryAfter.Should().Be(50);
        sent[1].MaxRetryAfter.Should().Be(new CreateWorkflowCommand().MaxRetryAfter, "the tool default matches the REST API default");
    }

    [Fact]
    public void Validators_ShouldRejectANegativeMaxRetryAfter_SentByTheTools()
    {
        var update = new UpdateWorkflowCommand { WorkflowId = Guid.CreateVersion7(), MaxRetryAfter = new() { Value = -1, IsUpdated = true } };

        new UpdateWorkflowCommandValidator().Validate(update).Errors.Should().Contain(e => e.ErrorMessage.Contains("MaxRetryAfter"));
        new CreateWorkflowCommandValidator().Validate(new CreateWorkflowCommand { Name = "x", MaxRetryAfter = -1 }).Errors.Should().Contain(e => e.ErrorMessage.Contains("MaxRetryAfter"));
    }

    private static IMediator CaptureMediator<TCommand>(Action<TCommand> capture) where TCommand : IRequest<Response<Guid>>
    {
        var mediator = new Mock<IMediator>();

        mediator.Setup(m => m.Send(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Response<Guid>>, CancellationToken>((command, _) => capture((TCommand)command))
                .ReturnsAsync(Response<Guid>.Success(Guid.CreateVersion7()));

        return mediator.Object;
    }

    private static MilvaionOpsTools CreateTools(IMediator mediator)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, PermissionCatalog.App.SuperAdmin)], "Test"));
        var guard = new McpPermissionGuard(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });

        return new MilvaionOpsTools(mediator, guard);
    }
}
