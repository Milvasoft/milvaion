using FluentAssertions;
using Milvaion.Application.Features.Workflows;
using System.Text.Json.Nodes;

namespace Milvaion.UnitTests.ApplicationTests.Workflows;

/// <summary>
/// Unit tests for the temporary step id remapping applied when a workflow is saved.
/// </summary>
/// <remarks>
/// Edges have always had their temporary ids translated to real ones. Condition expressions and data mappings
/// carry the same ids and used to be persisted verbatim, which failed quietly: the engine parses the prefix with
/// <c>Guid.TryParse</c>, a value like <c>step-1</c> does not parse, and the clause silently stops targeting
/// anything.
/// </remarks>
public class WorkflowStepExtensionsTests
{
    private static readonly Guid _extractId = Guid.Parse("0198a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid _transformId = Guid.Parse("0198ffff-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static readonly Dictionary<string, Guid> _map = new()
    {
        ["step-1"] = _extractId,
        ["step-11"] = _transformId,
        // An existing step is keyed by its own id, which is what makes editing a saved workflow a no-op.
        [_extractId.ToString()] = _extractId
    };

    #region Condition expressions

    [Fact]
    public void RemapConditionExpression_ShouldReplaceTemporaryIdWithRealId()
    {
        var result = WorkflowStepExtensions.RemapConditionExpression(Config("step-1:@status == 'Completed'"), _map);

        Expression(result).Should().Be($"{_extractId}:@status == 'Completed'");
    }

    /// <summary>
    /// Rewriting clause by clause rather than by string replacement, so a shorter id is not matched inside a
    /// longer one.
    /// </summary>
    [Fact]
    public void RemapConditionExpression_ShouldNotConfuseIdsSharingAPrefix()
    {
        var result = WorkflowStepExtensions.RemapConditionExpression(Config("step-11:@status == 'Failed'"), _map);

        Expression(result).Should().Be($"{_transformId}:@status == 'Failed'");
    }

    [Fact]
    public void RemapConditionExpression_ShouldRewriteEveryClause()
    {
        var result = WorkflowStepExtensions.RemapConditionExpression(
            Config("step-1:@status == 'Completed' && $.count > 1 || step-11:$.name == 'a'"),
            _map);

        Expression(result).Should().Be(
            $"{_extractId}:@status == 'Completed' && $.count > 1 || {_transformId}:$.name == 'a'");
    }

    /// <summary>
    /// A colon inside a compared value must not be mistaken for a step id prefix.
    /// </summary>
    [Theory]
    [InlineData("$.time == '10:30'")]
    [InlineData("@status == 'Completed'")]
    [InlineData("step-99:@status == 'Completed'")]
    public void RemapConditionExpression_ShouldLeaveUnrelatedClausesAlone(string expression)
    {
        var config = Config(expression);

        WorkflowStepExtensions.RemapConditionExpression(config, _map).Should().Be(config);
    }

    /// <summary>
    /// Editing a saved workflow maps every id to itself, so the payload must come back byte for byte identical.
    /// The update handler compares this value against the stored one to decide whether to cut a new version, and
    /// a re-serialised but semantically identical payload would register as an edit.
    /// </summary>
    [Fact]
    public void RemapConditionExpression_ShouldReturnTheOriginalStringWhenNothingChanges()
    {
        var config = Config($"{_extractId}:@status == 'Completed'");

        WorkflowStepExtensions.RemapConditionExpression(config, _map).Should().BeSameAs(config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"timeout\": 60}")]
    [InlineData("{\"expression\": \"\"}")]
    public void RemapConditionExpression_ShouldPassThroughUnusableConfig(string config)
        => WorkflowStepExtensions.RemapConditionExpression(config, _map).Should().Be(config);

    [Fact]
    public void RemapConditionExpression_ShouldPassThroughWhenThereIsNothingToMap()
    {
        var config = Config("step-1:@status == 'Completed'");

        WorkflowStepExtensions.RemapConditionExpression(config, []).Should().Be(config);
        WorkflowStepExtensions.RemapConditionExpression(config, null).Should().Be(config);
    }

    #endregion

    #region Data mappings

    [Fact]
    public void RemapDataMappings_ShouldReplaceTemporaryIdInKeys()
    {
        var result = WorkflowStepExtensions.RemapDataMappings(
            """{"step-1:result.userId": "inputUserId"}""",
            _map);

        result.Should().Contain($"{_extractId}:result.userId");
        result.Should().Contain("inputUserId");
        result.Should().NotContain("step-1:");
    }

    [Fact]
    public void RemapDataMappings_ShouldRewriteEveryKey()
    {
        var result = WorkflowStepExtensions.RemapDataMappings(
            """{"step-1:result.userId": "inputUserId", "step-11:result.data": "inputData"}""",
            _map);

        result.Should().Contain($"{_extractId}:result.userId");
        result.Should().Contain($"{_transformId}:result.data");
    }

    [Fact]
    public void RemapDataMappings_ShouldLeaveKeysWithoutAKnownPrefixAlone()
    {
        const string mappings = """{"result.userId": "inputUserId", "step-99:result.x": "inputX"}""";

        WorkflowStepExtensions.RemapDataMappings(mappings, _map).Should().Be(mappings);
    }

    [Fact]
    public void RemapDataMappings_ShouldReturnTheOriginalStringWhenNothingChanges()
    {
        var mappings = $$"""{"{{_extractId}}:result.userId": "inputUserId"}""";

        WorkflowStepExtensions.RemapDataMappings(mappings, _map).Should().BeSameAs(mappings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void RemapDataMappings_ShouldPassThroughUnusablePayloads(string mappings)
        => WorkflowStepExtensions.RemapDataMappings(mappings, _map).Should().Be(mappings);

    #endregion

    private static string Config(string expression) => $$"""{"expression":"{{expression.Replace("\"", "\\\"")}}"}""";

    private static string Expression(string config)
        => JsonNode.Parse(config)?["expression"]?.GetValue<string>();
}
