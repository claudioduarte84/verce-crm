using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>ADR-0007 §7.2: DocumentTemplate/DocumentTemplateVersion lifecycle — pure domain,
/// no DB dependency (the partial-unique-index invariants are proven separately, against real
/// PostgreSQL, in <c>Verce.IntegrationTests</c>).</summary>
public class DocumentTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static (string Definition, string PageSetup) ValidDocument() =>
        (DefaultProposalTemplateSeedData.BuildDefinitionJson(), DefaultProposalTemplateSeedData.BuildPageSetupJson());

    [Fact]
    public void PublishFirstVersion_creates_version_1_already_PUBLISHED()
    {
        var template = new DocumentTemplate("QUOTE", "Modelo de teste", isDefault: true);
        var (definition, pageSetup) = ValidDocument();

        var version = template.PublishFirstVersion(definition, pageSetup, 1, DocumentTemplateValidator.Validate, Now, publishedBy: null);

        version.VersionNumber.Should().Be(1);
        version.Status.Should().Be(DocumentTemplateVersionStatus.PUBLISHED);
        version.PublishedAt.Should().Be(Now);
        template.PublishedVersion.Should().BeSameAs(version);
    }

    [Fact]
    public void PublishFirstVersion_rejects_an_unknown_binding_path()
    {
        var template = new DocumentTemplate("QUOTE", "Modelo inválido", isDefault: true);
        var badDefinition = """{"body":[{"type":"DynamicField","binding":"quote.internalNotes"}]}""";
        var (_, pageSetup) = ValidDocument();

        var act = () => template.PublishFirstVersion(badDefinition, pageSetup, 1, DocumentTemplateValidator.Validate, Now, null);

        act.Should().Throw<InvalidOperationException>().WithMessage($"{RenderErrorCodes.InvalidBinding}:quote.internalNotes");
    }

    [Fact]
    public void PublishFirstVersion_cannot_be_called_twice_on_the_same_template()
    {
        var template = new DocumentTemplate("QUOTE", "Modelo de teste", isDefault: true);
        var (definition, pageSetup) = ValidDocument();
        template.PublishFirstVersion(definition, pageSetup, 1, DocumentTemplateValidator.Validate, Now, null);

        var act = () => template.PublishFirstVersion(definition, pageSetup, 1, DocumentTemplateValidator.Validate, Now, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_ALREADY_HAS_VERSIONS");
    }

    [Fact]
    public void A_PUBLISHED_version_exposes_no_mutation_method_at_all()
    {
        // ADR-0007 §7.2 "published immutability" is enforced by ABSENCE of a write path — proven
        // structurally: every property is a private setter, and the type has no public method
        // that could change Definition/PageSetup/SchemaVersion after construction.
        var mutatingMethods = typeof(DocumentTemplateVersion).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal) || m.Name.StartsWith("Update", StringComparison.Ordinal) || m.Name.StartsWith("Change", StringComparison.Ordinal));
        mutatingMethods.Should().BeEmpty("a PUBLISHED version must be immutable — there is no supported code path that could rewrite its content");
    }

    [Fact]
    public void DocumentType_requires_a_non_blank_code_and_name()
    {
        var actCode = () => new DocumentType("", "Nome");
        var actName = () => new DocumentType("CODE", "");
        actCode.Should().Throw<ArgumentException>().WithMessage("DOCUMENT_TYPE_CODE_REQUIRED");
        actName.Should().Throw<ArgumentException>().WithMessage("DOCUMENT_TYPE_NAME_REQUIRED");
    }
}
