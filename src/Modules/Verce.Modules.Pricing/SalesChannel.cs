using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Pricing;

/// <summary>Direct sale is a normal channel whose fee rule is 0%/R$0, never a special case in
/// the engine (ADR-0005 §2, DOMAIN-MODEL §7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SalesChannelKind>))]
public enum SalesChannelKind { Direct, Marketplace, Other }

[Auditable]
public sealed class SalesChannel : AggregateRoot
{
    /// <summary>The well-known code of the mandatory seeded "Venda Direta" channel (Terra N-03/
    /// N-04). DIRECT identity is a reserved, bidirectional one-to-one Code/Kind pair: this row's
    /// <see cref="Kind"/> is frozen at <see cref="SalesChannelKind.Direct"/> forever, AND no other
    /// channel may ever be given <see cref="SalesChannelKind.Direct"/> — a configuration user must
    /// never be able to repurpose this specific row away from Direct, nor create/convert a
    /// semantic alias of it under a different code.</summary>
    public const string DirectChannelCode = "DIRECT";

    private SalesChannel() { }

    public SalesChannel(string code, string name, SalesChannelKind kind, decimal? defaultMarginPercent, string? notes)
    {
        Code = NormalizeCode(code);
        UpdateDetails(name, kind, defaultMarginPercent, notes);
        Active = true;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public SalesChannelKind Kind { get; private set; }
    /// <summary>ADR-0002 fraction convention (0.35 = 35%) — Pricing's percentages are NOT the
    /// Costing-only percentage-point exception ADR-0018 carved out. Null means "use
    /// <c>pricing.default_margin_percent</c>".</summary>
    public decimal? DefaultMarginPercent { get; private set; }
    public string? Notes { get; private set; }
    public bool Active { get; private set; }

    public void UpdateDetails(string name, SalesChannelKind kind, decimal? defaultMarginPercent, string? notes)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("SALES_CHANNEL_KIND_INVALID");
        // Terra N-03: the seeded system channel's Kind is immutable — converting it away from
        // Direct would let Direct pricing silently start resolving marketplace-like fee terms
        // while every caller still addresses it by the well-known "DIRECT" code.
        if (Code == DirectChannelCode && kind != SalesChannelKind.Direct) throw new ArgumentException("DIRECT_CHANNEL_KIND_IMMUTABLE");
        // Terra N-04: the reverse direction — no channel other than the canonical "DIRECT" row
        // may ever be Kind=Direct. Without this, an Owner could create or convert e.g. "SHOP" to
        // Kind=Direct, producing a second semantic alias of Direct sale that ADR-0005 §2/ADR-0019
        // never anticipated (which one is "the" degenerate zero-fee case?). This check runs at
        // BOTH construction and update, since the constructor itself calls this method after
        // assigning Code — there is no separate code path that could bypass it.
        if (Code != DirectChannelCode && kind == SalesChannelKind.Direct) throw new ArgumentException("DIRECT_CHANNEL_IDENTITY_RESERVED");
        if (defaultMarginPercent is < 0 or >= 1) throw new ArgumentException("PRICING_INVALID_MARGIN");
        Name = Required(name, 2, 200, nameof(name));
        Kind = kind;
        DefaultMarginPercent = defaultMarginPercent;
        Notes = Optional(notes, 2000);
    }

    public void Activate() => Active = true;
    public void Deactivate() => Active = false;

    internal static string NormalizeCode(string code)
    {
        var trimmed = Required(code, 2, 40, nameof(code)).ToUpperInvariant();
        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new ArgumentException("SALES_CHANNEL_CODE_INVALID_CHARACTERS");
        return trimmed;
    }

    private static string Required(string value, int min, int max, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max) throw new ArgumentException($"{field.ToUpperInvariant()}_INVALID");
        return trimmed;
    }

    private static string? Optional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("FIELD_TOO_LONG");
}
