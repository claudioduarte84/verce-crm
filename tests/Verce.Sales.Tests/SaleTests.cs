using FluentAssertions;
using Verce.Modules.Sales;

namespace Verce.Sales.Tests;

public class SaleTests
{
    private static readonly SaleItemSnapshot[] Items =
    [new(Guid.NewGuid(), null, "Produto", 2m, 50m, 5m, 90m, 20m, 40m, 9m, 41m, 0.4556m)];

    [Fact]
    public void Manual_direct_sale_uses_sale_series_and_records_estimated_snapshot()
    {
        var sale = new Sale(7, new DateOnly(2026, 9, 22), SaleSource.MANUAL_ENTRY, Guid.NewGuid(), null, null, null, null,
            SaleFeeSource.LOCAL_RULE, Guid.NewGuid(), "Cliente", new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero), 3m, null, null, Items, Guid.NewGuid(), DateTimeOffset.UtcNow);

        sale.SaleNumber.Should().Be("260922-7");
        sale.CostBasis.Should().Be(SaleCostBasis.ESTIMATED);
        sale.NetAmount.Should().Be(90m);
        sale.GrossProfitAmount.Should().Be(38m);
        sale.History.Should().ContainSingle(x => x.ToStatus == SaleStatus.CONFIRMED);
    }

    [Theory]
    [InlineData(SaleSource.QUOTE_CONVERSION)]
    [InlineData(SaleSource.MANUAL_ENTRY)]
    public void Non_marketplace_sources_reject_marketplace_identity(SaleSource source)
    {
        Action construct = () => new Sale(1, new DateOnly(2026, 1, 1), source, Guid.NewGuid(), source == SaleSource.QUOTE_CONVERSION ? Guid.NewGuid() : null,
            null, Guid.NewGuid(), "external", SaleFeeSource.LOCAL_RULE, null, null, DateTimeOffset.UtcNow, 0m, null, null, Items, null, DateTimeOffset.UtcNow);

        construct.Should().Throw<ArgumentException>().WithMessage("SALE_SOURCE_INVALID");
    }

    [Fact]
    public void Marketplace_source_requires_complete_external_identity()
    {
        Action construct = () => new Sale(1, new DateOnly(2026, 1, 1), SaleSource.MARKETPLACE_ORDER, Guid.NewGuid(), null, null,
            Guid.NewGuid(), null, SaleFeeSource.PROVIDER_REPORTED, null, null, DateTimeOffset.UtcNow, 0m, null, null, Items, null, DateTimeOffset.UtcNow);

        construct.Should().Throw<ArgumentException>().WithMessage("SALE_SOURCE_INVALID");
    }

    [Fact]
    public void Cancellation_requires_reason_and_is_one_way()
    {
        var sale = new Sale(1, new DateOnly(2026, 1, 1), SaleSource.MANUAL_ENTRY, Guid.NewGuid(), null, null, null, null,
            SaleFeeSource.LOCAL_RULE, null, null, DateTimeOffset.UtcNow, 0m, null, null, Items, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Action blank = () => sale.Cancel(" ", Guid.NewGuid(), DateTimeOffset.UtcNow);
        blank.Should().Throw<ArgumentException>().WithMessage("SALE_CANCEL_REASON_REQUIRED");
        sale.Cancel("Cliente desistiu", Guid.NewGuid(), DateTimeOffset.UtcNow);
        sale.Status.Should().Be(SaleStatus.CANCELED);
        sale.History.Should().ContainSingle(x => x.ToStatus == SaleStatus.CANCELED && x.Reason == "Cliente desistiu");
        Action repeat = () => sale.Cancel("novamente", Guid.NewGuid(), DateTimeOffset.UtcNow);
        repeat.Should().Throw<ArgumentException>().WithMessage("SALE_INVALID_TRANSITION");
    }
}
