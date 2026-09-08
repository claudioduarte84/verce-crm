namespace Verce.SharedKernel.Time;

/// <summary>Production <see cref="IClock"/> implementation, backed by real wall-clock time.</summary>
public sealed class SystemClock : IClock
{
    public const string DefaultOrganizationTimeZoneId = "America/Sao_Paulo";

    private readonly TimeZoneInfo _organizationTimeZone;

    public SystemClock(string organizationTimeZoneId = DefaultOrganizationTimeZoneId)
    {
        OrganizationTimeZoneId = organizationTimeZoneId;
        // .NET Core 6+ resolves IANA IDs on all platforms (Windows included, via ICU),
        // so "America/Sao_Paulo" works without a Windows-specific alias.
        _organizationTimeZone = TimeZoneInfo.FindSystemTimeZoneById(organizationTimeZoneId);
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public string OrganizationTimeZoneId { get; }

    public DateOnly OrganizationToday()
    {
        var localNow = TimeZoneInfo.ConvertTime(UtcNow, _organizationTimeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }
}
