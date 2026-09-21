using FluentAssertions;
using Verce.Modules.Quoting;

namespace Verce.Quoting.Tests;

/// <summary>ADR-0020 §B.2, DATA-DICTIONARY §4.1 — hasEverWon (monotonic), the current
/// (non-monotonic-for-never-won) classification, and Option B's same-period/cross-period
/// cardinality.</summary>
public class QuoteOutcomeCalculatorTests
{
    private static readonly DateTimeOffset Jan03 = new(2026, 1, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan10 = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan20 = new(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb10 = new(2026, 2, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb20 = new(2026, 2, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar10 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar20 = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Apr10 = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private const string Tz = "America/Sao_Paulo";
    private static readonly DateOnly January = new(2026, 1, 1);
    private static readonly DateOnly February = new(2026, 2, 1);
    private static readonly DateOnly March = new(2026, 3, 1);
    private static readonly DateOnly April = new(2026, 4, 1);
    private static readonly DateOnly May = new(2026, 5, 1);

    [Fact]
    public void HasEverWon_is_false_when_never_approved()
    {
        QuoteOutcomeCalculator.HasEverWon(null).Should().BeFalse();
    }

    [Fact]
    public void HasEverWon_is_absorbing_once_approved()
    {
        QuoteOutcomeCalculator.HasEverWon(Jan20).Should().BeTrue();
    }

    [Fact]
    public void FirstApprovalAt_is_the_earliest_APPROVED_transition()
    {
        var history = new[]
        {
            new QuoteHistoryEvent("CANCELED", Jan03),
            new QuoteHistoryEvent("APPROVED", Mar20),
            new QuoteHistoryEvent("APPROVED", Apr10), // a later revision's approval never wins over the first
        };
        QuoteOutcomeCalculator.FirstApprovalAt(history).Should().Be(Mar20);
    }

    [Fact]
    public void Current_outcome_cycles_OPEN_LOST_OPEN_for_a_never_won_quote()
    {
        // Expire then revive (STATE-MACHINES §1.6) — current outcome is NOT monotonic pre-win.
        QuoteOutcomeCalculator.CurrentCommercialOutcome(null, currentRevisionIsCanceledOrExpired: false).Should().Be("OPEN");
        QuoteOutcomeCalculator.CurrentCommercialOutcome(null, currentRevisionIsCanceledOrExpired: true).Should().Be("LOST");
        QuoteOutcomeCalculator.CurrentCommercialOutcome(null, currentRevisionIsCanceledOrExpired: false).Should().Be("OPEN"); // revived
    }

    [Fact]
    public void Current_outcome_is_WON_forever_once_hasEverWon_regardless_of_current_revision_state()
    {
        QuoteOutcomeCalculator.CurrentCommercialOutcome(Jan20, currentRevisionIsCanceledOrExpired: true).Should().Be("WON");
        QuoteOutcomeCalculator.CurrentCommercialOutcome(Jan20, currentRevisionIsCanceledOrExpired: false).Should().Be("WON");
    }

    [Fact]
    public void EligiblePreWinLossTransitions_excludes_post_win_cancellations()
    {
        var history = new[]
        {
            new QuoteHistoryEvent("EXPIRED", Jan03),   // pre-win: eligible
            new QuoteHistoryEvent("APPROVED", Mar20),  // the win
            new QuoteHistoryEvent("CANCELED", Apr10),  // post-win: NOT a loss (e.g. a later revision's own scope change)
        };
        var firstApprovalAt = QuoteOutcomeCalculator.FirstApprovalAt(history);
        var eligible = QuoteOutcomeCalculator.EligiblePreWinLossTransitions(history, firstApprovalAt);
        eligible.Should().ContainSingle().Which.Should().Be(Jan03);
    }

    // ---- Option B: same-period cardinality (frozen 2026-09-20) ----

    [Fact]
    public void A_expired_only_counts_as_one_loss_in_its_period()
    {
        var outcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, null, [Jan03]);
        outcome.Should().Be(QuotePeriodOutcome.Lost);
    }

    [Fact]
    public void B_expired_then_canceled_in_the_same_period_is_one_loss_not_two()
    {
        // Expired Jan 03, revived, canceled again Jan 20 — never approved.
        var outcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, null, [Jan03, Jan20]);
        outcome.Should().Be(QuotePeriodOutcome.Lost);

        var aggregate = QuoteOutcomeCalculator.ConversionRate([outcome]);
        aggregate.Lost.Should().Be(1); // NOT 2 — the whole point of Option B
        aggregate.Won.Should().Be(0);
    }

    [Fact]
    public void C_expired_then_approved_in_the_same_period_is_won_with_zero_losses()
    {
        // Expired Jan 03, revived, approved Jan 20 in the SAME period — WON wins, LOST branch unreachable.
        var outcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, Jan20, [Jan03]);
        outcome.Should().Be(QuotePeriodOutcome.Won);

        var aggregate = QuoteOutcomeCalculator.ConversionRate([outcome]);
        aggregate.Won.Should().Be(1);
        aggregate.Lost.Should().Be(0);
    }

    [Fact]
    public void D_cross_period_losses_count_once_per_period()
    {
        // Expired Jan 03, revived Feb 10, canceled Feb 20 — never approved.
        var januaryOutcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, null, [Jan03, Feb20]);
        var februaryOutcome = QuoteOutcomeCalculator.PeriodOutcome(February, March, Tz, null, [Jan03, Feb20]);
        januaryOutcome.Should().Be(QuotePeriodOutcome.Lost);
        februaryOutcome.Should().Be(QuotePeriodOutcome.Lost);
    }

    [Fact]
    public void E_lost_then_won_across_periods_never_rewrites_the_earlier_period()
    {
        // Lost in January, first approved in March.
        var januaryOutcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, Mar20, [Jan03]);
        var marchOutcome = QuoteOutcomeCalculator.PeriodOutcome(March, April, Tz, Mar20, [Jan03]);
        januaryOutcome.Should().Be(QuotePeriodOutcome.Lost); // WON's date (March) is outside January, so LOST is reached
        marchOutcome.Should().Be(QuotePeriodOutcome.Won);
    }

    [Fact]
    public void F_a_later_approval_of_a_superseding_revision_adds_no_additional_win()
    {
        // First approval March; a later revision is ALSO approved in April — firstApprovalAt
        // never moves, so April gets nothing.
        var firstApprovalAt = Mar10; // frozen at the FIRST approval, per HasEverWon's contract
        var marchOutcome = QuoteOutcomeCalculator.PeriodOutcome(March, April, Tz, firstApprovalAt, []);
        var aprilOutcome = QuoteOutcomeCalculator.PeriodOutcome(April, May, Tz, firstApprovalAt, []);
        marchOutcome.Should().Be(QuotePeriodOutcome.Won);
        aprilOutcome.Should().Be(QuotePeriodOutcome.None);
    }

    // ---- M-02: additional Option B coverage — 3+ same-period losses, multiple losses plus a
    // same-period win, and the organization-timezone boundary. ----

    [Fact]
    public void G_three_or_more_same_period_losses_still_count_as_exactly_one_loss()
    {
        var outcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, null, [Jan03, Jan10, Jan20]);
        outcome.Should().Be(QuotePeriodOutcome.Lost);

        var aggregate = QuoteOutcomeCalculator.ConversionRate([outcome]);
        aggregate.Lost.Should().Be(1);
        aggregate.Won.Should().Be(0);
        aggregate.Decided.Should().Be(1);
    }

    [Fact]
    public void H_multiple_same_period_losses_followed_by_a_same_period_approval_is_won_with_zero_losses()
    {
        // Three losses in January, then finally approved — also in January. WON precedence means
        // none of the three losses are ever counted, no matter how many there were.
        var outcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, Jan20, [Jan03, Jan10]);
        outcome.Should().Be(QuotePeriodOutcome.Won);

        var aggregate = QuoteOutcomeCalculator.ConversionRate([outcome]);
        aggregate.Won.Should().Be(1);
        aggregate.Lost.Should().Be(0);
        aggregate.Decided.Should().Be(1);
    }

    [Fact]
    public void I_period_bucketing_uses_the_organization_timezone_never_the_raw_UTC_date()
    {
        // America/Sao_Paulo is a fixed UTC-3 offset (no DST since 2019). 2026-01-01T01:00:00Z has
        // a UTC date of Jan 1, but its organization date is still Dec 31 of the PREVIOUS year —
        // proving PeriodOutcome buckets by organization date, never the raw UTC instant's date.
        var utcJan1EarlyMorning = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        QuoteOutcomeCalculator.OrganizationDate(utcJan1EarlyMorning, Tz).Should().Be(new DateOnly(2025, 12, 31));

        var december2025 = new DateOnly(2025, 12, 1);
        var januaryOutcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, null, [utcJan1EarlyMorning]);
        var decemberOutcome = QuoteOutcomeCalculator.PeriodOutcome(december2025, January, Tz, null, [utcJan1EarlyMorning]);

        januaryOutcome.Should().Be(QuotePeriodOutcome.None, "the UTC date (Jan 1) falls in January, but the organization date does not");
        decemberOutcome.Should().Be(QuotePeriodOutcome.Lost, "the organization date (Dec 31) is the period that actually counts this loss");
    }

    [Fact]
    public void J_a_win_right_at_the_organization_timezone_boundary_is_attributed_to_the_correct_period()
    {
        // Same boundary instant, but as the WINNING transition instead of a loss.
        var utcJan1EarlyMorning = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var december2025 = new DateOnly(2025, 12, 1);

        var januaryOutcome = QuoteOutcomeCalculator.PeriodOutcome(January, February, Tz, utcJan1EarlyMorning, []);
        var decemberOutcome = QuoteOutcomeCalculator.PeriodOutcome(december2025, January, Tz, utcJan1EarlyMorning, []);

        januaryOutcome.Should().Be(QuotePeriodOutcome.None);
        decemberOutcome.Should().Be(QuotePeriodOutcome.Won);
    }

    [Fact]
    public void ConversionRate_is_null_when_nothing_decided()
    {
        var result = QuoteOutcomeCalculator.ConversionRate([QuotePeriodOutcome.None, QuotePeriodOutcome.None]);
        result.Decided.Should().Be(0);
        result.ConversionRate.Should().BeNull();
    }

    [Fact]
    public void ConversionRate_rounds_to_six_decimals_per_CR_12_1()
    {
        // 1 won out of 3 decided = 0.333333...
        var result = QuoteOutcomeCalculator.ConversionRate([QuotePeriodOutcome.Won, QuotePeriodOutcome.Lost, QuotePeriodOutcome.Lost]);
        result.ConversionRate.Should().Be(0.333333m);
    }
}
