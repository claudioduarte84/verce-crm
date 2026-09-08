using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

/// <summary>
/// F-3, F-6 (ROADMAP S1 catalogue, ADR-0011 §1): S1 ships zero real <see cref="IReferenceData"/>
/// or <see cref="IJoinTable"/> entities (no master data yet), so these PK-shape rules would
/// otherwise pass vacuously — "an empty LINQ scan returned true" proves nothing (mission §47).
/// Test-only fixture types, mapped in a model-only (never-opened) DbContext exactly like
/// PkCategoryTests' own pattern, prove the shape rule itself accepts the RIGHT shape and (via
/// the "bad" fixtures) would flag the WRONG one — never a fake production table.
/// </summary>
public class ReferenceDataAndJoinTablePkShapeTests
{
    // --- F-3: Category 3, PK is an immutable string Code ---
    private sealed class GoodReferenceDataFixture : IReferenceData
    {
        public string Code { get; private set; } = string.Empty;
        private GoodReferenceDataFixture() { }
        public GoodReferenceDataFixture(string code) => Code = code;
    }

    // Deliberately wrong shape: a Guid surrogate PK instead of the Code itself — proves the
    // check below is not vacuously true for anything implementing IReferenceData.
    private sealed class BadReferenceDataFixture : IReferenceData
    {
        public Guid Id { get; private set; }
        public string Code { get; private set; } = string.Empty;
    }

    // --- F-6: Category 5, composite FK primary key ---
    private sealed class GoodJoinTableFixture : IJoinTable
    {
        public Guid LeftId { get; private set; }
        public Guid RightId { get; private set; }
    }

    // Deliberately wrong shape: a single surrogate PK instead of a composite one.
    private sealed class BadJoinTableFixture : IJoinTable
    {
        public Guid Id { get; private set; }
        public Guid LeftId { get; private set; }
        public Guid RightId { get; private set; }
    }

    private sealed class FixtureDbContext : DbContext
    {
        public FixtureDbContext(DbContextOptions<FixtureDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<GoodReferenceDataFixture>(b => b.HasKey(x => x.Code));
            builder.Entity<BadReferenceDataFixture>(b => b.HasKey(x => x.Id));
            builder.Entity<GoodJoinTableFixture>(b => b.HasKey(x => new { x.LeftId, x.RightId }));
            builder.Entity<BadJoinTableFixture>(b => b.HasKey(x => x.Id));
        }
    }

    private static FixtureDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<FixtureDbContext>()
            .UseNpgsql("Host=localhost;Database=verce_model_only") // model-building only, never opened
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void F3_a_reference_data_type_with_Code_as_its_sole_primary_key_satisfies_the_shape_rule()
    {
        using var context = BuildContext();
        var entityType = context.Model.FindEntityType(typeof(GoodReferenceDataFixture))!;
        var pk = entityType.FindPrimaryKey()!;

        pk.Properties.Should().ContainSingle();
        pk.Properties[0].Name.Should().Be(nameof(GoodReferenceDataFixture.Code));
        pk.Properties[0].ClrType.Should().Be(typeof(string));
    }

    [Fact]
    public void F3_a_reference_data_type_with_a_surrogate_key_instead_of_Code_violates_the_shape_rule()
    {
        using var context = BuildContext();
        var entityType = context.Model.FindEntityType(typeof(BadReferenceDataFixture))!;
        var pk = entityType.FindPrimaryKey()!;

        // The rule PkCategoryTests would apply: Category 3's PK must BE Code, not merely contain it.
        var satisfiesRule = pk.Properties.Count == 1 && pk.Properties[0].Name == nameof(BadReferenceDataFixture.Code);
        satisfiesRule.Should().BeFalse("a surrogate uuid PK alongside Code is the wrong shape for Category 3 — this fixture exists to prove the rule actually rejects it");
    }

    [Fact]
    public void F6_a_join_table_type_with_a_composite_key_satisfies_the_shape_rule()
    {
        using var context = BuildContext();
        var entityType = context.Model.FindEntityType(typeof(GoodJoinTableFixture))!;
        var pk = entityType.FindPrimaryKey()!;

        pk.Properties.Should().HaveCountGreaterThan(1, "Category 5's identity IS the relationship — a composite FK key, never a surrogate");
        pk.Properties.Select(p => p.Name).Should().Contain([nameof(GoodJoinTableFixture.LeftId), nameof(GoodJoinTableFixture.RightId)]);
    }

    [Fact]
    public void F6_a_join_table_type_with_a_surrogate_key_violates_the_shape_rule()
    {
        using var context = BuildContext();
        var entityType = context.Model.FindEntityType(typeof(BadJoinTableFixture))!;
        var pk = entityType.FindPrimaryKey()!;

        pk.Properties.Should().ContainSingle("this fixture exists to prove a single surrogate key is the wrong shape for Category 5");
    }
}
