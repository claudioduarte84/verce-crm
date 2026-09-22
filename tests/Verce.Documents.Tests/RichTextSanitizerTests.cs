using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>ADR-0007 §3 / SECURITY §8 (S7 final-findings correction F-04): the allow-list
/// rich-text sanitizer preserves exactly <c>p, br, strong, em, ul, ol, li</c> — with no
/// attributes, ever — and neutralizes everything else, verified against the ACTUAL output.</summary>
public class RichTextSanitizerTests
{
    [Fact]
    public void Strong_is_preserved()
    {
        RichTextSanitizer.ToSafeHtml("<strong>bold</strong>").Should().Be("<strong>bold</strong>");
    }

    [Fact]
    public void Em_is_preserved()
    {
        RichTextSanitizer.ToSafeHtml("<em>italic</em>").Should().Be("<em>italic</em>");
    }

    [Fact]
    public void Lists_are_preserved()
    {
        RichTextSanitizer.ToSafeHtml("<ul><li>um</li><li>dois</li></ul>").Should().Be("<ul><li>um</li><li>dois</li></ul>");
        RichTextSanitizer.ToSafeHtml("<ol><li>um</li><li>dois</li></ol>").Should().Be("<ol><li>um</li><li>dois</li></ol>");
    }

    [Fact]
    public void Paragraphs_and_breaks_are_preserved_when_explicit()
    {
        RichTextSanitizer.ToSafeHtml("<p>Ola<br>mundo</p>").Should().Be("<p>Ola<br>mundo</p>");
    }

    [Fact]
    public void Nested_allowed_tags_are_preserved()
    {
        RichTextSanitizer.ToSafeHtml("<p><strong>Ola</strong> <em>mundo</em></p>")
            .Should().Be("<p><strong>Ola</strong> <em>mundo</em></p>");
        RichTextSanitizer.ToSafeHtml("<ul><li><strong>item</strong> normal</li></ul>")
            .Should().Be("<ul><li><strong>item</strong> normal</li></ul>");
    }

    [Fact]
    public void Script_tag_payload_is_neutralized_and_non_executable()
    {
        var safe = RichTextSanitizer.ToSafeHtml("<script>alert(1)</script>");
        safe.Should().NotContain("<script>").And.NotContain("</script>");
        safe.Should().Contain("&lt;script&gt;").And.Contain("&lt;/script&gt;");
    }

    [Fact]
    public void Img_onerror_payload_is_neutralized()
    {
        var safe = RichTextSanitizer.ToSafeHtml("<img src=x onerror=alert(1)>");
        safe.Should().NotContain("<img");
        safe.Should().Contain("&lt;img");
    }

    [Fact]
    public void Attributes_never_survive_even_on_an_allowed_tag()
    {
        var safe = RichTextSanitizer.ToSafeHtml("""<strong onclick="alert(1)" class="x">bold</strong>""");
        safe.Should().Be("<strong>bold</strong>");
        safe.Should().NotContain("onclick").And.NotContain("class=");
    }

    [Fact]
    public void An_unsafe_url_on_a_disallowed_anchor_tag_is_fully_neutralized()
    {
        var safe = RichTextSanitizer.ToSafeHtml("""<a href="javascript:alert(1)">link</a>""");
        safe.Should().NotContain("<a ").And.NotContain("<a>");
        safe.Should().Contain("&lt;a href=");
        // The whole tag survives only as inert encoded text — never a live anchor, so the
        // javascript: scheme is never parsed as a URL by anything.
    }

    [Fact]
    public void A_data_url_on_a_disallowed_tag_is_fully_neutralized()
    {
        var safe = RichTextSanitizer.ToSafeHtml("""<img src="data:text/html,<script>alert(1)</script>">""");
        safe.Should().NotContain("<img").And.NotContain("<script>");
    }

    [Fact]
    public void An_unknown_tag_is_neutralized_safely()
    {
        var safe = RichTextSanitizer.ToSafeHtml("<div>conteudo</div>");
        safe.Should().NotContain("<div>").And.NotContain("</div>");
        safe.Should().Contain("&lt;div&gt;").And.Contain("conteudo").And.Contain("&lt;/div&gt;");
    }

    [Fact]
    public void Disallowed_structural_tags_never_survive()
    {
        foreach (var tag in new[] { "iframe", "object", "embed", "svg", "video", "audio", "form", "input", "style" })
        {
            var safe = RichTextSanitizer.ToSafeHtml($"<{tag}>x</{tag}>");
            safe.Should().NotContain($"<{tag}>", $"'{tag}' must never survive as live markup").And.NotContain($"<{tag} ");
        }
    }

    [Fact]
    public void Malformed_input_produces_a_safe_result_without_throwing()
    {
        var act = () => RichTextSanitizer.ToSafeHtml("<strong>unterminated <em");
        act.Should().NotThrow();
        var safe = RichTextSanitizer.ToSafeHtml("<strong>unterminated <em");
        safe.Should().NotBeNull();
        safe.Should().NotContain("<em"); // the unterminated '<em' is never emitted as a live tag
    }

    [Fact]
    public void Unclosed_angle_bracket_degrades_to_literal_text()
    {
        var safe = RichTextSanitizer.ToSafeHtml("5 < 10 and 10 > 5");
        safe.Should().Contain("5 &lt; 10").And.Contain("10 &gt; 5");
    }

    [Fact]
    public void Only_allowed_tags_are_ever_emitted_as_live_markup()
    {
        var safe = RichTextSanitizer.ToSafeHtml("<b>bold</b> <i>italic</i> <a href=javascript:alert(1)>link</a>");
        // <b>/<i> are NOT in the authoritative allow-list (strong/em are) — they, like <a>, must
        // be neutralized, never silently accepted as synonyms.
        safe.Should().NotMatchRegex(@"<(?!/?(?:p|br|strong|em|ul|ol|li)>)[a-zA-Z]");
    }

    // ---- Plain-text path (today's real caller — no authoring surface produces tags at all) ----

    [Fact]
    public void Plain_text_survives_as_a_paragraph()
    {
        var safe = RichTextSanitizer.ToSafeHtml("Escopo do projeto.");
        safe.Should().Be("<p>Escopo do projeto.</p>");
    }

    [Fact]
    public void Blank_lines_become_separate_paragraphs_and_single_newlines_become_br()
    {
        var safe = RichTextSanitizer.ToSafeHtml("Linha 1\nLinha 2\n\nSegundo paragrafo.");
        safe.Should().Be("<p>Linha 1<br>Linha 2</p><p>Segundo paragrafo.</p>");
    }

    [Fact]
    public void Plain_text_is_still_HTML_encoded()
    {
        var safe = RichTextSanitizer.ToSafeHtml("Valor < 10 & > 5");
        safe.Should().Be("<p>Valor &lt; 10 &amp; &gt; 5</p>");
    }

    [Fact]
    public void Null_or_blank_input_produces_empty_output()
    {
        RichTextSanitizer.ToSafeHtml(null).Should().BeEmpty();
        RichTextSanitizer.ToSafeHtml("   ").Should().BeEmpty();
    }
}
