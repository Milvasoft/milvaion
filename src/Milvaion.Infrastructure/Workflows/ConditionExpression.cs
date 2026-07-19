using System.Text;

namespace Milvaion.Infrastructure.Workflows;

/// <summary>
/// Parses and evaluates workflow condition expressions.
/// </summary>
/// <remarks>
/// The grammar is the usual boolean one:
/// <code>
///   expression := term ( '||' term )*
///   term       := factor ( '&amp;&amp;' factor )*
///   factor     := '(' expression ')' | clause
/// </code>
/// <para>
/// This replaces an earlier implementation that split the string on <c>" || "</c> and then on <c>" &amp;&amp; "</c>.
/// That version had two problems worth naming, because both failed silently rather than loudly.
/// </para>
/// <para>
/// It could not express <c>(A || B) &amp;&amp; C</c> at all — there was no way to group, and precedence was fixed
/// at "and binds tighter". And because it matched the separators with their surrounding spaces, an expression
/// written as <c>A&amp;&amp;B</c> was not rejected: it collapsed into one unparseable clause, which evaluated to a
/// constant. A condition that always takes the same branch looks like working software until the day the branch
/// matters.
/// </para>
/// <para>
/// Parentheses are new, so no stored expression contains them. Everything written against the old rules parses
/// identically here, since the precedence is the same.
/// </para>
/// </remarks>
internal static class ConditionExpression
{
    private enum TokenType
    {
        Clause,
        And,
        Or,
        OpenParen,
        CloseParen
    }

    private readonly record struct Token(TokenType Type, string Text);

    /// <summary>
    /// Evaluates <paramref name="expression"/>, deferring each individual clause to <paramref name="evaluateClause"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="true"/> for an empty or malformed expression. The caller treats a condition it
    /// cannot understand as "let the step run", which matches the previous behaviour: a broken condition should
    /// not silently halt a pipeline.
    /// </remarks>
    public static bool Evaluate(string expression, Func<string, bool> evaluateClause)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        try
        {
            var tokens = Tokenize(expression);

            if (tokens.Count == 0)
                return true;

            var position = 0;
            var result = ParseExpression(tokens, ref position, evaluateClause);

            // Trailing tokens mean the expression was not fully consumed - an unbalanced closing parenthesis,
            // for instance. Treated as malformed rather than silently ignoring the remainder.
            return position != tokens.Count || result;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Splits the expression into clauses, operators and parentheses.
    /// </summary>
    /// <remarks>
    /// Quote aware, so a parenthesis or an operator inside a compared value - <c>$.label == '(draft)'</c> - stays
    /// part of the clause instead of restructuring the expression around it.
    /// </remarks>
    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var buffer = new StringBuilder();
        var quote = '\0';

        void FlushClause()
        {
            var text = buffer.ToString().Trim();

            if (text.Length > 0)
                tokens.Add(new Token(TokenType.Clause, text));

            buffer.Clear();
        }

        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];

            if (quote != '\0')
            {
                buffer.Append(current);

                if (current == quote)
                    quote = '\0';

                continue;
            }

            switch (current)
            {
                case '\'':
                case '"':
                    quote = current;
                    buffer.Append(current);
                    continue;

                case '(':
                    FlushClause();
                    tokens.Add(new Token(TokenType.OpenParen, "("));
                    continue;

                case ')':
                    FlushClause();
                    tokens.Add(new Token(TokenType.CloseParen, ")"));
                    continue;

                case '&' when i + 1 < input.Length && input[i + 1] == '&':
                    FlushClause();
                    tokens.Add(new Token(TokenType.And, "&&"));
                    i++;
                    continue;

                case '|' when i + 1 < input.Length && input[i + 1] == '|':
                    FlushClause();
                    tokens.Add(new Token(TokenType.Or, "||"));
                    i++;
                    continue;

                default:
                    buffer.Append(current);
                    continue;
            }
        }

        FlushClause();

        return tokens;
    }

    /// <summary>
    /// expression := term ( '||' term )*
    /// </summary>
    /// <remarks>
    /// Both levels evaluate every operand rather than short circuiting. Clause evaluation is a pure lookup
    /// against already loaded occurrences, so there is nothing to save, and evaluating all of them keeps the
    /// result independent of operand order.
    /// </remarks>
    private static bool ParseExpression(List<Token> tokens, ref int position, Func<string, bool> evaluateClause)
    {
        var result = ParseTerm(tokens, ref position, evaluateClause);

        while (position < tokens.Count && tokens[position].Type == TokenType.Or)
        {
            position++;

            result = ParseTerm(tokens, ref position, evaluateClause) || result;
        }

        return result;
    }

    /// <summary>
    /// term := factor ( '&amp;&amp;' factor )*
    /// </summary>
    private static bool ParseTerm(List<Token> tokens, ref int position, Func<string, bool> evaluateClause)
    {
        var result = ParseFactor(tokens, ref position, evaluateClause);

        while (position < tokens.Count && tokens[position].Type == TokenType.And)
        {
            position++;

            result = ParseFactor(tokens, ref position, evaluateClause) && result;
        }

        return result;
    }

    /// <summary>
    /// factor := '(' expression ')' | clause
    /// </summary>
    private static bool ParseFactor(List<Token> tokens, ref int position, Func<string, bool> evaluateClause)
    {
        if (position >= tokens.Count)
            throw new FormatException("Unexpected end of condition expression.");

        var token = tokens[position];

        if (token.Type == TokenType.OpenParen)
        {
            position++;

            var inner = ParseExpression(tokens, ref position, evaluateClause);

            if (position >= tokens.Count || tokens[position].Type != TokenType.CloseParen)
                throw new FormatException("Unbalanced parenthesis in condition expression.");

            position++;

            return inner;
        }

        if (token.Type != TokenType.Clause)
            throw new FormatException($"Expected a clause but found '{token.Text}'.");

        position++;

        return evaluateClause(token.Text);
    }
}
