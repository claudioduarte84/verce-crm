using FluentAssertions;
using Verce.SharedKernel.Time;

namespace Verce.SharedKernel.Tests;

/// <summary>
/// ADR-0004: business dates must resolve in America/Sao_Paulo, never from UTC.Date directly —
/// the classic bug the docs call out is a quote created at 22:00 BRT on 06/09 getting numbered
/// 260907 instead of 260906.
/// </summary>
public class ClockTests
{
    [Fact]
    public void OrganizationToday_at_22h_BRT_is_still_the_same_calendar_day_in_BRT()
    {
        // 06/09/2026 22:00 BRT = 07/09/2026 01:00 UTC (BRT is UTC-3, no DST since 2019)
        var utcInstant = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(utcInstant);

        var businessDate = clock.OrganizationToday();

        businessDate.Should().Be(new DateOnly(2026, 9, 6));
    }

    [Fact]
    public void OrganizationToday_just_after_midnight_UTC_is_still_previous_day_in_BRT()
    {
        // 00:30 UTC on 07/09 = 21:30 BRT on 06/09 — this is the boundary the naive
        // "DateTime.UtcNow.Date" bug gets wrong.
        var utcInstant = new DateTimeOffset(2026, 9, 7, 0, 30, 0, TimeSpan.Zero);
        var clock = new TestClock(utcInstant);

        clock.OrganizationToday().Should().Be(new DateOnly(2026, 9, 6));

        // Prove the naive approach would have been wrong:
        DateOnly.FromDateTime(utcInstant.UtcDateTime).Should().Be(new DateOnly(2026, 9, 7));
    }

    [Fact]
    public void SystemClock_resolves_the_IANA_timezone_id_on_this_platform()
    {
        // This is the test that would fail if .NET's ICU-backed IANA resolution were
        // unavailable on the current OS/runtime combination.
        var act = () => new SystemClock();
        act.Should().NotThrow();
    }

    [Fact]
    public void SystemClock_UtcNow_is_actually_UTC()
    {
        var clock = new SystemClock();
        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void OrganizationTimeZoneId_defaults_to_America_Sao_Paulo()
    {
        var clock = new SystemClock();
        clock.OrganizationTimeZoneId.Should().Be("America/Sao_Paulo");
    }
}
