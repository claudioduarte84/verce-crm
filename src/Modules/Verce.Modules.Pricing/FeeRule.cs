using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Pricing;

/// <summary>PER_ORDER has no meaning yet — S5 prices one output unit at a time (there is no
/// Quote line quantity until S6). Modeled now, per CR-07.3, so the fee model does not need a
/// breaking change later; only PER_UNIT is exercised by S5's PricingEngine.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixedFeeApplication>))]
public enum FixedFeeApplication { PerUnit, PerOrder }

/// <summary>
/// S5 FeeRule (DOMAIN-MODEL §7, ADR-0005). Deliberately narrower than the DOMAIN-MODEL sketch:
/// <c>AppliesTo</c>/<c>TargetId</c>/<c>Priority</c> product/category-scoped fee rules and
/// <c>PriceBracket</c> price-tiered fees are NOT implemented — CR-07.5 itself documents that
/// brackets are "not used before S8", and no S5 consumer needs product-scoped fee overrides
/// (mission §21-23 asks only for a per-channel current fee). Exactly one active FeeRule exists
/// per <see cref="SalesChannel"/> (enforced by a unique index), so resolution never needs a
/// priority tie-break. This mirrors CR-07.5's own reasoning for deferring brackets: specify the
/// simple case now, and the richer scoping can be added later without redesigning the fee
/// model's shape (FeeRule -&gt; FeeRuleVersion already matches DOMAIN-MODEL exactly).
/// </summary>
[Auditable]
public sealed class FeeRule : AggregateRoot
{
    private readonly List<FeeRuleVersion> _versions = [];
    private FeeRule() { }

    public FeeRule(Guid salesChannelId, string name)
    {
        if (salesChannelId == Guid.Empty) throw new ArgumentException("FEE_RULE_SALES_CHANNEL_REQUIRED");
        SalesChannelId = salesChannelId;
        Name = Required(name, 2, 200, nameof(name));
        Active = true;
    }

    public Guid SalesChannelId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool Active { get; private set; }
    public IReadOnlyList<FeeRuleVersion> Versions => _versions.AsReadOnly();

    public void Rename(string name) => Name = Required(name, 2, 200, nameof(name));
    public void Activate() => Active = true;
    public void Deactivate() => Active = false;

    /// <summary>Adds a new version effective from <paramref name="validFrom"/>. Historical
    /// versions are never mutated (ADR-0005 §1/mission §85): superseding terms always means a
    /// NEW version, and the database EXCLUDE constraint (see PricingConfiguration) is the actual
    /// enforcement of "versions never overlap" — this only performs the in-process shape checks
    /// a constraint violation cannot phrase as a clean domain error.
    ///
    /// <paramref name="channelKind"/> is the owning <see cref="SalesChannel"/>'s kind, resolved
    /// by the caller (Terra N-03): a <see cref="SalesChannelKind.Direct"/> channel can never
    /// carry non-zero commercial terms — ADR-0005 §2/DOMAIN-MODEL §7 already treat direct sale
    /// as the zero-fee degenerate case of the one marketplace formula, and this is what stops a
    /// configuration user from quietly turning that formula into a fee-bearing one while the
    /// engine still calls it Direct. Defaults to <see cref="SalesChannelKind.Marketplace"/> (no
    /// restriction) so existing marketplace-only call sites/tests are unaffected.</summary>
    public FeeRuleVersion AddVersion(DateOnly validFrom, DateOnly? validUntil, decimal commissionPercent, decimal fixedFee,
        FixedFeeApplication fixedFeeApplication, decimal? minimumFee, decimal? maximumFee, string? notes,
        SalesChannelKind channelKind = SalesChannelKind.Marketplace)
    {
        if (validUntil is { } until && until <= validFrom) throw new ArgumentException("FEE_RULE_VERSION_INVALID_WINDOW");
        if (channelKind == SalesChannelKind.Direct && (commissionPercent != 0m || fixedFee != 0m))
            throw new ArgumentException("DIRECT_CHANNEL_FEES_NOT_ALLOWED");
        var version = new FeeRuleVersion(Id, validFrom, validUntil, commissionPercent, fixedFee, fixedFeeApplication, minimumFee, maximumFee, notes);
        _versions.Add(version);
        return version;
    }

    /// <summary>ADR-0005 §3 resolution step 2: the version valid at <paramref name="instant"/>
    /// (half-open <c>[from, until)</c>). Pure and deterministic — the caller supplies "now" (no
    /// <see cref="DateTime"/> inside domain code, CLAUDE.md). At most one version can ever match
    /// in a correctly-persisted rule (the database EXCLUDE constraint is the actual guarantee;
    /// this is what a unit test exercises without a database).</summary>
    public FeeRuleVersion? ResolveVersionAt(DateOnly instant) =>
        _versions.SingleOrDefault(v => v.ValidFrom <= instant && (v.ValidUntil is null || instant < v.ValidUntil));

    /// <summary>Closes the currently open-ended version (if any) so a new one can begin —
    /// the usual "supersede as of today" operator action. Does not touch any version that is
    /// already closed; those are immutable history.</summary>
    public void CloseOpenVersion(DateOnly asOf)
    {
        var open = _versions.SingleOrDefault(v => v.ValidUntil is null);
        open?.Close(asOf);
    }

    private static string Required(string value, int min, int max, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max) throw new ArgumentException($"{field.ToUpperInvariant()}_INVALID");
        return trimmed;
    }
}

/// <summary>Immutable-once-superseded fee terms (ADR-0005 §1/§3, CR-07.3/CR-07.6). Non-overlap
/// across versions of the same rule is enforced by a PostgreSQL EXCLUDE constraint
/// (<c>daterange</c> + gist) — the true source of "ambiguous current fee" prevention (mission
/// §86), not application sequencing alone.</summary>
public sealed class FeeRuleVersion : Entity, IOwnedBy<FeeRule>
{
    private FeeRuleVersion() { }

    internal FeeRuleVersion(Guid feeRuleId, DateOnly validFrom, DateOnly? validUntil, decimal commissionPercent, decimal fixedFee,
        FixedFeeApplication fixedFeeApplication, decimal? minimumFee, decimal? maximumFee, string? notes)
    {
        FeeRuleId = feeRuleId;
        if (commissionPercent < 0 || commissionPercent >= 1) throw new ArgumentException("PRICING_INVALID_COMMISSION");
        if (fixedFee < 0) throw new ArgumentException("FEE_RULE_VERSION_FIXED_FEE_INVALID");
        // B-03: a fixed fee is a BRL amount, never more precise than whole cents — this rejects
        // a NEW version's fee outright (e.g. 1.005) rather than silently rounding/truncating it,
        // which the PER_ORDER allocator could otherwise never partition exactly. Existing,
        // already-persisted legacy rows are never touched by this — the constructor only runs
        // for a version being created now.
        if (!Verce.SharedKernel.Rounding.HasMoneyPrecision(fixedFee)) throw new ArgumentException("FIXED_FEE_PRECISION_INVALID");
        if (minimumFee is < 0) throw new ArgumentException("FEE_RULE_VERSION_MINIMUM_FEE_INVALID");
        if (minimumFee is { } minFeeValue && !Verce.SharedKernel.Rounding.HasMoneyPrecision(minFeeValue)) throw new ArgumentException("FIXED_FEE_PRECISION_INVALID");
        if (maximumFee is < 0) throw new ArgumentException("FEE_RULE_VERSION_MAXIMUM_FEE_INVALID");
        if (maximumFee is { } maxFeeValue && !Verce.SharedKernel.Rounding.HasMoneyPrecision(maxFeeValue)) throw new ArgumentException("FIXED_FEE_PRECISION_INVALID");
        if (minimumFee is { } min && maximumFee is { } max && min > max) throw new ArgumentException("FEE_RULE_VERSION_FEE_RANGE_INVALID");
        if (!Enum.IsDefined(fixedFeeApplication)) throw new ArgumentException("FEE_RULE_VERSION_FIXED_FEE_APPLICATION_INVALID");
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        CommissionPercent = commissionPercent;
        FixedFee = fixedFee;
        FixedFeeApplication = fixedFeeApplication;
        MinimumFee = minimumFee;
        MaximumFee = maximumFee;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : (notes.Trim().Length <= 2000 ? notes.Trim() : throw new ArgumentException("FIELD_TOO_LONG"));
    }

    public Guid FeeRuleId { get; private set; }
    public Guid ParentId => FeeRuleId;
    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidUntil { get; private set; }
    public decimal CommissionPercent { get; private set; }
    public decimal FixedFee { get; private set; }
    public FixedFeeApplication FixedFeeApplication { get; private set; }
    public decimal? MinimumFee { get; private set; }
    public decimal? MaximumFee { get; private set; }
    public string? Notes { get; private set; }

    internal void Close(DateOnly asOf)
    {
        if (asOf <= ValidFrom) throw new ArgumentException("FEE_RULE_VERSION_INVALID_WINDOW");
        ValidUntil = asOf;
    }
}
