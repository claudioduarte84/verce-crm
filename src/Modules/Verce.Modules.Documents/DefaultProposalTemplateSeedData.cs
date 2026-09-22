using System.Text.Json.Nodes;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §7: the `VERCE | Proposta Comercial Padrão` block tree, page setup and theme tokens
/// — ORDINARY DATA, built once at seed time and serialized to the
/// <see cref="DocumentTemplateVersion"/> row. Nothing here is renderer code; deleting this class
/// and re-typing the same JSON by hand into the seed service would change nothing about how a
/// document renders. Mirrors <see cref="DEFAULT-PROPOSAL-TEMPLATE.md"/> §2 verbatim, block for
/// block. Visual tokens marked `‹extract›` in that document remain documented placeholders here
/// (ARCHITECTURE-DEBT) — the reference PDF was never available to extract them from.
/// </summary>
public static class DefaultProposalTemplateSeedData
{
    public const int SchemaVersion = 1;

    public static string BuildDefinitionJson()
    {
        var root = new JsonObject
        {
            ["theme"] = BuildTheme(),
            ["header"] = BuildHeaderRegion(),
            ["body"] = BuildBody(),
            ["footer"] = BuildFooterRegion(),
        };
        return root.ToJsonString();
    }

    public static string BuildPageSetupJson()
    {
        var root = new JsonObject
        {
            ["size"] = "A4",
            ["orientation"] = "PORTRAIT",
            ["margin"] = new JsonObject { ["top"] = 22, ["bottom"] = 18, ["left"] = 14, ["right"] = 14 },
            ["header"] = new JsonObject { ["repeatOn"] = "ALL" },
            ["footer"] = new JsonObject { ["repeatOn"] = "ALL" },
        };
        return root.ToJsonString();
    }

    /// <summary>DEFAULT-PROPOSAL-TEMPLATE §6: visual token placeholders. The self-hosted font
    /// family names ARE resolved (see ARCHITECTURE-DEBT) — colour/spacing tokens remain
    /// documented `‹extract›` placeholders pending the still-absent reference PDF.</summary>
    private static JsonObject BuildTheme() => new()
    {
        ["colorPrimary"] = "#C2410C",
        ["colorText"] = "#14181D",
        ["colorMuted"] = "#55606B",
        ["colorRule"] = "#DDD8CF",
        // F-05 (S7 final-findings correction): the fallback after the embedded, self-hosted
        // family must never name a specific host font (Arial, Segoe UI, Roboto, -apple-system) —
        // only the generic CSS keyword "sans-serif", so no host-font dependency remains even in
        // the theoretical case the embedded @font-face somehow failed to load.
        ["fontHeading"] = "'Archivo',sans-serif",
        ["fontBody"] = "'Inter',sans-serif",
    };

    /// <summary>§2.1 — Page header, `repeatOn: ALL`.</summary>
    private static JsonObject BuildHeaderRegion() => new()
    {
        ["repeatOn"] = "ALL",
        ["blocks"] = new JsonArray
        {
            new JsonObject { ["type"] = "Logo", ["logoSource"] = "INHERIT_DEFAULT" },
            new JsonObject { ["type"] = "Text", ["text"] = "PROPOSTA COMERCIAL" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "quote.number" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "quote.date" },
        },
    };

    /// <summary>§2.9 — Page footer, `repeatOn: ALL`.</summary>
    private static JsonObject BuildFooterRegion() => new()
    {
        ["repeatOn"] = "ALL",
        ["blocks"] = new JsonArray
        {
            new JsonObject { ["type"] = "Logo", ["logoSource"] = "SPECIFIC_ASSET", ["assetRole"] = "COMPACT_LOGO" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "company.website" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "company.email" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "company.instagram" },
            new JsonObject { ["type"] = "DynamicField", ["binding"] = "company.phone" },
            new JsonObject { ["type"] = "PageNumber" },
        },
    };

    private static JsonArray BuildBody()
    {
        var body = new JsonArray();

        // §2.2 — Project.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "PROJETO", ["visibleWhen"] = VisibleWhen("quote.title", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "quote.title", ["visibleWhen"] = VisibleWhen("quote.title", "IS_NOT_EMPTY") });

        // §2.3 — Customer.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "CLIENTE" });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "customer.name" });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "customer.document", ["visibleWhen"] = VisibleWhen("customer.document", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "customer.contact", ["visibleWhen"] = VisibleWhen("customer.contact", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "customer.email", ["visibleWhen"] = VisibleWhen("customer.email", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "customer.phone", ["visibleWhen"] = VisibleWhen("customer.phone", "IS_NOT_EMPTY") });

        // §2.4 — Scope.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "ESCOPO", ["visibleWhen"] = VisibleWhen("quote.scope", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "RichText", ["binding"] = "quote.scope", ["visibleWhen"] = VisibleWhen("quote.scope", "IS_NOT_EMPTY") });

        // §2.5 — Technical information.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "INFORMAÇÕES TÉCNICAS", ["visibleWhen"] = VisibleWhen("quote.technicalHighlights[]", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "TechnicalHighlight", ["binding"] = "quote.technicalHighlights[]", ["columns"] = 2, ["visibleWhen"] = VisibleWhen("quote.technicalHighlights[]", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "RichText", ["binding"] = "quote.technicalNotes", ["visibleWhen"] = VisibleWhen("quote.technicalNotes", "IS_NOT_EMPTY") });

        // §2.6 — Investment.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "INVESTIMENTO" });
        body.Add(new JsonObject
        {
            ["type"] = "ItemsTable",
            ["binding"] = "items[]",
            ["repeatHeaderOnEachPage"] = true,
            ["emptyBehavior"] = "HIDE_BLOCK",
            ["columns"] = new JsonArray
            {
                new JsonObject { ["binding"] = "item.name", ["label"] = "Item", ["align"] = "left" },
                new JsonObject { ["binding"] = "item.quantity", ["label"] = "Qtd.", ["align"] = "right", ["format"] = "QUANTITY" },
                new JsonObject { ["binding"] = "item.unitPrice", ["label"] = "Valor unit.", ["align"] = "right", ["format"] = "MONEY" },
                new JsonObject { ["binding"] = "item.lineTotal", ["label"] = "Total", ["align"] = "right", ["format"] = "MONEY" },
            },
        });
        body.Add(new JsonObject
        {
            ["type"] = "Totals",
            ["rows"] = new JsonArray
            {
                new JsonObject { ["binding"] = "subtotal", ["label"] = "Subtotal" },
                new JsonObject { ["binding"] = "discount", ["label"] = "Desconto", ["visibleWhen"] = new JsonObject { ["path"] = "discount", ["operator"] = "GT", ["value"] = 0 } },
                new JsonObject { ["binding"] = "total", ["label"] = "Total", ["emphasis"] = true },
            },
        });

        // §2.7 — Terms.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "CONDIÇÕES" });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "deliveryTerms", ["visibleWhen"] = VisibleWhen("deliveryTerms", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "paymentTerms", ["visibleWhen"] = VisibleWhen("paymentTerms", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "quote.validUntil" });
        body.Add(new JsonObject { ["type"] = "DynamicField", ["binding"] = "warranty", ["visibleWhen"] = VisibleWhen("warranty", "IS_NOT_EMPTY") });

        // §2.8 — Out of scope / notes.
        body.Add(new JsonObject { ["type"] = "Header", ["text"] = "NÃO INCLUSO", ["visibleWhen"] = VisibleWhen("outOfScope", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "RichText", ["binding"] = "outOfScope", ["visibleWhen"] = VisibleWhen("outOfScope", "IS_NOT_EMPTY") });
        body.Add(new JsonObject { ["type"] = "RichText", ["binding"] = "quote.notes", ["visibleWhen"] = VisibleWhen("quote.notes", "IS_NOT_EMPTY") });

        return body;
    }

    private static JsonObject VisibleWhen(string path, string op) => new() { ["path"] = path, ["operator"] = op };
}
