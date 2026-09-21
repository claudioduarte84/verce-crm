namespace Verce.SharedKernel.Time;

/// <summary>
/// Pure timezone-conversion helpers shared by every module that must bucket an already-known
/// instant into an organization business date WITHOUT depending on <see cref="IClock"/> for "now"
/// (a synchronous domain event handler may not depend on any injected service — ADR-0012 §9 —
/// but converting an already-resolved <see cref="DateTimeOffset"/> is not a "now" source, exactly
/// like <c>FeeRule.ResolveVersionAt(DateOnly instant)</c> takes time as a parameter). Both
/// <c>Verce.Modules.Quoting</c>'s outcome calculator and <c>Verce.Modules.Production</c>'s domain
/// event handlers call this SAME method so the conversion can never diverge between modules.
/// </summary>
public static class OrganizationTimeZone
{
    /// <summary>Converts a UTC instant to the organization's local business date. The identical
    /// conversion <see cref="SystemClock.OrganizationToday"/> performs for "today".</summary>
    public static DateOnly ToOrganizationDate(DateTimeOffset instant, string organizationTimeZoneId = SystemClock.DefaultOrganizationTimeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(organizationTimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }
}
