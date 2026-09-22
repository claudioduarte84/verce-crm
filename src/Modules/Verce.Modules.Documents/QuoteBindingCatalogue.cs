namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3: the closed binding catalogue for <c>DocumentType = QUOTE</c> — the exact path
/// list from DEFAULT-PROPOSAL-TEMPLATE §4, verbatim. This is a SECURITY BOUNDARY (SECURITY §8),
/// not a convenience list: a path absent here cannot be printed by ANY template, seeded now or
/// authored later in the Studio (S14), by construction rather than by reviewer vigilance.
/// Deliberately excludes EVERY cost/margin/commission field and <c>internal_notes</c> —
/// <see cref="QuoteBindingCatalogueTests"/>-style tests assert none of those paths resolves.
/// </summary>
public static class QuoteBindingCatalogue
{
    public const string DocumentTypeCode = "QUOTE";

    /// <summary>Scalar/rich-text paths — resolved by <see cref="IRenderContext.ResolveScalar"/>.</summary>
    public static readonly IReadOnlyDictionary<string, BindingDescriptor> Scalars = new Dictionary<string, BindingDescriptor>(StringComparer.Ordinal)
    {
        ["company.name"] = new("company.name", BindingKind.Text, "Nome da empresa"),
        ["company.legalName"] = new("company.legalName", BindingKind.Text, "Razão social"),
        ["company.document"] = new("company.document", BindingKind.Text, "CNPJ"),
        ["company.email"] = new("company.email", BindingKind.Text, "E-mail da empresa"),
        ["company.phone"] = new("company.phone", BindingKind.Text, "Telefone da empresa"),
        ["company.website"] = new("company.website", BindingKind.Text, "Site"),
        ["company.instagram"] = new("company.instagram", BindingKind.Text, "Instagram"),
        ["company.whatsapp"] = new("company.whatsapp", BindingKind.Text, "WhatsApp"),
        ["company.address"] = new("company.address", BindingKind.Text, "Endereço da empresa"),
        ["company.logo"] = new("company.logo", BindingKind.Text, "Logo (resolvido pelo bloco Logo)"),

        ["quote.number"] = new("quote.number", BindingKind.Text, "Número do orçamento"),
        ["quote.date"] = new("quote.date", BindingKind.Date, "Data de emissão"),
        ["quote.validUntil"] = new("quote.validUntil", BindingKind.Date, "Válido até"),
        ["quote.title"] = new("quote.title", BindingKind.Text, "Título do projeto"),
        ["quote.status"] = new("quote.status", BindingKind.Text, "Status (rótulo pt-BR)"),
        ["quote.scope"] = new("quote.scope", BindingKind.RichText, "Escopo"),
        ["quote.notes"] = new("quote.notes", BindingKind.RichText, "Observações"),
        ["quote.technicalNotes"] = new("quote.technicalNotes", BindingKind.RichText, "Notas técnicas"),

        ["customer.name"] = new("customer.name", BindingKind.Text, "Cliente"),
        ["customer.document"] = new("customer.document", BindingKind.Text, "Documento do cliente"),
        ["customer.contact"] = new("customer.contact", BindingKind.Text, "Contato do cliente"),
        ["customer.email"] = new("customer.email", BindingKind.Text, "E-mail do cliente"),
        ["customer.phone"] = new("customer.phone", BindingKind.Text, "Telefone do cliente"),

        ["subtotal"] = new("subtotal", BindingKind.Currency, "Subtotal"),
        ["discount"] = new("discount", BindingKind.Currency, "Desconto"),
        ["total"] = new("total", BindingKind.Currency, "Total"),

        ["paymentTerms"] = new("paymentTerms", BindingKind.RichText, "Condições de pagamento"),
        ["deliveryTerms"] = new("deliveryTerms", BindingKind.RichText, "Condições de entrega"),
        ["warranty"] = new("warranty", BindingKind.RichText, "Garantia"),
        ["outOfScope"] = new("outOfScope", BindingKind.RichText, "Não incluso"),

        // Row-scoped paths (valid only inside an items[]/technicalHighlights[] child context —
        // ItemsTable/TechnicalHighlight validate their OWN column/pair bindings against these).
        ["item.name"] = new("item.name", BindingKind.Text, "Item"),
        ["item.description"] = new("item.description", BindingKind.Text, "Descrição"),
        ["item.quantity"] = new("item.quantity", BindingKind.Text, "Quantidade"),
        ["item.unitPrice"] = new("item.unitPrice", BindingKind.Currency, "Preço unitário"),
        ["item.discount"] = new("item.discount", BindingKind.Currency, "Desconto do item"),
        ["item.lineTotal"] = new("item.lineTotal", BindingKind.Currency, "Total do item"),
        ["label"] = new("label", BindingKind.Text, "Rótulo (destaque técnico)"),
        ["value"] = new("value", BindingKind.Text, "Valor (destaque técnico)"),
    };

    /// <summary>Collection paths — resolved by <see cref="IRenderContext.ResolveCollection"/>;
    /// each carries the row-scoped path prefix its children resolve against.</summary>
    public static readonly IReadOnlyDictionary<string, BindingDescriptor> Collections = new Dictionary<string, BindingDescriptor>(StringComparer.Ordinal)
    {
        ["items[]"] = new("items[]", BindingKind.Collection, "Itens do orçamento"),
        ["quote.technicalHighlights[]"] = new("quote.technicalHighlights[]", BindingKind.Collection, "Destaques técnicos"),
    };

    public static bool IsKnownScalar(string path) => Scalars.ContainsKey(path);
    public static bool IsKnownCollection(string path) => Collections.ContainsKey(path);
    public static bool IsKnownPath(string path) => IsKnownScalar(path) || IsKnownCollection(path);

    /// <summary>SECURITY §8 / ADR-0007 §3: the deliberately-excluded set, restated as an
    /// executable assertion rather than only a comment — <c>QuoteBindingCatalogueTests</c> pins
    /// every one of these to never resolve. Never used at runtime; documentation + test fixture.</summary>
    public static readonly IReadOnlyList<string> DeliberatelyExcluded =
    [
        "quote.internalNotes", "internalNotes",
        "item.unitCost", "item.estimatedUnitCost", "item.cost",
        "item.desiredMarginPercent", "item.margin", "item.effectiveMarginPercent",
        "item.commissionPercent", "item.commissionAmount",
        "item.allocatedOrderFee", "item.fixedFeePerUnit", "item.feeClampApplied",
        "costEngineVersion", "materialCostBeforeWastage",
    ];
}
