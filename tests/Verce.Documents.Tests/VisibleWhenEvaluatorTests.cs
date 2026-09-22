using System.Text.Json;
using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>ADR-0007 §3.1: the closed <c>visibleWhen</c> predicate grammar — every operator, the
/// <c>all</c>/<c>any</c> combinators, and the depth-3 limit.</summary>
public class VisibleWhenEvaluatorTests
{
    private sealed class FakeContext(object? scalar, IReadOnlyList<IRenderContext>? collection = null) : IRenderContext
    {
        public object? ResolveScalar(string path) => scalar;
        public IReadOnlyList<IRenderContext> ResolveCollection(string path) => collection ?? [];
        public string? ResolveLogo(string? assetRole) => null;
    }

    private static bool Eval(string json, IRenderContext context)
    {
        using var doc = JsonDocument.Parse(json);
        return VisibleWhenEvaluator.Evaluate(doc.RootElement, context);
    }

    [Theory]
    [InlineData("""{"path":"x","operator":"IS_NOT_EMPTY"}""", "value", true)]
    [InlineData("""{"path":"x","operator":"IS_NOT_EMPTY"}""", "", false)]
    [InlineData("""{"path":"x","operator":"IS_NOT_EMPTY"}""", null, false)]
    [InlineData("""{"path":"x","operator":"IS_EMPTY"}""", "", true)]
    [InlineData("""{"path":"x","operator":"IS_EMPTY"}""", "value", false)]
    [InlineData("""{"path":"x","operator":"IS_NULL"}""", null, true)]
    [InlineData("""{"path":"x","operator":"IS_NOT_NULL"}""", "value", true)]
    public void Scalar_operators_evaluate_correctly(string json, object? scalar, bool expected)
    {
        Eval(json, new FakeContext(scalar)).Should().Be(expected);
    }

    [Fact]
    public void EQUALS_and_NOT_EQUALS_compare_the_resolved_value()
    {
        Eval("""{"path":"x","operator":"EQUALS","value":"APPROVED"}""", new FakeContext("APPROVED")).Should().BeTrue();
        Eval("""{"path":"x","operator":"EQUALS","value":"APPROVED"}""", new FakeContext("SENT")).Should().BeFalse();
        Eval("""{"path":"x","operator":"NOT_EQUALS","value":"APPROVED"}""", new FakeContext("SENT")).Should().BeTrue();
    }

    [Theory]
    [InlineData("GT", 5, 3, true)]
    [InlineData("GT", 3, 5, false)]
    [InlineData("GTE", 5, 5, true)]
    [InlineData("LT", 3, 5, true)]
    [InlineData("LTE", 5, 5, true)]
    public void Numeric_operators_compare_correctly(string op, decimal actual, decimal threshold, bool expected)
    {
        Eval($$"""{"path":"discount","operator":"{{op}}","value":{{threshold}}}""", new FakeContext(actual)).Should().Be(expected);
    }

    [Fact]
    public void All_requires_every_child_to_be_true()
    {
        Eval("""{"all":[{"path":"a","operator":"IS_NOT_EMPTY"},{"path":"b","operator":"IS_NOT_EMPTY"}]}""", new FakeContext("x")).Should().BeTrue();
    }

    [Fact]
    public void Any_requires_at_least_one_child_to_be_true()
    {
        using var doc = JsonDocument.Parse("""{"any":[{"path":"a","operator":"IS_EMPTY"},{"path":"b","operator":"IS_NOT_EMPTY"}]}""");
        VisibleWhenEvaluator.Evaluate(doc.RootElement, new FakeContext("present")).Should().BeTrue(); // second branch true
    }

    [Fact]
    public void Collection_paths_resolve_emptiness_from_ResolveCollection_not_ResolveScalar()
    {
        var context = new FakeContext(scalar: "should-not-be-used", collection: [new FakeContext("row")]);
        Eval("""{"path":"quote.technicalHighlights[]","operator":"IS_NOT_EMPTY"}""", context).Should().BeTrue();

        var emptyContext = new FakeContext(scalar: "should-not-be-used", collection: []);
        Eval("""{"path":"quote.technicalHighlights[]","operator":"IS_EMPTY"}""", emptyContext).Should().BeTrue();
    }

    [Fact]
    public void Nesting_at_exactly_the_limit_is_accepted()
    {
        // depth 1: all -> depth 2: any -> depth 3: leaf. Exactly MaxDepth.
        var json = """{"all":[{"any":[{"path":"x","operator":"IS_NOT_EMPTY"}]}]}""";
        var act = () => Eval(json, new FakeContext("value"));
        act.Should().NotThrow();
    }

    [Fact]
    public void Nesting_beyond_the_limit_is_rejected()
    {
        var json = """{"all":[{"any":[{"all":[{"path":"x","operator":"IS_NOT_EMPTY"}]}]}]}""";
        var act = () => Eval(json, new FakeContext("value"));
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_VISIBLE_WHEN_TOO_DEEP");
    }

    [Fact]
    public void An_unknown_operator_is_rejected()
    {
        var act = () => Eval("""{"path":"x","operator":"CONTAINS"}""", new FakeContext("value"));
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_VISIBLE_WHEN_OPERATOR_UNKNOWN:CONTAINS");
    }

    [Fact]
    public void No_visibleWhen_node_means_always_visible()
    {
        VisibleWhenEvaluator.Evaluate(null, new FakeContext(null)).Should().BeTrue();
    }
}
