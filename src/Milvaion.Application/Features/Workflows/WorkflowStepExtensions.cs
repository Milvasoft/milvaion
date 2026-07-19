using Milvaion.Application.Features.Workflows.CreateWorkflow;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Milvaion.Application.Features.Workflows;

/// <summary>
/// Extension methods for workflow step collections.
/// </summary>
public static class WorkflowStepExtensions
{
    /// <summary>
    /// Rewrites the temporary step ids inside a condition expression to the real ids the steps were saved with.
    /// </summary>
    /// <remarks>
    /// Edges get this treatment through <c>tempIdToRealId</c> already, but the same temporary ids also appear
    /// inside two payloads that used to be persisted verbatim: condition expressions
    /// (<c>step-1:@status == 'Completed'</c>) and data mappings (<c>step-1:$.total</c>).
    /// <para>
    /// Leaving them unmapped fails quietly rather than loudly, which is what made it worth fixing. The engine
    /// parses the prefix with <c>Guid.TryParse</c>; a value like <c>step-1</c> does not parse, so the whole
    /// clause is treated as targeting every parent and matches nothing. The condition then silently evaluates
    /// to a constant instead of erroring.
    /// </para>
    /// <para>
    /// Only affects newly created steps. When an existing workflow is edited the temporary id already is the
    /// real id, so the lookup maps it to itself and nothing changes.
    /// </para>
    /// </remarks>
    public static string RemapConditionExpression(string nodeConfigJson, Dictionary<string, Guid> tempIdToRealId)
    {
        if (string.IsNullOrWhiteSpace(nodeConfigJson) || tempIdToRealId is null || tempIdToRealId.Count == 0)
            return nodeConfigJson;

        // A colon also shows up inside compared values - a time of '10:30' for instance - so the prefix is only
        // rewritten when it actually matches a step in the request.
        string RemapClause(string clause)
        {
            var trimmed = clause.TrimStart();
            var colonIndex = trimmed.IndexOf(':');

            if (colonIndex <= 0)
                return clause;

            return tempIdToRealId.TryGetValue(trimmed[..colonIndex], out var realId)
                ? $"{realId}:{trimmed[(colonIndex + 1)..]}"
                : clause;
        }

        try
        {
            if (JsonNode.Parse(nodeConfigJson) is not JsonObject node)
                return nodeConfigJson;

            var expression = node["expression"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(expression))
                return nodeConfigJson;

            // Clause by clause rather than a blind string replace, for the reason above.
            var rewritten = string.Join(" || ", expression.Split(" || ").Select(orGroup
                => string.Join(" && ", orGroup.Split(" && ").Select(RemapClause))));

            // Returning the original string when nothing moved is deliberate: the update handler compares this
            // value against the stored one to decide whether to cut a new version, and a re-serialised but
            // identical payload would look like an edit.
            if (rewritten == expression)
                return nodeConfigJson;

            node["expression"] = rewritten;

            return node.ToJsonString();
        }
        catch (JsonException)
        {
            // Malformed config is left alone. It is already broken and rewriting it would only obscure that.
            return nodeConfigJson;
        }
    }

    /// <summary>
    /// Rewrites the temporary step ids used as sources in a data mapping payload.
    /// </summary>
    /// <remarks>
    /// The payload is <c>{ "sourceStepId:jsonPath": "targetJsonPath" }</c>, so the temporary id sits in the key
    /// ahead of the first colon. See <see cref="RemapConditionExpression"/> for why this matters.
    /// </remarks>
    public static string RemapDataMappings(string dataMappings, Dictionary<string, Guid> tempIdToRealId)
    {
        if (string.IsNullOrWhiteSpace(dataMappings) || tempIdToRealId is null || tempIdToRealId.Count == 0)
            return dataMappings;

        try
        {
            if (JsonNode.Parse(dataMappings) is not JsonObject mappings)
                return dataMappings;

            var remapped = new JsonObject();
            var changed = false;

            foreach (var (key, value) in mappings)
            {
                var colonIndex = key.IndexOf(':');
                var newKey = key;

                if (colonIndex > 0 && tempIdToRealId.TryGetValue(key[..colonIndex], out var realId))
                    newKey = $"{realId}:{key[(colonIndex + 1)..]}";

                // Only a key that actually moved counts as a change. An existing step is mapped to its own
                // id - that is what makes re-saving an unedited workflow a no-op - so a lookup hitting is
                // not the same as a rewrite. Treating the hit as a change re-serialised identical content,
                // which the update handler then read as an edit and cut a new version for on every save.
                if (newKey != key)
                    changed = true;

                remapped[newKey] = value?.DeepClone();
            }

            return changed ? remapped.ToJsonString() : dataMappings;
        }
        catch (JsonException)
        {
            return dataMappings;
        }
    }

    /// <summary>
    /// Validates that the step graph contains no cycles using Kahn's topological sort.
    /// Returns <see langword="true"/> if the steps form a valid DAG.
    /// </summary>
    public static bool ValidateDAG(this List<CreateWorkflowStepDto> steps, List<CreateWorkflowEdgeDto> edges)
    {
        if (steps == null || steps.Count == 0)
            return true;

        var adjacency = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var step in steps)
        {
            var id = step.TempId ?? step.GetHashCode().ToString();
            adjacency.TryAdd(id, []);
            inDegree.TryAdd(id, 0);
        }

        foreach (var edge in edges ?? [])
        {
            if (string.IsNullOrWhiteSpace(edge.SourceTempId) || string.IsNullOrWhiteSpace(edge.TargetTempId))
                continue;

            if (adjacency.TryGetValue(edge.SourceTempId, out var neighbors) && inDegree.ContainsKey(edge.TargetTempId))
            {
                neighbors.Add(edge.TargetTempId);
                inDegree[edge.TargetTempId] = inDegree.GetValueOrDefault(edge.TargetTempId) + 1;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            visited++;

            foreach (var neighbor in adjacency.GetValueOrDefault(node, []))
            {
                inDegree[neighbor]--;

                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        return visited == steps.Count;
    }
}
