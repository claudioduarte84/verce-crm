using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>
/// SECURITY §8 / ADR-0007 §3: the QUOTE binding catalogue is a security boundary — every
/// deliberately-excluded path must be structurally impossible to resolve, never merely absent
/// from a convenience list. Proven two ways: (1) the catalogue itself never lists them, and
/// (2) attempting to render a template that references one throws
/// <see cref="RenderErrorCodes.InvalidBinding"/> rather than silently printing blank.
/// </summary>
public class QuoteBindingCatalogueTests
{
    [Theory]
    [MemberData(nameof(ForbiddenPaths))]
    public void Forbidden_paths_are_never_present_in_the_catalogue(string path)
    {
        QuoteBindingCatalogue.IsKnownPath(path).Should().BeFalse($"'{path}' must be structurally impossible to bind — it exposes internal cost/margin/commission data");
    }

    public static IEnumerable<object[]> ForbiddenPaths() => QuoteBindingCatalogue.DeliberatelyExcluded.Select(p => new object[] { p });

    [Theory]
    [InlineData("company.name")]
    [InlineData("quote.number")]
    [InlineData("customer.name")]
    [InlineData("subtotal")]
    [InlineData("paymentTerms")]
    [InlineData("item.unitPrice")]
    public void Every_DEFAULT_PROPOSAL_TEMPLATE_path_is_known(string path)
    {
        QuoteBindingCatalogue.IsKnownPath(path).Should().BeTrue();
    }

    [Fact]
    public void Unknown_path_is_rejected_by_the_generic_context_never_silently_blank()
    {
        var input = QuotePdfHtmlTemplateTests.NewInput();
        var context = new QuoteRenderContext(input);
        var act = () => context.ResolveScalar("quote.internalNotes");
        act.Should().Throw<InvalidOperationException>().WithMessage($"{RenderErrorCodes.InvalidBinding}:quote.internalNotes");
    }

    [Fact]
    public void Unknown_path_is_rejected_at_template_validation_time_too()
    {
        var badDefinition = """{"body":[{"type":"DynamicField","binding":"item.estimatedUnitCost"}]}""";
        var act = () => DocumentTemplateValidator.Validate(badDefinition, DefaultProposalTemplateSeedData.BuildPageSetupJson());
        act.Should().Throw<InvalidOperationException>().WithMessage($"{RenderErrorCodes.InvalidBinding}:item.estimatedUnitCost");
    }

    [Fact]
    public void Every_seeded_default_proposal_binding_path_validates_successfully()
    {
        // mission §78: the seed must already satisfy every validation rule the future S14
        // publish workflow will enforce.
        var act = () => DocumentTemplateValidator.Validate(DefaultProposalTemplateSeedData.BuildDefinitionJson(), DefaultProposalTemplateSeedData.BuildPageSetupJson());
        act.Should().NotThrow();
    }
}
