using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Modules.Settings;

namespace Verce.IntegrationTests.Settings;

/// <summary>
/// M-03: <see cref="AppSettingValueReader.GetIntAsync"/> must never let an out-of-<see cref="int"/>-
/// range stored value reach the caller as an unhandled <see cref="OverflowException"/> (a generic
/// 500) — it must surface as a stable, catchable <see cref="ArgumentException"/>. Also proves the
/// domain-level validity-days bound (Quote.cs) rejects an in-range-for-Setting-but-absurd value
/// before it can ever reach <c>DateOnly.AddDays</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AppSettingValueReaderTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public AppSettingValueReaderTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE settings.app_setting RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("2147483648")] // int.MaxValue + 1
    [InlineData("99999999999999")]
    public async Task GetIntAsync_rejects_an_out_of_int_range_value_with_a_stable_code_instead_of_overflowing(string outOfRangeValue)
    {
        await using var db = _fixture.CreateContext();
        // The catalogue's own predicate for this key only checks ">= 1" — no upper bound — so an
        // out-of-int-range (but valid, positive) long value can genuinely be persisted through the
        // normal AppSetting constructor, exactly like a misconfigured operator input could.
        db.Add(new AppSetting("quote.default_validity_days", outOfRangeValue, AppSettingValueType.Int, "quote", "Validade padrão de orçamentos"));
        await db.SaveChangesAsync();

        var reader = new AppSettingValueReader(db);
        var act = async () => await reader.GetIntAsync("quote.default_validity_days", CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("SETTING_VALUE_OUT_OF_RANGE");
    }

    [Fact]
    public void An_out_of_int_range_negative_value_is_rejected_too_though_the_catalogs_own_predicate_gets_there_first()
    {
        // int.MinValue - 1 ("-2147483649") is a valid long, but "quote.default_validity_days"'s
        // OWN catalog predicate (">= 1") rejects any negative value before GetIntAsync's new
        // bounds check ever runs — defense in depth at an EARLIER layer, still never an unhandled
        // OverflowException either way.
        var act = () => new AppSetting("quote.default_validity_days", "-2147483649", AppSettingValueType.Int, "quote", "Validade padrão de orçamentos");
        act.Should().Throw<ArgumentException>().WithMessage("SETTING_VALUE_INVALID");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("15")]
    [InlineData("2147483647")] // int.MaxValue itself is a valid parse target, even though the domain further bounds it to 3650.
    public async Task GetIntAsync_accepts_values_within_int_range(string validValue)
    {
        await using var db = _fixture.CreateContext();
        db.Add(new AppSetting("quote.default_validity_days", validValue, AppSettingValueType.Int, "quote", "Validade padrão de orçamentos"));
        await db.SaveChangesAsync();

        var reader = new AppSettingValueReader(db);
        var result = await reader.GetIntAsync("quote.default_validity_days", CancellationToken.None);
        result.Should().Be(int.Parse(validValue));
    }

    [Fact]
    public async Task GetIntAsync_rejects_malformed_stored_text_with_the_existing_invalid_code()
    {
        await using (var seedDb = _fixture.CreateContext())
        {
            seedDb.Add(new AppSetting("quote.default_validity_days", "15", AppSettingValueType.Int, "quote", "Validade padrão de orçamentos"));
            await seedDb.SaveChangesAsync();
        }

        // Simulates corrupted/legacy stored data — bypasses the constructor's own validation via
        // raw SQL, since a well-formed AppSetting can never hold non-numeric text for an Int key.
        await using (var corruptDb = _fixture.CreateContext())
        {
            await corruptDb.Database.ExecuteSqlRawAsync(
                "UPDATE settings.app_setting SET value = 'not-a-number' WHERE key = 'quote.default_validity_days';");
        }

        // A FRESH context: GetIntAsync prefers an already-tracked Local instance (read-your-writes
        // within the SAME unit of work) over a DB round-trip — reusing the seeding context here
        // would silently serve its own stale in-memory "15" instead of the corrupted row.
        await using var readDb = _fixture.CreateContext();
        var reader = new AppSettingValueReader(readDb);
        var act = async () => await reader.GetIntAsync("quote.default_validity_days", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("SETTING_VALUE_INVALID");
    }
}
