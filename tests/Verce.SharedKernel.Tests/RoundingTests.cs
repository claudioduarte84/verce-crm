using FluentAssertions;
using Verce.SharedKernel;

namespace Verce.SharedKernel.Tests;

/// <summary>
/// CR-00.1 / CR-00.2 (CALCULATION-RULES §0): half-up (MidpointRounding.AwayFromZero) at fixed
/// scales. Everything downstream (the future cost/pricing engines) depends on this being right.
/// </summary>
public class RoundingTests
{
    [Theory]
    [InlineData(12.345, 12.35)]   // classic half-up case: away-from-zero rounds up
    [InlineData(12.344, 12.34)]
    [InlineData(-12.345, -12.35)] // away-from-zero rounds the negative magnitude up too
    [InlineData(0.005, 0.01)]
    [InlineData(1.005, 1.01)]
    public void ToMoney_rounds_half_away_from_zero(decimal input, decimal expected)
    {
        Rounding.ToMoney(input).Should().Be(expected);
    }

    [Fact]
    public void ToMoney_uses_banker_rejecting_mode()
    {
        // .NET's default Math.Round uses ToEven (banker's rounding); we must NOT get that.
        var bankersResult = Math.Round(2.5m, 0, MidpointRounding.ToEven); // => 2 (even)
        var ourResult = Rounding.ToScale(2.5m, 0);                        // => 3 (away from zero)

        bankersResult.Should().Be(2m);
        ourResult.Should().Be(3m);
    }

    [Fact]
    public void ToInternal_preserves_six_decimal_places()
    {
        // G1 from CALCULATION-RULES: 72g PLA @ 89.90/kg => 6.472800
        var grams = 72m;
        var pricePerKg = 89.90m;
        var raw = grams / 1000m * pricePerKg;

        Rounding.ToInternal(raw).Should().Be(6.472800m);
    }

    [Fact]
    public void ToPercent_preserves_six_decimal_places()
    {
        Rounding.ToPercent(0.1750001234m).Should().Be(0.175000m);
    }

    [Theory]
    [InlineData(RoundingScaleKind.Money, 2)]
    [InlineData(RoundingScaleKind.Internal, 6)]
    [InlineData(RoundingScaleKind.Percent, 6)]
    [InlineData(RoundingScaleKind.Grams, 3)]
    [InlineData(RoundingScaleKind.Kwh, 4)]
    [InlineData(RoundingScaleKind.Quantity, 4)]
    public void Constants_match_the_documented_scales(RoundingScaleKind kind, int expectedScale)
    {
        var actual = kind switch
        {
            RoundingScaleKind.Money => Rounding.MoneyScale,
            RoundingScaleKind.Internal => Rounding.InternalScale,
            RoundingScaleKind.Percent => Rounding.PercentScale,
            RoundingScaleKind.Grams => Rounding.GramsScale,
            RoundingScaleKind.Kwh => Rounding.KwhScale,
            RoundingScaleKind.Quantity => Rounding.QuantityScale,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        actual.Should().Be(expectedScale);
    }

    public enum RoundingScaleKind { Money, Internal, Percent, Grams, Kwh, Quantity }
}
