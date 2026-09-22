using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>ADR-0007 §3 "publish time" validation — every structural rule
/// <see cref="DocumentTemplateValidator"/> enforces before a version may ever become PUBLISHED,
/// proven against minimal hand-built definitions rather than the full seeded default.</summary>
public class DocumentTemplateValidatorTests
{
    private const string ValidPageSetup = """{"size":"A4","margin":{"top":22,"bottom":18,"left":14,"right":14},"header":{"repeatOn":"ALL"},"footer":{"repeatOn":"ALL"}}""";

    private static void ValidateBody(string bodyJson) =>
        DocumentTemplateValidator.Validate($$"""{"theme":{},"body":{{bodyJson}}}""", ValidPageSetup);

    [Fact]
    public void Rejects_a_page_setup_missing_size()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"header":{}}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:size");
    }

    [Fact]
    public void Rejects_an_unknown_repeatOn_value()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","header":{"repeatOn":"SOMETIMES"}}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:repeatOn:SOMETIMES");
    }

    // ---- F-02 (S7 final-findings correction): page_setup size/orientation/margin are authoritative ----

    [Fact]
    public void Accepts_the_A4_and_LETTER_named_sizes()
    {
        var a4 = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4"}""");
        var letter = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"LETTER"}""");
        a4.Should().NotThrow();
        letter.Should().NotThrow();
    }

    [Fact]
    public void Rejects_an_unknown_size_value()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"LEGAL"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:size:LEGAL");
    }

    [Fact]
    public void Rejects_CUSTOM_size_without_widthMm_and_heightMm()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"CUSTOM"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:widthMm/heightMm");
    }

    [Fact]
    public void Rejects_CUSTOM_size_with_a_non_positive_dimension()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"CUSTOM","widthMm":100,"heightMm":0}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:widthMm/heightMm");
    }

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2000, 1)]
    public void Accepts_CUSTOM_dimensions_at_the_renderer_safety_bounds(decimal width, decimal height)
    {
        var pageSetup = "{\"size\":\"CUSTOM\",\"widthMm\":" + width.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"heightMm\":" + height.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", pageSetup);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.999)]
    [InlineData(2000.001)]
    public void Rejects_CUSTOM_dimensions_outside_the_renderer_safety_bounds(decimal dimension)
    {
        var pageSetup = "{\"size\":\"CUSTOM\",\"widthMm\":" + dimension.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"heightMm\":100}";
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", pageSetup);
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:widthMm/heightMm");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Rejects_a_non_string_repeatOn_value(string value)
    {
        var pageSetup = "{\"size\":\"A4\",\"header\":{\"repeatOn\":" + value + "}}";
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", pageSetup);
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:repeatOn:*");
    }

    [Fact]
    public void Accepts_CUSTOM_size_with_valid_positive_dimensions_for_a_future_label_template()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"CUSTOM","widthMm":100,"heightMm":150}""");
        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_PORTRAIT_and_LANDSCAPE_orientation()
    {
        var portrait = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","orientation":"PORTRAIT"}""");
        var landscape = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","orientation":"LANDSCAPE"}""");
        portrait.Should().NotThrow();
        landscape.Should().NotThrow();
    }

    [Fact]
    public void Rejects_an_unknown_orientation_value()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","orientation":"SIDEWAYS"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:orientation:SIDEWAYS");
    }

    [Fact]
    public void Rejects_a_negative_margin_value()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","margin":{"top":-5}}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:margin.top");
    }

    [Fact]
    public void Rejects_a_non_numeric_margin_value()
    {
        var act = () => DocumentTemplateValidator.Validate("""{"theme":{},"body":[]}""", """{"size":"A4","margin":{"left":"wide"}}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:margin.left");
    }

    [Fact]
    public void Rejects_an_unknown_block_type_in_the_body()
    {
        var act = () => ValidateBody("""[{"type":"RawHtml","html":"<script>alert(1)</script>"}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_UNKNOWN_BLOCK_TYPE:RawHtml");
    }

    [Fact]
    public void Rejects_a_body_only_block_type_placed_in_a_header_or_footer_region()
    {
        // ItemsTable/Totals/RichText/TechnicalHighlight/SectionHeader are body-only — the region
        // vocabulary is deliberately smaller (Logo/Text/DynamicField/PageNumber).
        var act = () => DocumentTemplateValidator.Validate(
            """{"theme":{},"header":{"blocks":[{"type":"ItemsTable","columns":[{"binding":"item.description","label":"Item"}]}]},"body":[]}""",
            ValidPageSetup);
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_BLOCK_NOT_ALLOWED_IN_REGION:ItemsTable");
    }

    [Fact]
    public void Rejects_an_ItemsTable_block_with_no_columns()
    {
        var act = () => ValidateBody("""[{"type":"ItemsTable","columns":[]}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_ITEMS_TABLE_COLUMNS_REQUIRED");
    }

    [Fact]
    public void Rejects_an_ItemsTable_column_bound_to_an_unknown_or_forbidden_path()
    {
        var act = () => ValidateBody("""[{"type":"ItemsTable","columns":[{"binding":"item.unitCost","label":"Custo"}]}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage($"{RenderErrorCodes.InvalidBinding}:item.unitCost");
    }

    [Fact]
    public void Rejects_a_Totals_block_with_no_rows()
    {
        var act = () => ValidateBody("""[{"type":"Totals","rows":[]}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_TOTALS_ROWS_REQUIRED");
    }

    [Fact]
    public void Rejects_an_invalid_Logo_source()
    {
        var act = () => ValidateBody("""[{"type":"Logo","logoSource":"RANDOM_URL"}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_LOGO_SOURCE_INVALID:RANDOM_URL");
    }

    [Fact]
    public void Rejects_a_DynamicField_bound_to_internal_notes()
    {
        var act = () => ValidateBody("""[{"type":"DynamicField","binding":"quote.internalNotes"}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage($"{RenderErrorCodes.InvalidBinding}:quote.internalNotes");
    }

    [Fact]
    public void Rejects_a_malformed_block_with_no_type()
    {
        var act = () => ValidateBody("""[{"binding":"quote.number"}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_MALFORMED_BLOCK");
    }

    [Fact]
    public void Rejects_visibleWhen_with_no_operator()
    {
        var act = () => ValidateBody("""[{"type":"Text","text":"x","visibleWhen":{"path":"quote.number"}}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
    }

    [Fact]
    public void Rejects_visibleWhen_nested_beyond_the_depth_limit()
    {
        var act = () => ValidateBody(
            """[{"type":"Text","text":"x","visibleWhen":{"all":[{"any":[{"all":[{"path":"quote.number","operator":"IS_NOT_EMPTY"}]}]}]}}]""");
        act.Should().Throw<InvalidOperationException>().WithMessage("DOCUMENT_TEMPLATE_VISIBLE_WHEN_TOO_DEEP");
    }

    [Fact]
    public void Accepts_a_minimal_well_formed_body()
    {
        var act = () => ValidateBody("""[{"type":"Text","text":"Ola"},{"type":"DynamicField","binding":"quote.number"}]""");
        act.Should().NotThrow();
    }
}
