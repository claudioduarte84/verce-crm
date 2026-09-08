namespace Verce.SharedKernel.Time;

/// <summary>
/// Controllable clock for unit/integration tests. Never referenced from production code paths
/// — only from test projects, which reference SharedKernel directly.
/// </summary>
public sealed class TestClock : IClock
{
    private readonly TimeZoneInfo _organizationTimeZone;

    public TestClock(DateTimeOffset utcNow, string organizationTimeZoneId = SystemClock.DefaultOrganizationTimeZoneId)
    {
        UtcNow = utcNow;
        OrganizationTimeZoneId = organizationTimeZoneId;
        _organizationTimeZone = TimeZoneInfo.FindSystemTimeZoneById(organizationTimeZoneId);
    }

    public DateTimeOffset UtcNow { get; set; }

    public string OrganizationTimeZoneId { get; }

    public DateOnly OrganizationToday()
    {
        var localNow = TimeZoneInfo.ConvertTime(UtcNow, _organizationTimeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }
}
