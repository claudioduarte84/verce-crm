namespace Verce.SharedKernel.Time;

/// <summary>
/// The only legitimate source of "now" anywhere in domain/application code (CLAUDE.md rule,
/// ADR-0004). No <c>DateTime.Now</c> or <c>DateTime.UtcNow</c> may appear outside an
/// <see cref="IClock"/> implementation — enforced by Verce.Architecture.Tests.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC. All persisted instants use this.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// The current business date in the organization's timezone (America/Sao_Paulo).
    /// Used for daily quote numbering, validity windows and anything the operator perceives
    /// as "today" — never derived from <see cref="UtcNow"/>.Date, which is wrong near
    /// midnight UTC offset boundaries (ADR-0004).
    /// </summary>
    DateOnly OrganizationToday();

    /// <summary>The organization's IANA timezone id. Fixed to America/Sao_Paulo in v1.</summary>
    string OrganizationTimeZoneId { get; }
}
