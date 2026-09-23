using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Sales;

[JsonConverter(typeof(JsonStringEnumConverter<SaleStatus>))]
public enum SaleStatus { CONFIRMED, CANCELED }
[JsonConverter(typeof(JsonStringEnumConverter<SaleSource>))]
public enum SaleSource { QUOTE_CONVERSION, MANUAL_ENTRY, MARKETPLACE_ORDER }
[JsonConverter(typeof(JsonStringEnumConverter<SaleFeeSource>))]
public enum SaleFeeSource { LOCAL_RULE, PROVIDER_REPORTED }
[JsonConverter(typeof(JsonStringEnumConverter<SaleCostBasis>))]
public enum SaleCostBasis { ESTIMATED, MIXED, ACTUAL }

public sealed record SaleItemSnapshot(Guid? ProductId, Guid? QuoteItemId, string ProductName, decimal Quantity,
    decimal UnitPrice, decimal DiscountAmount, decimal LineTotalAmount, decimal UnitCostAmount,
    decimal LineCostAmount, decimal ChannelFeeAmount, decimal GrossProfitAmount, decimal EffectiveMarginPercent);

[Auditable]
public sealed class Sale : AggregateRoot
{
    private readonly List<SaleItem> _items = [];
    private readonly List<SaleStatusHistory> _history = [];
    private Sale() { }

    public Sale(int sequence, DateOnly numberDate, SaleSource source, Guid salesChannelId, Guid? quoteRevisionId,
        Guid? conversionRequestId, Guid? marketplaceAccountId, string? externalOrderId, SaleFeeSource feeSource,
        Guid? customerId, string? customerNameSnapshot, DateTimeOffset soldAt, decimal shippingAmount,
        string? externalOrderCode, string? notes, IReadOnlyList<SaleItemSnapshot> items, Guid? actorId, DateTimeOffset now)
    {
        if (sequence <= 0) throw new ArgumentException("SALE_NUMBER_SEQUENCE_INVALID");
        if (salesChannelId == Guid.Empty) throw new ArgumentException("SALE_SALES_CHANNEL_REQUIRED");
        if (items.Count == 0) throw new ArgumentException("SALE_ITEMS_REQUIRED");
        ValidateSource(source, quoteRevisionId, marketplaceAccountId, externalOrderId);
        NumberDate = numberDate; NumberSequence = sequence; SaleNumber = $"{numberDate:yyMMdd}-{sequence}";
        Source = source; SalesChannelId = salesChannelId; QuoteRevisionId = quoteRevisionId;
        ConversionRequestId = conversionRequestId; MarketplaceAccountId = marketplaceAccountId;
        ExternalOrderId = Trim(externalOrderId); FeeSource = feeSource; CustomerId = customerId;
        CustomerNameSnapshot = Trim(customerNameSnapshot); SoldAt = soldAt; SoldDate = DateOnly.FromDateTime(soldAt.UtcDateTime);
        ShippingAmount = Rounding.ToMoney(shippingAmount); ExternalOrderCode = Trim(externalOrderCode); Notes = Trim(notes);
        for (var i = 0; i < items.Count; i++) _items.Add(new SaleItem(Id, i + 1, items[i]));
        RecomputeTotals();
        _history.Add(new SaleStatusHistory(Id, null, SaleStatus.CONFIRMED, null, now, actorId));
    }

    public string SaleNumber { get; private set; } = string.Empty;
    public DateOnly NumberDate { get; private set; }
    public int NumberSequence { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string? CustomerNameSnapshot { get; private set; }
    public Guid SalesChannelId { get; private set; }
    public Guid? QuoteRevisionId { get; private set; }
    public Guid? ConversionRequestId { get; private set; }
    public SaleSource Source { get; private set; }
    public Guid? MarketplaceAccountId { get; private set; }
    public string? ExternalOrderId { get; private set; }
    public SaleFeeSource FeeSource { get; private set; }
    public DateTimeOffset SoldAt { get; private set; }
    public DateOnly SoldDate { get; private set; }
    public SaleStatus Status { get; private set; } = SaleStatus.CONFIRMED;
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal NetAmount { get; private set; }
    public decimal ChannelFeeAmount { get; private set; }
    public decimal ShippingAmount { get; private set; }
    public decimal TotalCostAmount { get; private set; }
    public decimal GrossProfitAmount { get; private set; }
    public decimal EffectiveMarginPercent { get; private set; }
    public SaleCostBasis CostBasis { get; private set; } = SaleCostBasis.ESTIMATED;
    public string? ExternalOrderCode { get; private set; }
    public string? Notes { get; private set; }
    public IReadOnlyList<SaleItem> Items => _items.AsReadOnly();
    public IReadOnlyList<SaleStatusHistory> History => _history.AsReadOnly();

    public void Cancel(string reason, Guid? actorId, DateTimeOffset now)
    {
        if (Status != SaleStatus.CONFIRMED) throw new ArgumentException("SALE_INVALID_TRANSITION");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("SALE_CANCEL_REASON_REQUIRED");
        Status = SaleStatus.CANCELED;
        _history.Add(new SaleStatusHistory(Id, SaleStatus.CONFIRMED, SaleStatus.CANCELED, reason.Trim(), now, actorId));
    }

    private void RecomputeTotals()
    {
        GrossAmount = Rounding.ToMoney(_items.Sum(x => x.UnitPrice * x.Quantity));
        DiscountAmount = Rounding.ToMoney(_items.Sum(x => x.DiscountAmount * x.Quantity));
        NetAmount = Rounding.ToMoney(_items.Sum(x => x.LineTotalAmount));
        ChannelFeeAmount = Rounding.ToMoney(_items.Sum(x => x.ChannelFeeAmount));
        TotalCostAmount = Rounding.ToMoney(_items.Sum(x => x.LineCostAmount));
        GrossProfitAmount = Rounding.ToMoney(NetAmount - ChannelFeeAmount - ShippingAmount - TotalCostAmount);
        EffectiveMarginPercent = NetAmount > 0 ? Rounding.ToPercent(GrossProfitAmount / NetAmount) : 0m;
    }
    private static void ValidateSource(SaleSource source, Guid? quote, Guid? account, string? external)
    {
        var hasExternal = account is not null || !string.IsNullOrWhiteSpace(external);
        if (source == SaleSource.QUOTE_CONVERSION && (quote is null || hasExternal)) throw new ArgumentException("SALE_SOURCE_INVALID");
        if (source == SaleSource.MANUAL_ENTRY && (quote is not null || hasExternal)) throw new ArgumentException("SALE_SOURCE_INVALID");
        if (source == SaleSource.MARKETPLACE_ORDER && (quote is not null || account is null || string.IsNullOrWhiteSpace(external))) throw new ArgumentException("SALE_SOURCE_INVALID");
    }
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SaleItem : Entity, IOwnedBy<Sale>
{
    private SaleItem() { }
    internal SaleItem(Guid saleId, int lineNumber, SaleItemSnapshot snapshot)
    {
        if (snapshot.Quantity <= 0 || string.IsNullOrWhiteSpace(snapshot.ProductName)) throw new ArgumentException("SALE_ITEM_INVALID");
        SaleId = saleId; LineNumber = lineNumber; ProductId = snapshot.ProductId; QuoteItemId = snapshot.QuoteItemId;
        ProductNameSnapshot = snapshot.ProductName.Trim(); Quantity = Rounding.ToQuantity(snapshot.Quantity);
        UnitPrice = Rounding.ToMoney(snapshot.UnitPrice); DiscountAmount = Rounding.ToMoney(snapshot.DiscountAmount);
        LineTotalAmount = Rounding.ToMoney(snapshot.LineTotalAmount); UnitCostAmount = Rounding.ToInternal(snapshot.UnitCostAmount);
        LineCostAmount = Rounding.ToMoney(snapshot.LineCostAmount); ChannelFeeAmount = Rounding.ToMoney(snapshot.ChannelFeeAmount);
        GrossProfitAmount = Rounding.ToMoney(snapshot.GrossProfitAmount); EffectiveMarginPercent = Rounding.ToPercent(snapshot.EffectiveMarginPercent);
    }
    public Guid SaleId { get; private set; } public Guid ParentId => SaleId; public int LineNumber { get; private set; }
    public Guid? ProductId { get; private set; } public string ProductNameSnapshot { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; } public decimal UnitPrice { get; private set; } public decimal DiscountAmount { get; private set; }
    public decimal LineTotalAmount { get; private set; } public decimal UnitCostAmount { get; private set; } public decimal LineCostAmount { get; private set; }
    public decimal ChannelFeeAmount { get; private set; } public decimal GrossProfitAmount { get; private set; } public decimal EffectiveMarginPercent { get; private set; }
    public Guid? QuoteItemId { get; private set; }
}

public sealed class SaleStatusHistory : Entity, IOwnedBy<Sale>
{
    private SaleStatusHistory() { }
    internal SaleStatusHistory(Guid saleId, SaleStatus? from, SaleStatus to, string? reason, DateTimeOffset changedAt, Guid? changedBy)
    { SaleId = saleId; FromStatus = from; ToStatus = to; Reason = reason; ChangedAt = changedAt; ChangedBy = changedBy; }
    public Guid SaleId { get; private set; } public Guid ParentId => SaleId; public SaleStatus? FromStatus { get; private set; }
    public SaleStatus ToStatus { get; private set; } public string? Reason { get; private set; } public DateTimeOffset ChangedAt { get; private set; } public Guid? ChangedBy { get; private set; }
}
