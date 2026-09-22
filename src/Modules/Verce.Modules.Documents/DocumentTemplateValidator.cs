using System.Text.Json;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3 "publish time" validation (mission §77): every binding path, block type,
/// <c>visibleWhen</c> node and structural requirement is checked BEFORE a version may become
/// PUBLISHED — an unknown/forbidden path never reaches render time as a silent blank
/// (<see cref="RenderErrorCodes.InvalidBinding"/>). S7 has no draft-authoring workflow, so this
/// runs once, at seed time, against the hand-built default proposal — S14's publish action will
/// call the SAME validator.
/// </summary>
public static class DocumentTemplateValidator
{
    // Renderer-safety bounds, not business paper-size rules: avoid pathological browser/PDF
    // allocations while retaining normal labels through large-format technical documents.
    private const decimal MinimumCustomDimensionMm = 1m;
    private const decimal MaximumCustomDimensionMm = 2000m;
    private static readonly HashSet<string> KnownBlockTypes = new(StringComparer.Ordinal)
        { "Logo", "Text", "Header", "DynamicField", "RichText", "TechnicalHighlight", "ItemsTable", "Totals", "PageNumber" };
    private static readonly HashSet<string> RegionBlockTypes = new(StringComparer.Ordinal)
        { "Logo", "Text", "DynamicField", "PageNumber" };

    public static void Validate(string definitionJson, string pageSetupJson)
    {
        using var definitionDoc = JsonDocument.Parse(definitionJson);
        using var pageSetupDoc = JsonDocument.Parse(pageSetupJson);
        var definition = definitionDoc.RootElement;
        var pageSetup = pageSetupDoc.RootElement;

        ValidatePageSetup(pageSetup);
        ValidateTheme(definition);
        ValidateRegion(definition, "header");
        ValidateRegion(definition, "footer");
        ValidateBody(definition);
    }

    /// <summary>F-02 (S7 final-findings correction): <c>page_setup</c> is the authoritative
    /// render contract, not a set of hints the renderer may accept-and-ignore — ADR-0007 §3.3
    /// ("size (A4 default, or custom millimetres for labels), orientation, margins... repeatOn").
    /// Every accepted value here is one <see cref="BlockTreeRenderer"/> genuinely honors; nothing
    /// is validated here that the renderer would then silently drop.</summary>
    private static void ValidatePageSetup(JsonElement pageSetup)
    {
        if (pageSetup.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID");

        if (!pageSetup.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:size");
        var sizeValue = size.GetString();
        if (sizeValue is "A4" or "LETTER")
        {
            // A named preset — Playwright maps it directly (PagePdfOptions.Format); no custom
            // width/height is read or required alongside it.
        }
        else if (sizeValue == "CUSTOM")
        {
            if (!TryGetBoundedMillimetres(pageSetup, "widthMm", out _) || !TryGetBoundedMillimetres(pageSetup, "heightMm", out _))
                throw new InvalidOperationException("DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:widthMm/heightMm");
        }
        else
        {
            throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:size:{sizeValue}");
        }

        if (pageSetup.TryGetProperty("orientation", out var orientation))
        {
            if (orientation.ValueKind != JsonValueKind.String || orientation.GetString() is not ("PORTRAIT" or "LANDSCAPE"))
                throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:orientation:{(orientation.ValueKind == JsonValueKind.String ? orientation.GetString() : orientation.ValueKind)}");
        }

        if (pageSetup.TryGetProperty("margin", out var margin) && margin.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "top", "bottom", "left", "right" })
            {
                if (!margin.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Number || value.GetDecimal() < 0)
                    throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:margin.{key}");
            }
        }

        foreach (var regionKey in new[] { "header", "footer" })
        {
            if (!pageSetup.TryGetProperty(regionKey, out var region)) continue;
            if (region.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:{regionKey}");
            if (region.TryGetProperty("repeatOn", out var repeatOn))
            {
                if (repeatOn.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:repeatOn:{repeatOn.ValueKind}");
                var value = repeatOn.GetString();
                if (value is not ("ALL" or "ALL_EXCEPT_FIRST" or "FIRST_ONLY" or "NONE"))
                    throw new InvalidOperationException($"DOCUMENT_TEMPLATE_PAGE_SETUP_INVALID:repeatOn:{value}");
            }
        }
    }

    private static bool TryGetBoundedMillimetres(JsonElement pageSetup, string property, out decimal value)
    {
        value = 0;
        if (!pageSetup.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Number) return false;
        value = node.GetDecimal();
        return value >= MinimumCustomDimensionMm && value <= MaximumCustomDimensionMm;
    }

    private static void ValidateTheme(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("DOCUMENT_TEMPLATE_DEFINITION_INVALID");
        if (definition.TryGetProperty("theme", out var theme) && theme.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_DEFINITION_INVALID:theme");
    }

    private static void ValidateRegion(JsonElement definition, string regionKey)
    {
        if (!definition.TryGetProperty(regionKey, out var region)) return;
        if (region.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"DOCUMENT_TEMPLATE_DEFINITION_INVALID:{regionKey}");
        if (!region.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"DOCUMENT_TEMPLATE_DEFINITION_INVALID:{regionKey}.blocks");
        foreach (var block in blocks.EnumerateArray())
            ValidateBlock(block, RegionBlockTypes, isRegionBlock: true);
    }

    private static void ValidateBody(JsonElement definition)
    {
        if (!definition.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_DEFINITION_INVALID:body");
        foreach (var block in body.EnumerateArray())
            ValidateBlock(block, KnownBlockTypes, isRegionBlock: false);
    }

    private static void ValidateBlock(JsonElement block, HashSet<string> allowedTypes, bool isRegionBlock)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_MALFORMED_BLOCK");
        var type = typeNode.GetString()!;
        if (!allowedTypes.Contains(type))
            throw new InvalidOperationException(isRegionBlock
                ? $"DOCUMENT_TEMPLATE_BLOCK_NOT_ALLOWED_IN_REGION:{type}"
                : $"DOCUMENT_TEMPLATE_UNKNOWN_BLOCK_TYPE:{type}");

        if (block.TryGetProperty("binding", out var bindingNode) && bindingNode.ValueKind == JsonValueKind.String)
            ValidateBindingPath(bindingNode.GetString()!, type);

        if (type == "ItemsTable")
        {
            if (!block.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() == 0)
                throw new InvalidOperationException("DOCUMENT_TEMPLATE_ITEMS_TABLE_COLUMNS_REQUIRED");
            foreach (var column in columns.EnumerateArray())
            {
                if (!column.TryGetProperty("binding", out var colBinding) || colBinding.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("DOCUMENT_TEMPLATE_MALFORMED_BLOCK:column.binding");
                ValidateBindingPath(colBinding.GetString()!, "ItemsTable.column");
                if (!column.TryGetProperty("label", out var colLabel) || colLabel.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("DOCUMENT_TEMPLATE_MALFORMED_BLOCK:column.label");
            }
        }

        if (type == "Totals")
        {
            if (!block.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                throw new InvalidOperationException("DOCUMENT_TEMPLATE_TOTALS_ROWS_REQUIRED");
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("binding", out var rowBinding) || rowBinding.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("DOCUMENT_TEMPLATE_MALFORMED_BLOCK:row.binding");
                ValidateBindingPath(rowBinding.GetString()!, "Totals.row");
                if (row.TryGetProperty("visibleWhen", out var rowVw)) ValidateVisibleWhen(rowVw, depth: 1);
            }
        }

        if (type == "Logo" && block.TryGetProperty("logoSource", out var logoSource) && logoSource.ValueKind == JsonValueKind.String)
        {
            var source = logoSource.GetString();
            if (source is not ("INHERIT_DEFAULT" or "SPECIFIC_ASSET"))
                throw new InvalidOperationException($"DOCUMENT_TEMPLATE_LOGO_SOURCE_INVALID:{source}");
        }

        if (block.TryGetProperty("visibleWhen", out var visibleWhen))
            ValidateVisibleWhen(visibleWhen, depth: 1);
    }

    private static void ValidateBindingPath(string path, string context)
    {
        var isCollectionBlock = context is "TechnicalHighlight" or "ItemsTable" && path.EndsWith("[]", StringComparison.Ordinal);
        var known = isCollectionBlock ? QuoteBindingCatalogue.IsKnownCollection(path) : QuoteBindingCatalogue.IsKnownPath(path);
        if (!known) throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}");
    }

    /// <summary>ADR-0007 §3.1: closed operator set, <c>all</c>/<c>any</c> grouping,
    /// nesting depth &le; 3 — validated structurally here (mirrors
    /// <see cref="VisibleWhenEvaluator"/>'s own depth guard, checked again at every render as
    /// defense in depth).</summary>
    private static void ValidateVisibleWhen(JsonElement node, int depth)
    {
        if (depth > VisibleWhenEvaluator.MaxDepth) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_TOO_DEEP");
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");

        if (node.TryGetProperty("all", out var all) || node.TryGetProperty("any", out all))
        {
            if (all.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
            foreach (var child in all.EnumerateArray()) ValidateVisibleWhen(child, depth + 1);
            return;
        }

        if (!node.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
        if (!node.TryGetProperty("operator", out var opNode) || opNode.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_VISIBLE_WHEN_INVALID");
        ValidateBindingPath(pathNode.GetString()!, "visibleWhen");
    }
}
