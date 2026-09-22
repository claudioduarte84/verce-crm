using System.Text.Json;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3.1: the closed <c>visibleWhen</c> predicate grammar — declarative, non-executable,
/// nesting depth &le; 3. A template is data a reviewer can read, never a program (the same
/// reasoning ADR-0005 applies to fee rules). Evaluated against the generic
/// <see cref="IRenderContext"/> only — no reflection, no expression compilation, no scripting.
/// </summary>
public static class VisibleWhenEvaluator
{
    private static readonly HashSet<string> ScalarOperators = new(StringComparer.Ordinal)
        { "IS_NULL", "IS_NOT_NULL", "IS_EMPTY", "IS_NOT_EMPTY", "EQUALS", "NOT_EQUALS", "GT", "GTE", "LT", "LTE" };

    public const int MaxDepth = 3;

    /// <summary>Returns <c>true</c> when no <c>visibleWhen</c> node is present at all (a block
    /// with no condition is always visible) — the caller passes <c>null</c> for that case.</summary>
    public static bool Evaluate(JsonElement? visibleWhen, IRenderContext context)
    {
        if (visibleWhen is not { } node) return true;
        return EvaluateNode(node, context, depth: 1);
    }

    private static bool EvaluateNode(JsonElement node, IRenderContext context, int depth)
    {
        if (depth > MaxDepth) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_TOO_DEEP");
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");

        if (node.TryGetProperty("all", out var allNode))
        {
            if (allNode.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
            foreach (var child in allNode.EnumerateArray())
                if (!EvaluateNode(child, context, depth + 1)) return false;
            return true;
        }

        if (node.TryGetProperty("any", out var anyNode))
        {
            if (anyNode.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
            foreach (var child in anyNode.EnumerateArray())
                if (EvaluateNode(child, context, depth + 1)) return true;
            return false;
        }

        // Leaf predicate: { "path": "...", "operator": "...", "value": <optional> }
        if (!node.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
        if (!node.TryGetProperty("operator", out var operatorNode) || operatorNode.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");

        var path = pathNode.GetString()!;
        var op = operatorNode.GetString()!;
        if (!ScalarOperators.Contains(op)) throw new InvalidOperationException($"DOCUMENT_TEMPLATE_VISIBLE_WHEN_OPERATOR_UNKNOWN:{op}");

        var isCollection = QuoteBindingCatalogue.IsKnownCollection(path);
        object? scalarValue = null;
        IReadOnlyList<IRenderContext>? collectionValue = null;
        if (isCollection) collectionValue = context.ResolveCollection(path);
        else scalarValue = context.ResolveScalar(path);
        var isEmpty = RenderValueRules.IsEmpty(scalarValue, collectionValue);

        return op switch
        {
            "IS_NULL" => scalarValue is null,
            "IS_NOT_NULL" => scalarValue is not null,
            "IS_EMPTY" => isEmpty,
            "IS_NOT_EMPTY" => !isEmpty,
            "EQUALS" => CompareEquals(scalarValue, node),
            "NOT_EQUALS" => !CompareEquals(scalarValue, node),
            "GT" => CompareNumeric(scalarValue, node) is { } c && c > 0,
            "GTE" => CompareNumeric(scalarValue, node) is { } c && c >= 0,
            "LT" => CompareNumeric(scalarValue, node) is { } c && c < 0,
            "LTE" => CompareNumeric(scalarValue, node) is { } c && c <= 0,
            _ => throw new InvalidOperationException($"DOCUMENT_TEMPLATE_VISIBLE_WHEN_OPERATOR_UNKNOWN:{op}"),
        };
    }

    private static bool CompareEquals(object? scalarValue, JsonElement node)
    {
        if (!node.TryGetProperty("value", out var valueNode)) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
        var expected = valueNode.ValueKind == JsonValueKind.String ? valueNode.GetString() : valueNode.ToString();
        return string.Equals(scalarValue?.ToString(), expected, StringComparison.Ordinal);
    }

    private static int? CompareNumeric(object? scalarValue, JsonElement node)
    {
        if (!node.TryGetProperty("value", out var valueNode)) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
        var expected = valueNode.GetDecimal();
        var actual = scalarValue switch
        {
            decimal d => d,
            int i => (decimal)i,
            null => (decimal?)null,
            _ => throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_NOT_NUMERIC"),
        };
        return actual is null ? null : actual.Value.CompareTo(expected);
    }
}
