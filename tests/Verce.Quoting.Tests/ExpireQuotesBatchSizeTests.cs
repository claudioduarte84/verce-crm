using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verce.Modules.Quoting;

namespace Verce.Quoting.Tests;

/// <summary>F-04: <see cref="ExpireQuotesService.MaxBatchSize"/> is a finite technical ceiling —
/// any positive int up to <see cref="int.MaxValue"/> was previously accepted, defeating H-02's own
/// bounded-query invariant. These cases fire the defensive argument check BEFORE the method ever
/// touches its <c>VerceDbContext</c>/<c>IUnitOfWork</c>/<c>IClock</c> dependencies, so passing
/// <c>null!</c> for all three is safe and keeps this a pure, DB-free unit test — valid-value
/// acceptance (which DOES need real infrastructure) is covered in the integration suite.</summary>
public class ExpireQuotesBatchSizeTests
{
    private static ExpireQuotesService NewService() => new(null!, null!, null!);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExpireQuotesService.MaxBatchSize + 1)]
    [InlineData(int.MaxValue)]
    public async Task ExpireEligibleAsync_rejects_an_out_of_range_batch_size_before_touching_any_dependency(int invalidBatchSize)
    {
        var service = NewService();
        var act = async () => await service.ExpireEligibleAsync(CancellationToken.None, invalidBatchSize);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("EXPIRE_QUOTES_BATCH_SIZE_INVALID");
    }
}

/// <summary>F-04 §28-30: the Quartz-bound <see cref="ExpireQuotesJobOptions.BatchSize"/> is
/// validated once, at startup (composition time), never silently clamped.</summary>
public class ExpireQuotesJobOptionsStartupValidationTests
{
    private static IConfiguration ConfigWithBatchSize(int? batchSize, bool schedulingEnabled = false)
    {
        var values = new Dictionary<string, string?> { ["Quoting:Expiration:SchedulingEnabled"] = schedulingEnabled.ToString() };
        if (batchSize is { } size) values["Quoting:Expiration:BatchSize"] = size.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(values!).Build();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExpireQuotesService.MaxBatchSize + 1)]
    [InlineData(int.MaxValue)]
    public void AddVerceQuotingScheduling_fails_fast_on_an_invalid_configured_batch_size(int invalidBatchSize)
    {
        var services = new ServiceCollection();
        var act = () => services.AddVerceQuotingScheduling(ConfigWithBatchSize(invalidBatchSize));
        act.Should().Throw<InvalidOperationException>().WithMessage("EXPIRE_QUOTES_BATCH_SIZE_INVALID*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(ExpireQuotesService.MaxBatchSize)]
    [InlineData(null)] // unset -> ExpireQuotesJobOptions' own default (DefaultBatchSize)
    public void AddVerceQuotingScheduling_accepts_a_valid_configured_batch_size(int? validBatchSize)
    {
        var services = new ServiceCollection();
        var act = () => services.AddVerceQuotingScheduling(ConfigWithBatchSize(validBatchSize));
        act.Should().NotThrow();
    }
}
