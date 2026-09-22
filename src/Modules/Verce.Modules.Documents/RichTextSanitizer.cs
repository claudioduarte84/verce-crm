using System.Net;
using System.Text;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3 / SECURITY §8: the fixed allow-list sanitizer for <see cref="BindingKind.RichText"/>
/// values — "bold, italic, lists, paragraphs, line breaks... never raw HTML". The authoritative
/// allow-list (S7 final-findings correction F-04) is exactly seven tags, no attributes on any of
/// them, ever: <c>p, br, strong, em, ul, ol, li</c>.
///
/// Uses a small, provably-bounded hand-written tokenizer — never a general HTML parser and never
/// a broad regex replacement over the raw string (SECURITY-sensitive parsing must never guess at
/// HTML semantics via pattern matching). The tokenizer makes exactly one linear pass: every
/// character is either inside literal text or inside a tag delimiter scan; there is no
/// backtracking beyond a single quoted-attribute lookahead, so it terminates in O(n) and cannot
/// be driven into pathological behaviour by adversarial input.
///
/// Today's real caller (the proposal-content `&lt;textarea&gt;` fields) never sends any markup at
/// all — S7 ships no rich-text AUTHORING surface — so the tokenizer's "no tag detected" path
/// preserves the exact plain-text behaviour this sanitizer always had: blank-line-separated runs
/// become <c>&lt;p&gt;</c> paragraphs, single newlines within one become <c>&lt;br&gt;</c>. The
/// allow-list tokenizer path exists for correctness against the DOCUMENTED contract regardless of
/// what today's UI happens to send, and for whatever future authoring surface (S14+) eventually
/// produces real inline markup.
/// </summary>
public static class RichTextSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "br", "strong", "em", "ul", "ol", "li" };

    public static string ToSafeHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (!ContainsAnyTagLikeConstruct(normalized)) return ToSafeHtmlFromPlainText(normalized);
        return Tokenize(normalized);
    }

    /// <summary>The plain-text path, unchanged from before F-04: no HTML parser ever runs over
    /// this input because there is nothing tag-shaped in it to parse — every character is
    /// HTML-encoded and only the sanitizer's OWN <c>&lt;p&gt;</c>/<c>&lt;br&gt;</c> structure,
    /// derived from the plain text's own newlines, is ever emitted as live markup.</summary>
    private static string ToSafeHtmlFromPlainText(string normalized)
    {
        var paragraphs = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paragraphs.Length == 0) return string.Empty;

        var html = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var lines = paragraph.Split('\n');
            var encodedLines = lines.Select(WebUtility.HtmlEncode);
            html.Append("<p>").Append(string.Join("<br>", encodedLines)).Append("</p>");
        }
        return html.ToString();
    }

    /// <summary>Cheap upfront check: is there anything in this string that COULD be a tag at all
    /// (a <c>&lt;</c> followed eventually by a letter or <c>/</c>)? If not, the tokenizer's tag
    /// scan can never fire and the plain-text path is both correct and cheaper.</summary>
    private static bool ContainsAnyTagLikeConstruct(string value)
    {
        for (var i = 0; i < value.Length - 1; i++)
        {
            if (value[i] != '<') continue;
            var next = value[i + 1];
            if (char.IsLetter(next) || next == '/') return true;
        }
        return false;
    }

    /// <summary>The allow-list tokenizer: a single forward pass. At every position we are either
    /// copying literal text (HTML-encoded) or scanning a candidate tag; a candidate that turns
    /// out not to be a real, allowed tag is emitted as HTML-encoded literal text instead —
    /// unsafe/unknown markup is neutralized by encoding, never by silently deleting it and never
    /// by executing it.</summary>
    private static string Tokenize(string value)
    {
        var output = new StringBuilder(value.Length + 16);
        var i = 0;
        while (i < value.Length)
        {
            var lt = value.IndexOf('<', i);
            if (lt < 0)
            {
                output.Append(WebUtility.HtmlEncode(value[i..]));
                break;
            }
            if (lt > i) output.Append(WebUtility.HtmlEncode(value[i..lt]));

            var tagEnd = FindTagEnd(value, lt);
            if (tagEnd < 0)
            {
                // No terminating '>' for this '<' before end of string (or before the next
                // plausible tag start) — malformed input degrades safely: the '<' is just text.
                output.Append(WebUtility.HtmlEncode("<"));
                i = lt + 1;
                continue;
            }

            var rawTag = value[lt..(tagEnd + 1)]; // includes surrounding '<' and '>'
            if (TryGetAllowedTagName(rawTag, out var tagName, out var isClosing))
                output.Append('<').Append(isClosing ? "/" : "").Append(tagName).Append('>');
            else
                output.Append(WebUtility.HtmlEncode(rawTag));

            i = tagEnd + 1;
        }
        return output.ToString();
    }

    /// <summary>Finds the index of the '>' that terminates the tag starting at <paramref name="ltIndex"/>,
    /// tracking quoted attribute values so a '>' inside <c>title="a&gt;b"</c> is not mistaken for
    /// the tag terminator. Returns -1 (malformed/unterminated) rather than scanning unbounded —
    /// a tag may never span a newline, which caps the worst-case scan length per candidate.</summary>
    private static int FindTagEnd(string value, int ltIndex)
    {
        char? quote = null;
        for (var j = ltIndex + 1; j < value.Length; j++)
        {
            var c = value[j];
            if (c == '\n') return -1;
            if (quote is char q)
            {
                if (c == q) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') return j;
        }
        return -1;
    }

    /// <summary>Parses the tag NAME only — attributes are never inspected or carried through
    /// (F-04: "no arbitrary attributes unless authority explicitly permits them"; none do).</summary>
    private static bool TryGetAllowedTagName(string rawTag, out string tagName, out bool isClosing)
    {
        tagName = string.Empty;
        isClosing = false;
        // rawTag is "<...>" including delimiters.
        var inner = rawTag[1..^1];
        if (inner.Length == 0) return false;
        var cursor = 0;
        if (inner[0] == '/') { isClosing = true; cursor = 1; }
        var nameStart = cursor;
        while (cursor < inner.Length && char.IsLetter(inner[cursor])) cursor++;
        if (cursor == nameStart) return false; // no letters at all — not a tag name
        var name = inner[nameStart..cursor];
        // Whatever follows (whitespace + attributes, or a trailing '/' for a self-closed void
        // element like <br/>) is discarded — only the name ever survives.
        if (!AllowedTags.Contains(name)) return false;
        tagName = name.ToLowerInvariant();
        return true;
    }
}
