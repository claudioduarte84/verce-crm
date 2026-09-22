using FluentAssertions;
using Verce.Modules.Quoting;

namespace Verce.Quoting.Tests;

public class QuoteRevisionSuffixTests
{
    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "B")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(28, "AB")]
    [InlineData(53, "BA")]
    public void ToSuffix_matches_ADR_0004_boundary_table(int revisionIndex, string expected) =>
        QuoteRevision.ToSuffix(revisionIndex).Should().Be(expected);
}

public class QuoteNumberFormatTests
{
    [Fact]
    public void FormatNumber_is_YYMMDD_dash_sequence()
    {
        Quote.FormatNumber(new DateOnly(2026, 9, 6), 4).Should().Be("260906-4");
    }
}

public class QuoteTests
{
    private static readonly Guid ChannelId = Guid.NewGuid();
    private static readonly Guid CorrelationId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 20);
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static QuoteItemCostSnapshotInput CostSnapshot(decimal unitCost) =>
        new("1.0.0", 0m, 0m, 0m, null, null, null, 0m, null, null, 0m, 0m, unitCost, 1, unitCost, [], []);

    private static QuoteItemSnapshot Item(decimal quantity = 1m, decimal unitCost = 10m, decimal margin = 0.35m,
        decimal? manualOverride = null, QuoteFixedFeeApplication feeApplication = QuoteFixedFeeApplication.PerUnit,
        decimal allocatedOrderFee = 0m, decimal rawFixedFee = 0m) =>
        new(null, null, null, "Item de teste", null, quantity, CostSnapshot(unitCost), margin, ChannelId, null, 0.10m,
            feeApplication, rawFixedFee, allocatedOrderFee, "CENT", 20.00m, 2.00m, null, manualOverride,
            QuoteDiscountKind.None, 0m);

    private static Quote NewQuote(IReadOnlyList<QuoteItemSnapshot>? items = null) =>
        new(1, Today, new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            items ?? [Item()], 15, ProposalContentInput.Empty, null, CorrelationId, Now);

    [Fact]
    public void Construction_creates_revision_1_as_GENERATED_with_no_suffix()
    {
        var quote = NewQuote();
        quote.CurrentRevision.RevisionIndex.Should().Be(1);
        quote.CurrentRevision.RevisionSuffix.Should().BeEmpty();
        quote.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.GENERATED);
        quote.DisplayNumberFor(quote.CurrentRevision).Should().Be(quote.Number);
        quote.Number.Should().Be("260920-1");
        quote.PendingEvents.OfType<Verce.Modules.Quoting.Contracts.QuoteCreatedEvent>().Should().ContainSingle();
    }

    [Fact]
    public void ConstructNextRevision_never_mutates_the_previous_revision_fields()
    {
        var quote = NewQuote();
        var original = quote.CurrentRevision;
        var originalTotal = original.TotalAmount;

        quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item(unitCost: 999m)], 15, ProposalContentInput.Empty, Today, null, CorrelationId, Now);

        original.TotalAmount.Should().Be(originalTotal); // field-for-field historical
        original.Items.Single().UnitTotalCost.Should().Be(10m);
    }

    [Fact]
    public void ConstructNextRevision_sets_supersession_pointer_and_status_on_the_previous_revision()
    {
        var quote = NewQuote();
        var r1 = quote.CurrentRevision;
        var r2 = quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], 15, ProposalContentInput.Empty, Today, null, CorrelationId, Now);

        r1.SupersededByRevisionId.Should().Be(r2.Id);
        r1.Status.Should().Be(QuoteRevisionStatus.SUPERSEDED); // was GENERATED (non-terminal)
        r2.RevisionIndex.Should().Be(2);
        r2.RevisionSuffix.Should().Be("B");
        quote.CurrentRevisionId.Should().Be(r2.Id);
    }

    [Fact]
    public void ConstructNextRevision_is_never_blocked_by_anything_this_aggregate_knows_about()
    {
        // BLOCKING-01: there is no production-order parameter on this method at all — creation
        // cannot be gated on shop-floor state because the aggregate has no way to even ask.
        var quote = NewQuote();
        quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true);
        var act = () => quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], 15, ProposalContentInput.Empty, Today, null, CorrelationId, Now);
        act.Should().NotThrow();
    }

    [Fact]
    public void An_APPROVED_revision_keeps_its_status_forever_even_after_being_superseded()
    {
        var quote = NewQuote();
        quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true);
        var approved = quote.CurrentRevision;

        quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], 15, ProposalContentInput.Empty, Today, null, CorrelationId, Now);

        approved.Status.Should().Be(QuoteRevisionStatus.APPROVED); // never SUPERSEDED
        approved.SupersededByRevisionId.Should().NotBeNull(); // but the pointer IS set
    }

    [Fact]
    public void Approve_requires_direct_approval_setting_when_still_GENERATED()
    {
        var quote = NewQuote();
        var act = () => quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: false);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_DIRECT_APPROVAL_NOT_ALLOWED");
    }

    [Fact]
    public void Approve_from_SENT_never_needs_the_direct_approval_setting()
    {
        var quote = NewQuote();
        quote.Send(null, CorrelationId, Now);
        var act = () => quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void Approve_rejects_an_empty_quote()
    {
        var quote = NewQuote(items: []);
        var act = () => quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_HAS_NO_ITEMS");
    }

    [Fact]
    public void Approve_rejects_an_expired_revision()
    {
        var quote = NewQuote();
        var act = () => quote.Approve(null, CorrelationId, Now, Today.AddDays(20), allowDirectApproval: true);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_REVISION_EXPIRED");
    }

    [Fact]
    public void Approve_twice_is_rejected_as_already_decided()
    {
        var quote = NewQuote();
        quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true);
        var act = () => quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_REVISION_ALREADY_DECIDED");
    }

    [Fact]
    public void Approve_raises_QuoteApproved_with_the_current_revision_id()
    {
        var quote = NewQuote();
        quote.Approve(Guid.NewGuid(), CorrelationId, Now, Today, allowDirectApproval: true);
        var evt = quote.PendingEvents.OfType<Verce.Modules.Quoting.Contracts.QuoteApprovedEvent>().Single();
        evt.QuoteRevisionId.Should().Be(quote.CurrentRevisionId);
        evt.QuoteId.Should().Be(quote.Id);
    }

    [Fact]
    public void Cancel_requires_a_reason()
    {
        var quote = NewQuote();
        var act = () => quote.Cancel("", null, CorrelationId, Now);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_CANCEL_REASON_REQUIRED");
    }

    [Fact]
    public void ExpireCurrentRevision_is_idempotent_and_a_no_op_when_not_eligible()
    {
        var quote = NewQuote();
        quote.Approve(null, CorrelationId, Now, Today, allowDirectApproval: true); // now terminal
        quote.ExpireCurrentRevision(CorrelationId, Now, Today.AddDays(100)); // way past ValidUntil
        quote.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.APPROVED); // untouched — re-running changes nothing
    }

    [Fact]
    public void ExpireCurrentRevision_expires_an_eligible_revision_and_raises_QuoteExpired()
    {
        var quote = NewQuote();
        quote.ExpireCurrentRevision(CorrelationId, Now, Today.AddDays(20)); // 15-day validity elapsed
        quote.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.EXPIRED);
        quote.PendingEvents.OfType<Verce.Modules.Quoting.Contracts.QuoteExpiredEvent>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Construction_rejects_a_non_positive_validity_days_value(int validityDays)
    {
        var act = () => new Quote(1, Today, new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], validityDays, ProposalContentInput.Empty, null, CorrelationId, Now);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_VALIDITY_DAYS_INVALID");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(3650)]
    [InlineData(3651)] // F-05: no arbitrary business-length maximum — only representability matters.
    public void Construction_accepts_any_positive_validity_days_value_that_DateOnly_can_represent(int validityDays)
    {
        var quote = new Quote(1, Today, new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], validityDays, ProposalContentInput.Empty, null, CorrelationId, Now);
        quote.CurrentRevision.ValidUntil.Should().Be(Today.AddDays(validityDays));
    }

    [Fact]
    public void Construction_accepts_the_maximum_representable_validity_days_value_for_the_given_organization_date()
    {
        var maxRepresentableDays = DateOnly.MaxValue.DayNumber - Today.DayNumber;
        var quote = new Quote(1, Today, new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], maxRepresentableDays, ProposalContentInput.Empty, null, CorrelationId, Now);
        quote.CurrentRevision.ValidUntil.Should().Be(DateOnly.MaxValue);
    }

    [Theory]
    [InlineData(1)] // one past the maximum representable value for `Today`
    [InlineData(int.MaxValue)] // wildly unrepresentable regardless of organization date
    public void Construction_rejects_a_validity_days_value_DateOnly_cannot_represent(int daysPastMaxRepresentable)
    {
        var maxRepresentableDays = DateOnly.MaxValue.DayNumber - Today.DayNumber;
        var unrepresentableValidityDays = daysPastMaxRepresentable == int.MaxValue ? int.MaxValue : maxRepresentableDays + daysPastMaxRepresentable;
        var act = () => new Quote(1, Today, new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], unrepresentableValidityDays, ProposalContentInput.Empty, null, CorrelationId, Now);
        act.Should().Throw<ArgumentException>().WithMessage("QUOTE_VALIDITY_DAYS_INVALID");
    }

    [Fact]
    public void Revived_quote_after_expiration_starts_a_fresh_GENERATED_revision()
    {
        var quote = NewQuote();
        quote.ExpireCurrentRevision(CorrelationId, Now, Today.AddDays(20));
        var revived = quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), ChannelId,
            [Item()], 15, ProposalContentInput.Empty, Today.AddDays(20), null, CorrelationId, Now);
        revived.Status.Should().Be(QuoteRevisionStatus.GENERATED);
        quote.CurrentRevision.Should().BeSameAs(revived);
    }
}
