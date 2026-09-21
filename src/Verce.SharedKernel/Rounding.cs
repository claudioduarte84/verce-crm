namespace Verce.SharedKernel;

/// <summary>
/// The single rounding contract for the whole system (ADR-0002, CALCULATION-RULES §0).
/// Every monetary/percent/quantity calculation MUST route through these helpers so that
/// engine, database, API and frontend can never disagree about a rounded value.
/// </summary>
public static class Rounding
{
    /// <summary>Presented money: unit price, line total, document totals. numeric(18,2).</summary>
    public const int MoneyScale = 2;

    /// <summary>Intermediate money: component cost, unit cost, cost per gram. numeric(18,6).</summary>
    public const int InternalScale = 6;

    /// <summary>Percent, stored as a fraction (0.175 = 17.5%). numeric(9,6).</summary>
    public const int PercentScale = 6;

    /// <summary>Weight in grams. numeric(12,3).</summary>
    public const int GramsScale = 3;

    /// <summary>Energy in kWh. numeric(12,4).</summary>
    public const int KwhScale = 4;

    /// <summary>Generic quantity. numeric(14,4).</summary>
    public const int QuantityScale = 4;

    /// <summary>
    /// Half-up, everywhere, no exceptions (CR-00.2). Banker's rounding is explicitly rejected:
    /// it is commercially surprising, which is exactly the failure mode this product exists to
    /// eliminate.
    /// </summary>
    public const MidpointRounding Mode = MidpointRounding.AwayFromZero;

    /// <summary>Rounds to money scale (2) using the system-wide rounding mode.</summary>
    public static decimal ToMoney(decimal value) => Math.Round(value, MoneyScale, Mode);

    /// <summary>Rounds to internal scale (6) using the system-wide rounding mode.</summary>
    public static decimal ToInternal(decimal value) => Math.Round(value, InternalScale, Mode);

    /// <summary>Rounds to percent scale (6) using the system-wide rounding mode.</summary>
    public static decimal ToPercent(decimal value) => Math.Round(value, PercentScale, Mode);

    /// <summary>Rounds to grams scale (3) using the system-wide rounding mode.</summary>
    public static decimal ToGrams(decimal value) => Math.Round(value, GramsScale, Mode);

    /// <summary>Rounds to kWh scale (4) using the system-wide rounding mode.</summary>
    public static decimal ToKwh(decimal value) => Math.Round(value, KwhScale, Mode);

    /// <summary>Rounds to quantity scale (4) using the system-wide rounding mode.</summary>
    public static decimal ToQuantity(decimal value) => Math.Round(value, QuantityScale, Mode);

    /// <summary>Rounds to an arbitrary scale using the system-wide rounding mode.</summary>
    public static decimal ToScale(decimal value, int scale) => Math.Round(value, scale, Mode);

    /// <summary>
    /// True iff <paramref name="value"/> has no more than <see cref="MoneyScale"/> (2) significant
    /// fractional digits — i.e. it is already a whole number of cents (B-03: a PER_ORDER fixed
    /// fee is a BRL amount partitioned in cents, so a fee with a third fractional digit, e.g.
    /// <c>1.005</c>, can never be exactly partitioned and must be REJECTED, never silently
    /// rounded/truncated/coerced). Trailing-zero scale differences never matter here: decimal
    /// equality compares value, not representation, so <c>1.0m == 1.00m</c> is true.
    /// </summary>
    public static bool HasMoneyPrecision(decimal value) => value == Math.Round(value, MoneyScale, Mode);
}
