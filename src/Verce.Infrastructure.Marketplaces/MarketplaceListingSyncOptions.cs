using Microsoft.Extensions.Options;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>Operational limits owned by VERCE, not by a provider contract (handoff B6).</summary>
public sealed class MarketplaceListingSyncOptions
{
    public const string SectionName = "Marketplaces:ListingSync";
    /// <summary>When false no listing-sync Quartz job/trigger is registered and the immediate dispatch trigger is a no-op.</summary>
    public bool SchedulingEnabled { get; init; } = true;
    public int DispatchIntervalSeconds { get; init; } = 60;
    public int MaxConcurrentRuns { get; init; } = 2;
    public int MaxInFlightRequestsPerProvider { get; init; } = 2;
    public int MaxAttempts { get; init; } = 3;
    public int LeaseSeconds { get; init; } = 120;
    public int DetailBatchSize { get; init; } = 20;
}

public sealed class MarketplaceListingSyncOptionsValidator : IValidateOptions<MarketplaceListingSyncOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketplaceListingSyncOptions options)
    {
        var failures = new List<string>();
        if (options.DetailBatchSize is < 1 or > 20) failures.Add("Marketplaces:ListingSync:DetailBatchSize must be 1..20.");
        if (options.DispatchIntervalSeconds is < 10 or > 3600) failures.Add("Marketplaces:ListingSync:DispatchIntervalSeconds must be 10..3600.");
        if (options.MaxConcurrentRuns is < 1 or > 8) failures.Add("Marketplaces:ListingSync:MaxConcurrentRuns must be 1..8.");
        if (options.MaxInFlightRequestsPerProvider is < 1 or > 8) failures.Add("Marketplaces:ListingSync:MaxInFlightRequestsPerProvider must be 1..8.");
        if (options.MaxAttempts is < 1 or > 10) failures.Add("Marketplaces:ListingSync:MaxAttempts must be 1..10.");
        if (options.LeaseSeconds is < 30 or > 900) failures.Add("Marketplaces:ListingSync:LeaseSeconds must be 30..900.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
