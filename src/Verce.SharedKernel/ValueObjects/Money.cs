namespace Verce.SharedKernel.ValueObjects;

/// <summary>
/// Monetary value object (ADR-0002). Wraps <see cref="decimal"/> — never <c>double</c>/<c>float</c>.
/// Currency is implicit BRL in v1 (ADR-0002 §7): no currency field, no multi-currency arithmetic.
/// Internal precision is 6 decimal places; presentation rounding happens only at the points
/// specified in CALCULATION-RULES §0.3, never opportunistically inside this type.
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    public decimal Amount { get; }

    public Money(decimal amount)
    {
        // Store at internal (6dp) precision; callers round to presentation scale explicitly
        // at the points CALCULATION-RULES §0.3 specifies. We do NOT silently round here,
        // because that would hide exactly the class of bug this value object exists to prevent.
        Amount = amount;
    }

    public static Money Zero => new(0m);

    public static Money FromMoneyScale(decimal amount) => new(Rounding.ToMoney(amount));

    public static Money FromInternalScale(decimal amount) => new(Rounding.ToInternal(amount));

    public Money RoundToMoney() => new(Rounding.ToMoney(Amount));

    public Money RoundToInternal() => new(Rounding.ToInternal(Amount));

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount);

    public static Money operator -(Money a) => new(-a.Amount);

    /// <summary>Money may be scaled by a plain decimal factor (e.g. quantity), never by another Money.</summary>
    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor);

    public static Money operator *(decimal factor, Money a) => new(a.Amount * factor);

    public static Money operator /(Money a, decimal divisor) => new(a.Amount / divisor);

    public static bool operator ==(Money a, Money b) => a.Amount == b.Amount;
    public static bool operator !=(Money a, Money b) => a.Amount != b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;

    public bool Equals(Money other) => Amount == other.Amount;
    public override bool Equals(object? obj) => obj is Money m && Equals(m);
    public override int GetHashCode() => Amount.GetHashCode();
    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    /// <summary>
    /// Wire representation: a string, never a JSON number (ADR-0002 §6, ADR-0014 §3).
    /// The frontend never performs arithmetic on this value.
    /// </summary>
    public override string ToString() => Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
}
