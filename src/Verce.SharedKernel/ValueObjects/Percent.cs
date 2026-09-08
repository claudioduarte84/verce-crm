namespace Verce.SharedKernel.ValueObjects;

/// <summary>
/// Percentage stored as a FRACTION (0.175 = 17.5%), never as "17.5" (ADR-0002 §3, CLAUDE.md rule 4).
/// Only <see cref="Fraction"/> ≥ 0 is enforced here; contextual upper bounds (e.g. commission
/// &lt; 1) are applied by the callers that know the business meaning (CR-07.1).
/// </summary>
public readonly struct Percent : IEquatable<Percent>, IComparable<Percent>
{
    public decimal Fraction { get; }

    public Percent(decimal fraction)
    {
        if (fraction < 0)
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Percent fraction cannot be negative.");
        Fraction = fraction;
    }

    public static Percent Zero => new(0m);

    /// <summary>Constructs from a "human" percentage value: FromHumanPercent(17.5m) => 0.175.</summary>
    public static Percent FromHumanPercent(decimal humanPercent) => new(humanPercent / 100m);

    /// <summary>The value as a human-readable percentage: 0.175 => 17.5.</summary>
    public decimal ToHumanPercent() => Fraction * 100m;

    public static Percent operator +(Percent a, Percent b) => new(a.Fraction + b.Fraction);

    public static Percent operator -(Percent a, Percent b) => new(a.Fraction - b.Fraction);

    public static bool operator ==(Percent a, Percent b) => a.Fraction == b.Fraction;
    public static bool operator !=(Percent a, Percent b) => a.Fraction != b.Fraction;
    public static bool operator <(Percent a, Percent b) => a.Fraction < b.Fraction;
    public static bool operator <=(Percent a, Percent b) => a.Fraction <= b.Fraction;
    public static bool operator >(Percent a, Percent b) => a.Fraction > b.Fraction;
    public static bool operator >=(Percent a, Percent b) => a.Fraction >= b.Fraction;

    public bool Equals(Percent other) => Fraction == other.Fraction;
    public override bool Equals(object? obj) => obj is Percent p && Equals(p);
    public override int GetHashCode() => Fraction.GetHashCode();
    public int CompareTo(Percent other) => Fraction.CompareTo(other.Fraction);

    public override string ToString() => Fraction.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
}
