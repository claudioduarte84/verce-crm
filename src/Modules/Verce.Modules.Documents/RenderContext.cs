namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3: the formatting/authoring vocabulary a bound value may declare. Server-side
/// formatting is driven entirely by this — templates never supply format code (SECURITY §8).
/// </summary>
public enum BindingKind { Text, RichText, Currency, Date, Percent, Boolean, Collection }

/// <summary>One entry in a document type's closed binding catalogue (ADR-0007 §3) — a security
/// boundary, not a convenience: a path absent from the catalogue cannot be printed by ANY
/// template, including one an operator builds in the future Studio (S14).</summary>
public sealed record BindingDescriptor(string Path, BindingKind Kind, string Label);

/// <summary>
/// ADR-0007 §3/§19-24 (S7/S14 scope authority gate): the generic block-tree walker consumes ONLY
/// this interface — it has zero knowledge of Quoting/Customers/Settings/Production entities. A
/// document-type-specific resolver (e.g. <see cref="QuoteRenderContext"/>, assembled by the
/// composition root) implements it against whatever domain snapshot it was given.
/// </summary>
public interface IRenderContext
{
    /// <summary>Resolves a scalar/rich-text binding path to its raw value (string, decimal,
    /// DateOnly, DateTimeOffset, bool, or null) — never pre-formatted; formatting is the
    /// renderer's job, driven by <see cref="BindingKind"/> (ADR-0007 §3).</summary>
    object? ResolveScalar(string path);

    /// <summary>Resolves a collection binding path (e.g. <c>items[]</c>,
    /// <c>quote.technicalHighlights[]</c>) to a sequence of child contexts, each scoped to one
    /// row/pair — <c>ItemsTable</c>/<c>TechnicalHighlight</c> blocks walk these, resolving their
    /// OWN column bindings (e.g. <c>item.unitPrice</c>) against each child in turn.</summary>
    IReadOnlyList<IRenderContext> ResolveCollection(string path);

    /// <summary>Resolves a <c>Logo</c> block's intent (ADR-0015 §4/ADR-0016 §1) to a data URI, or
    /// null if none is configured — <paramref name="assetRole"/> null means
    /// <c>INHERIT_DEFAULT</c>; a non-null role (e.g. <c>COMPACT_LOGO</c>) means
    /// <c>SPECIFIC_ASSET</c>. Kept off the plain scalar/collection surface because logo
    /// resolution freezes a <c>BrandAssetVersion</c> id as a side effect (tracked separately by
    /// the composition root, never re-derived from the rendered HTML).</summary>
    string? ResolveLogo(string? assetRole);
}

/// <summary>Deterministic emptiness for the closed <c>visibleWhen</c> grammar (ADR-0007 §3.1):
/// null, an empty/whitespace-only string, and an empty collection are ALL "empty" — a template
/// author should never need to know which shape a given path happens to be.</summary>
public static class RenderValueRules
{
    public static bool IsEmpty(object? scalarOrNull, IReadOnlyList<IRenderContext>? collectionOrNull)
    {
        if (collectionOrNull is not null) return collectionOrNull.Count == 0;
        return scalarOrNull switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false,
        };
    }
}
