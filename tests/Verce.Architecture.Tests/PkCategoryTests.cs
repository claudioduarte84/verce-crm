using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

/// <summary>
/// ADR-0011 §1: every entity type in the EF model is classified either by a marker interface
/// (application-owned) or by an explicit framework-registry allow-list (framework-owned) —
/// never by neither, and never by naming convention.
/// </summary>
public class PkCategoryTests
{
    // The "technical registry" (ADR-0011 §1.1): framework-owned types that CANNOT implement
    // our marker interfaces. This is the only allow-list in the system, and it is reviewed here
    // rather than left as an ad-hoc per-table exception.
    private static readonly HashSet<string> FrameworkOwnedTypeNames = new()
    {
        "Verce.Platform.Identity.ApplicationUser",
        "Verce.Platform.Identity.ApplicationRole",
        "Microsoft.AspNetCore.Identity.IdentityUserRole`1",
        "Microsoft.AspNetCore.Identity.IdentityUserClaim`1",
        "Microsoft.AspNetCore.Identity.IdentityUserLogin`1",
        "Microsoft.AspNetCore.Identity.IdentityUserToken`1",
        "Microsoft.AspNetCore.Identity.IdentityRoleClaim`1",
        "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey",
    };

    // Closed generic framework types (e.g. IdentityRoleClaim<Guid>) render their FullName with
    // full assembly-qualified generic argument info (e.g. "...IdentityRoleClaim`1[[System.Guid,
    // System.Private.CoreLib, ...]]"), which never string-equals a plain allow-list entry. The
    // open generic type definition's FullName (e.g. "...IdentityRoleClaim`1") is stable and is
    // what the allow-list is written against.
    private static bool IsFrameworkOwned(Type clrType)
    {
        var name = clrType.IsGenericType
            ? clrType.GetGenericTypeDefinition().FullName
            : clrType.FullName;
        return FrameworkOwnedTypeNames.Contains(name ?? clrType.Name);
    }

    private static DbContextOptions<VerceDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<VerceDbContext>()
            .UseNpgsql("Host=localhost;Database=verce_model_only") // model-building only, never opened
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public void Every_entity_in_the_model_is_classified_by_exactly_one_category()
    {
        using var context = new VerceDbContext(BuildOptions());
        var model = context.Model;

        var unclassified = new List<string>();
        var overclassified = new List<string>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (IsFrameworkOwned(clrType)) continue;

            var categories = new[]
            {
                typeof(IDomainEntity).IsAssignableFrom(clrType) && !typeof(IMasterData).IsAssignableFrom(clrType),
                typeof(IMasterData).IsAssignableFrom(clrType),
                typeof(IReferenceData).IsAssignableFrom(clrType),
                typeof(ITechnicalTable).IsAssignableFrom(clrType),
                typeof(IJoinTable).IsAssignableFrom(clrType),
            };

            var matchCount = categories.Count(c => c);
            if (matchCount == 0) unclassified.Add(clrType.FullName ?? clrType.Name);
            if (matchCount > 1) overclassified.Add(clrType.FullName ?? clrType.Name);
        }

        unclassified.Should().BeEmpty("every application-owned entity must implement exactly one PK category marker, or be in the framework registry");
        overclassified.Should().BeEmpty("a type must not implement more than one PK category marker");
    }

    [Fact]
    public void IDomainEntity_and_IMasterData_types_have_a_Guid_primary_key()
    {
        using var context = new VerceDbContext(BuildOptions());
        var violations = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (IsFrameworkOwned(clrType)) continue;
            if (!typeof(IDomainEntity).IsAssignableFrom(clrType)) continue;

            var pk = entityType.FindPrimaryKey();
            if (pk is null || pk.Properties.Count != 1 || pk.Properties[0].ClrType != typeof(Guid))
                violations.Add(clrType.FullName ?? clrType.Name);
        }

        violations.Should().BeEmpty("Category 1/2 entities (IDomainEntity/IMasterData) must have a single Guid primary key");
    }

    [Fact]
    public void Framework_owned_types_are_not_required_to_implement_application_markers()
    {
        using var context = new VerceDbContext(BuildOptions());
        var frameworkTypesInModel = context.Model.GetEntityTypes()
            .Select(e => e.ClrType)
            .Where(IsFrameworkOwned)
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        // Sanity check that the allow-list actually corresponds to real types in THIS model —
        // an allow-list entry for a type that no longer exists would silently stop protecting
        // anything.
        frameworkTypesInModel.Should().NotBeEmpty("the registry must reference real framework types present in the model");
    }

    [Fact]
    public void A_closed_generic_Identity_type_is_recognized_via_its_generic_type_definition()
    {
        // Regression for the checkpoint-001 fix: IdentityRoleClaim<Guid>.FullName is the long
        // assembly-qualified generic form, which never string-equals the allow-list entry
        // "...IdentityRoleClaim`1" directly. IsFrameworkOwned must go through
        // GetGenericTypeDefinition().FullName for this to match at all.
        var closedGenericType = typeof(Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>);

        closedGenericType.FullName.Should().Contain("[[",
            "sanity check: a closed generic FullName really does carry bracketed type-argument info that cannot equal the open definition's FullName");

        IsFrameworkOwned(closedGenericType).Should().BeTrue(
            "a closed generic ASP.NET Identity type must be recognized as framework-owned via its generic type definition, not a literal string match");
    }

    [Fact]
    public void TechnicalEntity_derived_outbox_types_are_ITechnicalTable_and_never_IDomainEntity()
    {
        // Regression for the checkpoint-001 fix: OutboxMessage/OutboxMessageAttempt used to
        // derive from Entity (which unconditionally implements IDomainEntity), while also
        // declaring ITechnicalTable — an ADR-0011 §1 category-exclusivity violation. They now
        // derive from TechnicalEntity, which provides the same Id/equality shape without the
        // IDomainEntity marker. This pins the specific defect down directly, independent of the
        // whole-model scan in Every_entity_in_the_model_is_classified_by_exactly_one_category.
        typeof(Verce.Platform.Outbox.OutboxMessage).Should().BeAssignableTo<ITechnicalTable>();
        typeof(Verce.Platform.Outbox.OutboxMessage).Should().NotBeAssignableTo<IDomainEntity>();

        typeof(Verce.Platform.Outbox.OutboxMessageAttempt).Should().BeAssignableTo<ITechnicalTable>();
        typeof(Verce.Platform.Outbox.OutboxMessageAttempt).Should().NotBeAssignableTo<IDomainEntity>();
    }
}
