using FluentAssertions;
using Verce.SharedKernel.ValueObjects;

namespace Verce.SharedKernel.Tests;

public class MoneyTests
{
    [Fact]
    public void Addition_and_subtraction_are_exact_decimal_arithmetic()
    {
        var a = new Money(10.10m);
        var b = new Money(5.05m);

        (a + b).Amount.Should().Be(15.15m);
        (a - b).Amount.Should().Be(5.05m);
    }

    [Fact]
    public void Multiplication_by_quantity_scales_correctly()
    {
        // G8 from CALCULATION-RULES: 40.52 * 7 = 283.64
        var unitPrice = new Money(40.52m);
        var quantity = 7m;

        var lineTotal = (unitPrice * quantity).RoundToMoney();

        lineTotal.Amount.Should().Be(283.64m);
    }

    [Fact]
    public void Comparison_operators_work()
    {
        var small = new Money(1.00m);
        var big = new Money(2.00m);

        (small < big).Should().BeTrue();
        (big > small).Should().BeTrue();
        (small == new Money(1.00m)).Should().BeTrue();
        (small != big).Should().BeTrue();
    }

    [Fact]
    public void FromMoneyScale_rounds_to_two_decimal_places()
    {
        var m = Money.FromMoneyScale(12.345m);
        m.Amount.Should().Be(12.35m); // away-from-zero
    }

    [Fact]
    public void Zero_is_the_additive_identity()
    {
        var m = new Money(42.00m);
        (m + Money.Zero).Should().Be(m);
    }

    [Fact]
    public void ToString_formats_with_two_decimals_invariant_culture()
    {
        var m = new Money(1234.5m);
        m.ToString().Should().Be("1234.50");
    }
}
