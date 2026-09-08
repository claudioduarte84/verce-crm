using FluentAssertions;
using Verce.SharedKernel.ValueObjects;

namespace Verce.SharedKernel.Tests;

/// <summary>CLAUDE.md rule 4 / ADR-0002 §3: percent is a fraction, never "17.5" for 17.5%.</summary>
public class PercentTests
{
    [Fact]
    public void FromHumanPercent_converts_17_5_to_0_175_fraction()
    {
        var p = Percent.FromHumanPercent(17.5m);
        p.Fraction.Should().Be(0.175m);
    }

    [Fact]
    public void ToHumanPercent_converts_fraction_back_for_display()
    {
        var p = new Percent(0.175m);
        p.ToHumanPercent().Should().Be(17.5m);
    }

    [Fact]
    public void Negative_fraction_is_rejected_at_construction()
    {
        var act = () => new Percent(-0.01m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zero_percent_is_valid()
    {
        var p = Percent.Zero;
        p.Fraction.Should().Be(0m);
    }

    [Fact]
    public void Addition_combines_fractions()
    {
        var commission = Percent.FromHumanPercent(18m);
        var margin = Percent.FromHumanPercent(35m);

        var sum = commission + margin;

        sum.Fraction.Should().Be(0.53m); // matches CR-07.2 worked example denominator basis
    }
}
