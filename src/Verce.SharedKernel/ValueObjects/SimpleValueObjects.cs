namespace Verce.SharedKernel.ValueObjects;

/// <summary>Filament weight, always grams — never kilograms (DOMAIN-MODEL §1). numeric(12,3).</summary>
public readonly struct Grams : IEquatable<Grams>, IComparable<Grams>
{
    public decimal Value { get; }

    public Grams(decimal value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Grams cannot be negative.");
        Value = value;
    }

    public static Grams Zero => new(0m);
    public decimal ToKilograms() => Value / 1000m;

    public static Grams operator +(Grams a, Grams b) => new(a.Value + b.Value);
    public static bool operator ==(Grams a, Grams b) => a.Value == b.Value;
    public static bool operator !=(Grams a, Grams b) => a.Value != b.Value;
    public bool Equals(Grams other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Grams g && Equals(g);
    public override int GetHashCode() => Value.GetHashCode();
    public int CompareTo(Grams other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Energy in kWh (DOMAIN-MODEL §1). numeric(12,4).</summary>
public readonly struct Kwh : IEquatable<Kwh>, IComparable<Kwh>
{
    public decimal Value { get; }

    public Kwh(decimal value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Kwh cannot be negative.");
        Value = value;
    }

    public static Kwh Zero => new(0m);
    public static Kwh operator +(Kwh a, Kwh b) => new(a.Value + b.Value);
    public static bool operator ==(Kwh a, Kwh b) => a.Value == b.Value;
    public static bool operator !=(Kwh a, Kwh b) => a.Value != b.Value;
    public bool Equals(Kwh other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Kwh k && Equals(k);
    public override int GetHashCode() => Value.GetHashCode();
    public int CompareTo(Kwh other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Duration in whole seconds (DOMAIN-MODEL §1).</summary>
public readonly struct DurationSeconds : IEquatable<DurationSeconds>
{
    public int Value { get; }

    public DurationSeconds(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Duration cannot be negative.");
        Value = value;
    }

    public decimal ToHours() => (decimal)Value / 3600m;
    public bool Equals(DurationSeconds other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is DurationSeconds d && Equals(d);
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Half-open date range [From, Until) (DOMAIN-MODEL §1). Used for temporal versioning windows.</summary>
public readonly struct DateRange : IEquatable<DateRange>
{
    public DateOnly From { get; }
    public DateOnly? Until { get; }

    public DateRange(DateOnly from, DateOnly? until)
    {
        if (until.HasValue && until.Value <= from)
            throw new ArgumentException("Until must be strictly after From in a half-open range.", nameof(until));
        From = from;
        Until = until;
    }

    public bool Contains(DateOnly date) => date >= From && (!Until.HasValue || date < Until.Value);

    public bool Equals(DateRange other) => From == other.From && Until == other.Until;
    public override bool Equals(object? obj) => obj is DateRange r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(From, Until);
}
