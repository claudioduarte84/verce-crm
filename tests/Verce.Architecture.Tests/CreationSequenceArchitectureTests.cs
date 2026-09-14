using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Verce.Modules.Customers;

namespace Verce.Architecture.Tests;

/// <summary>
/// S2 Wave 1 gate correction (VERCE3D-M0-S2-WAVE1-CREATION-SEQUENCE-IMPLEMENTATION-001),
/// mission §26: CreationSequence must be provably internal persistence metadata — a shadow
/// property, database-allocated, never a public Customer CLR member, never present on any
/// request/response DTO — checked via reflection/EF metadata rather than brittle whole-file
/// text matching wherever metadata inspection makes that possible (source scanning is kept only
/// for the one rule metadata cannot express: "no MAX(...)+1 allocation in application code").
/// </summary>
public class CreationSequenceArchitectureTests
{
    /// <summary>
    /// Applies ONLY <see cref="CustomerConfiguration"/>, deliberately not going through
    /// <c>VerceDbContext</c> — that type's module-assembly discovery is a process-wide static
    /// (<c>VerceDbContext.ConfigureModuleAssemblies</c>), and mutating it here would leak into
    /// other test classes that build a bare <c>VerceDbContext</c> without expecting the
    /// Customers/Settings modules to be wired up (e.g. <see cref="PkCategoryTests"/>), depending
    /// on xUnit's test-class run order. This context is self-contained and side-effect-free.
    /// </summary>
    private sealed class CustomerOnlyContext(DbContextOptions<CustomerOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyConfiguration(new CustomerConfiguration());
    }

    private static DbContextOptions<CustomerOnlyContext> BuildOptions() =>
        new DbContextOptionsBuilder<CustomerOnlyContext>()
            .UseNpgsql("Host=localhost;Database=verce_model_only") // model-building only, never opened
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public void CreationSequence_is_a_required_unique_database_generated_shadow_property()
    {
        using var context = new CustomerOnlyContext(BuildOptions());
        var entityType = context.Model.FindEntityType(typeof(Customer))!;
        var property = entityType.FindProperty("CreationSequence");

        property.Should().NotBeNull();
        property!.IsShadowProperty().Should().BeTrue();
        property.ClrType.Should().Be(typeof(long));
        property.GetColumnName().Should().Be("creation_sequence");
        property.IsNullable.Should().BeFalse();
        property.ValueGenerated.Should().Be(ValueGenerated.OnAdd, "allocation must be database-generated on insert, never computed in application code");
        property.GetDefaultValueSql().Should().Contain("nextval(", "the database default must draw from a real PostgreSQL sequence");
        property.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Throw, "an already-persisted value must never be silently overwritten");

        entityType.GetIndexes().Should().Contain(
            index => index.Properties.Count == 1 && index.Properties[0] == property && index.IsUnique,
            "the database must independently guarantee uniqueness, not merely rely on sequence allocation");
    }

    [Fact]
    public void Customer_never_exposes_CreationSequence_as_a_CLR_property()
    {
        typeof(Customer)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Should().NotContain(p => p.Name == "CreationSequence",
                "CreationSequence must exist only as an EF shadow property, never as a Customer CLR member");
    }

    [Fact]
    public void No_request_or_response_DTO_in_the_API_assembly_exposes_CreationSequence()
    {
        var apiAssembly = typeof(Verce.Api.Customers.CustomerResponse).Assembly;
        var violations = apiAssembly.GetTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith("Verce.Api", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.Name.Contains("CreationSequence", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.DeclaringType!.FullName}.{p.Name}")
            .ToList();

        violations.Should().BeEmpty("no request or response contract may carry the internal Customer pagination tie-breaker");
    }

    [Fact]
    public void No_source_file_computes_creation_sequence_via_MAX_plus_one()
    {
        // ADR-0011 §1.2.1 explicitly prohibits MAX(...)+1 allocation — this is a call-site
        // pattern, not type metadata, so a source scan is the legitimate technique here
        // (mirrors SourceLevelRuleTests' own justification for the equivalent Id-ordering rule).
        var pattern = new Regex(@"MAX\s*\(\s*.*creation_sequence.*\)\s*\+\s*1", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in RepoPaths.AllSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                    violations.Add($"{Path.GetRelativePath(RepoPaths.RepoRoot, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        violations.Should().BeEmpty("CreationSequence must only ever be allocated by the PostgreSQL sequence default, never MAX(...)+1 in application code");
    }
}
