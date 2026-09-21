using Verce.SharedKernel.Time;

namespace Verce.Modules.Quoting;

/// <summary>One append-only <c>quote_status_history</c> row, projected down to the two fields
/// the outcome calculation needs. <see cref="ToStatus"/> uses the same string values as
/// <see cref="QuoteRevisionStatus"/> (kept as <c>string</c> here so this calculator has zero
/// dependency on the persisted enum shape).</summary>
public sealed record QuoteHistoryEvent(string ToStatus, DateTimeOffset ChangedAt);

/// <summary>The Option B (frozen 2026-09-20) per-period classification for one quote. At most one
/// per quote per period — never a count of transitions (ADR-0020 §B.2, DATA-DICTIONARY §4.1).</summary>
public enum QuotePeriodOutcome { None, Won, Lost }

/// <summary>
/// Pure derivation of H-009 A's commercial outcome (ADR-0020 Part B) from append-only history.
/// No DbContext, no clock — every timestamp is supplied by the caller, exactly like
/// <see cref="PerOrderFeeAllocator"/>. Two distinct things are computed, deliberately kept
/// separate (MEDIUM-01 correction): <see cref="HasEverWon"/> (absorbing/monotonic — the ONLY
/// part of this model that is) and the current, non-monotonic <see cref="CurrentCommercialOutcome"/>
/// classification, plus the deduplicated, precedence-ordered <see cref="PeriodOutcome"/> a
/// reporting period actually counts (Option B).
/// </summary>
public static class QuoteOutcomeCalculator
{
    public const string Approved = "APPROVED";
    public const string Canceled = "CANCELED";
    public const string Expired = "EXPIRED";

    /// <summary><c>firstApprovalAt(quote) = MIN(changed_at) WHERE to_status = 'APPROVED'</c> —
    /// null if the quote (across every one of its revisions) was never approved.</summary>
    public static DateTimeOffset? FirstApprovalAt(IEnumerable<QuoteHistoryEvent> history)
    {
        var approvals = history.Where(h => h.ToStatus == Approved).Select(h => h.ChangedAt).ToArray();
        return approvals.Length == 0 ? null : approvals.Min();
    }

    /// <summary>Absorbing: once true from a given <paramref name="firstApprovalAt"/>, it is true
    /// forever — no later revision, cancellation or expiration can make this false again.</summary>
    public static bool HasEverWon(DateTimeOffset? firstApprovalAt) => firstApprovalAt is not null;

    /// <summary>A terminal CANCELED/EXPIRED transition counts as a loss only while the quote had
    /// never yet been approved — a post-win cancellation of a later revision is a scope change,
    /// never a loss (ADR-0020 §B.2). Returns every such eligible transition's timestamp; the
    /// caller (or <see cref="PeriodOutcome"/>) buckets them into reporting periods.</summary>
    public static IReadOnlyList<DateTimeOffset> EligiblePreWinLossTransitions(
        IEnumerable<QuoteHistoryEvent> history, DateTimeOffset? firstApprovalAt) =>
        history
            .Where(h => (h.ToStatus == Canceled || h.ToStatus == Expired) && (firstApprovalAt is null || h.ChangedAt < firstApprovalAt))
            .Select(h => h.ChangedAt)
            .ToArray();

    /// <summary>The CURRENT, real-time classification (STATE-MACHINES-adjacent reporting field) —
    /// NOT monotonic for a never-won quote: it can legitimately cycle OPEN -> LOST -> OPEN as the
    /// current revision expires/cancels and is later revived by a new one. Only the <c>WON</c>
    /// branch is absorbing, because it is gated on the absorbing <see cref="HasEverWon"/>.</summary>
    public static string CurrentCommercialOutcome(DateTimeOffset? firstApprovalAt, bool currentRevisionIsCanceledOrExpired) =>
        HasEverWon(firstApprovalAt) ? "WON" : currentRevisionIsCanceledOrExpired ? "LOST" : "OPEN";

    /// <summary>Converts a UTC instant to the organization's local business date — the same
    /// conversion <see cref="Verce.SharedKernel.Time.SystemClock.OrganizationToday"/> performs,
    /// duplicated here (rather than depending on <c>IClock</c>) so this calculator stays a pure
    /// function of its inputs, with no service dependency. Period bucketing MUST use this, never
    /// a raw UTC date (ADR-0004's own organization-date rule, restated for reporting).</summary>
    public static DateOnly OrganizationDate(DateTimeOffset instant, string organizationTimeZoneId) =>
        OrganizationTimeZone.ToOrganizationDate(instant, organizationTimeZoneId);

    /// <summary>
    /// Option B (frozen 2026-09-20, product decision): for one quote and one reporting period
    /// <c>[periodStart, periodEndExclusive)</c>, returns AT MOST ONE outcome, with precedence
    /// <c>WON &gt; LOST &gt; None</c>. A quote that both won and lost inside the same period (win
    /// after an earlier revival) counts as WON only, with zero losses for that period — the
    /// `LOST` branch is never even reached once `WON` matches (ADR-0020 §B.2, DATA-DICTIONARY
    /// §4.1, CR-12.1).
    /// </summary>
    public static QuotePeriodOutcome PeriodOutcome(
        DateOnly periodStart,
        DateOnly periodEndExclusive,
        string organizationTimeZoneId,
        DateTimeOffset? firstApprovalAt,
        IReadOnlyList<DateTimeOffset> eligiblePreWinLossTransitions)
    {
        if (firstApprovalAt is { } wonAt)
        {
            var wonDate = OrganizationDate(wonAt, organizationTimeZoneId);
            if (wonDate >= periodStart && wonDate < periodEndExclusive) return QuotePeriodOutcome.Won;
        }

        foreach (var lossAt in eligiblePreWinLossTransitions)
        {
            var lossDate = OrganizationDate(lossAt, organizationTimeZoneId);
            if (lossDate >= periodStart && lossDate < periodEndExclusive) return QuotePeriodOutcome.Lost;
        }

        return QuotePeriodOutcome.None;
    }

    /// <summary><c>won(period)</c>/<c>lost(period)</c>/<c>decided(period)</c>/
    /// <c>conversionRate(period)</c> (CR-12.1) — one <see cref="QuotePeriodOutcome"/> per quote
    /// already deduplicated by <see cref="PeriodOutcome"/>, never a raw transition count.</summary>
    public static ConversionRatePeriodResult ConversionRate(IEnumerable<QuotePeriodOutcome> outcomesForEveryQuoteInPeriod)
    {
        var won = 0;
        var lost = 0;
        foreach (var outcome in outcomesForEveryQuoteInPeriod)
        {
            if (outcome == QuotePeriodOutcome.Won) won++;
            else if (outcome == QuotePeriodOutcome.Lost) lost++;
        }
        var decided = won + lost;
        decimal? rate = decided > 0 ? Math.Round((decimal)won / decided, 6, MidpointRounding.AwayFromZero) : null;
        return new ConversionRatePeriodResult(won, lost, decided, rate);
    }
}

public sealed record ConversionRatePeriodResult(int Won, int Lost, int Decided, decimal? ConversionRate);
