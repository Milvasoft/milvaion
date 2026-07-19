using FluentAssertions;
using Milvaion.Infrastructure.Workflows;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Unit tests for the workflow condition expression grammar.
/// </summary>
/// <remarks>
/// Clause evaluation is stubbed out here: the parser's job is deciding which clauses to evaluate and how to
/// combine them, and separating that from what a clause means keeps these tests readable. The clause names are
/// single letters mapping to fixed booleans.
/// </remarks>
public class ConditionExpressionTests
{
    /// <summary>
    /// A: true, B: false, C: true, D: false. Anything else is false.
    /// </summary>
    private static readonly Func<string, bool> _evaluate = clause => clause.Trim() switch
    {
        "A" => true,
        "B" => false,
        "C" => true,
        "D" => false,
        _ => false
    };

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("A && C", true)]
    [InlineData("A && B", false)]
    [InlineData("A || B", true)]
    [InlineData("B || D", false)]
    public void Evaluate_ShouldHandleSingleOperators(string expression, bool expected)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);

    [Theory]
    [InlineData("A && B || C", true)]      // (A && B) || C  →  false || true
    [InlineData("C || A && B", true)]      // C || (A && B)  →  true || false
    [InlineData("B && A || D", false)]     // (B && A) || D  →  false || false
    public void Evaluate_ShouldGiveAndHigherPrecedenceThanOr(string expression, bool expected)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);

    [Theory]
    [InlineData("A && (B || C)", true)]
    [InlineData("(A || B) && (C || D)", true)]
    [InlineData("(A || B) && (B || D)", false)]
    [InlineData("((A))", true)]
    [InlineData("A && (B || (C && A))", true)]
    [InlineData("(B && A) || (D && C)", false)]
    public void Evaluate_ShouldRespectParentheses(string expression, bool expected)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);

    /// <summary>
    /// The shapes the visual builder produces: a flat run of clauses with per-connector operators, optionally
    /// with a bracketed group somewhere in the middle.
    /// </summary>
    [Theory]
    [InlineData("A && C && B || D", false)]         // (A && C && B) || D
    [InlineData("A || B || D", true)]
    [InlineData("A && (B || C) && A", true)]        // bracketed group between two clauses
    [InlineData("B || A && C || D", true)]          // B || (A && C) || D
    [InlineData("(A || B) && C && A", true)]
    public void Evaluate_ShouldHandleMixedOperatorChains(string expression, bool expected)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);

    /// <summary>
    /// Parentheses change the answer, which is the whole reason they were added.
    /// </summary>
    [Fact]
    public void Evaluate_ParenthesesShouldOverridePrecedence()
    {
        // Without brackets and binds first, so C rescues the expression.
        ConditionExpression.Evaluate("A && B || C", _evaluate).Should().BeTrue();

        // With brackets the or is evaluated first, and B || C is true, so the and decides.
        ConditionExpression.Evaluate("A && (B || C)", _evaluate).Should().BeTrue();

        // The case that could not be expressed at all before.
        ConditionExpression.Evaluate("(A || B) && D", _evaluate).Should().BeFalse();
    }

    /// <summary>
    /// The previous implementation split on <c>" &amp;&amp; "</c> including the spaces, so an expression written
    /// without them collapsed into a single unparseable clause and quietly evaluated to a constant.
    /// </summary>
    [Theory]
    [InlineData("A&&B", false)]
    [InlineData("A||B", true)]
    [InlineData("A&&C", true)]
    [InlineData("  A   &&   C  ", true)]
    public void Evaluate_ShouldNotRequireSpacesAroundOperators(string expression, bool expected)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);

    /// <summary>
    /// Operators and brackets inside a quoted value belong to the clause, not to the expression structure.
    /// </summary>
    [Fact]
    public void Evaluate_ShouldNotSplitInsideQuotedValues()
    {
        var seen = new List<string>();

        ConditionExpression.Evaluate("$.label == '(draft)' && $.note == 'a || b'", clause =>
        {
            seen.Add(clause);
            return true;
        });

        seen.Should().HaveCount(2);
        seen[0].Should().Be("$.label == '(draft)'");
        seen[1].Should().Be("$.note == 'a || b'");
    }

    /// <summary>
    /// A condition the engine cannot understand must not stop the pipeline, so anything malformed evaluates to
    /// true rather than throwing or returning false.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(A && B")]
    [InlineData("A && )")]
    [InlineData(")(")]
    [InlineData("A &&")]
    [InlineData("&& A")]
    [InlineData("(((")]
    public void Evaluate_ShouldReturnTrueForEmptyOrMalformedExpressions(string expression)
        => ConditionExpression.Evaluate(expression, _evaluate).Should().BeTrue();

    /// <summary>
    /// A clause that throws is treated the same way as a malformed expression.
    /// </summary>
    [Fact]
    public void Evaluate_ShouldReturnTrueWhenClauseEvaluationThrows()
        => ConditionExpression.Evaluate("A && B", _ => throw new InvalidOperationException()).Should().BeTrue();

    /// <summary>
    /// Expressions stored before parentheses existed must keep evaluating exactly as they did, since precedence
    /// is unchanged. These are the shapes the old split based implementation produced.
    /// </summary>
    [Theory]
    [InlineData("@status == 'Completed'")]
    [InlineData("A && C")]
    [InlineData("A || B")]
    [InlineData("A && B || C && D")]
    [InlineData("B || A && C")]
    public void Evaluate_ShouldMatchLegacySplitBehaviour(string expression)
    {
        var expected = EvaluateWithLegacyRules(expression);

        ConditionExpression.Evaluate(expression, _evaluate).Should().Be(expected);
    }

    /// <summary>
    /// The old algorithm, kept here purely as the oracle for the compatibility test above.
    /// </summary>
    private static bool EvaluateWithLegacyRules(string condition)
    {
        var orGroups = condition.Split(" || ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var orGroup in orGroups)
        {
            var andClauses = orGroup.Split(" && ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (andClauses.All(clause => _evaluate(clause.Trim())))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every operand is evaluated rather than short circuiting, so the result cannot depend on operand order.
    /// </summary>
    [Fact]
    public void Evaluate_ShouldEvaluateEveryClause()
    {
        var seen = new List<string>();

        ConditionExpression.Evaluate("B && A || C && D", clause =>
        {
            seen.Add(clause);
            return _evaluate(clause);
        });

        seen.Should().BeEquivalentTo(["B", "A", "C", "D"]);
    }
}
