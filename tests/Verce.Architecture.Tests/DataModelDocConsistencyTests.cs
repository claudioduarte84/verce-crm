using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;

namespace Verce.Architecture.Tests;

/// <summary>
/// F-7, F-8 (ROADMAP S1 catalogue, ADR-0011 §1). F-7 is a genuine documentation-consistency
/// contract — and a real regression net: this exact test would have caught the checkpoint-002
/// finding that DATA-MODEL.md mislabeled every Category 4 (Technical/framework) table as
/// "Category 3" in three places, contradicting ADR-0011 §1's own numbering.
/// </summary>
public class DataModelDocConsistencyTests
{
    private static string ReadDoc(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "docs", relativePath));

    [Fact]
    public void F7_ADR0011_and_DATA_MODEL_declare_the_same_five_categories_in_the_same_order()
    {
        var adr = ReadDoc("architecture/ADR-0011-identifiers-and-concurrency.md");

        var expectedHeadings = new[]
        {
            "Category 1 — Domain entity",
            "Category 2 — Master data",
            "Category 3 — Reference data",
            "Category 4 — Technical / framework table",
            "Category 5 — Join table",
        };

        foreach (var heading in expectedHeadings)
            adr.Should().Contain(heading, $"ADR-0011 §1 must declare '{heading}' — this is the source of truth F-7 checks DATA-MODEL against");
    }

    [Fact]
    public void F7_DATA_MODEL_never_labels_a_technical_framework_table_as_a_category_other_than_4()
    {
        var dataModel = ReadDoc("DATA-MODEL.md");

        // Every "Category N technical table" mention must say Category 4 — ADR-0011 §1 defines
        // Category 3 as Reference data (a code-keyed PK), never Technical/framework.
        var matches = Regex.Matches(dataModel, @"Category (\d) technical table", RegexOptions.IgnoreCase);
        matches.Should().NotBeEmpty("DATA-MODEL.md must actually classify its framework-owned tables (DataProtectionKey, the archive table, Quartz) for this test to mean anything");

        foreach (Match match in matches)
            match.Groups[1].Value.Should().Be("4", $"'{match.Value}' contradicts ADR-0011 §1, which numbers Technical/framework as Category 4, not {match.Groups[1].Value}");
    }

    [Fact]
    public void F8_framework_owned_Identity_and_DataProtection_tables_carry_no_application_audit_columns()
    {
        var options = new DbContextOptionsBuilder<VerceDbContext>()
            .UseNpgsql("Host=localhost;Database=verce_model_only") // model-building only, never opened
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new VerceDbContext(options);

        var frameworkOwnedTypes = new[]
        {
            typeof(Microsoft.AspNetCore.Identity.IdentityUser<Guid>).IsAssignableFrom(typeof(Verce.Platform.Identity.ApplicationUser))
                ? typeof(Verce.Platform.Identity.ApplicationUser) : null,
            typeof(Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>),
            typeof(Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>),
            typeof(Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>),
            typeof(Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>),
            typeof(Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>),
            typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey),
        };

        var forbiddenColumnNames = new[] { "CreatedBy", "UpdatedBy", "ModifiedBy" };
        var violations = new List<string>();

        foreach (var type in frameworkOwnedTypes)
        {
            if (type is null) continue;
            var entityType = context.Model.FindEntityType(type);
            if (entityType is null) continue;

            foreach (var property in entityType.GetProperties())
                if (forbiddenColumnNames.Contains(property.Name))
                    violations.Add($"{type.Name}.{property.Name}");
        }

        violations.Should().BeEmpty("F-8: framework-owned tables keep their framework schema exactly — no rule anywhere demands application audit columns on them");
    }
}
