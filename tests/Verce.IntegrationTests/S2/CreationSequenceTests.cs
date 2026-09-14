using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Verce.Modules.Customers;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.S2;

/// <summary>
/// S2 Wave 1 gate correction (VERCE3D-M0-S2-WAVE1-CREATION-SEQUENCE-IMPLEMENTATION-001):
/// <c>CreationSequence</c> is an internal, database-allocated, immutable EF shadow property
/// used only to make Customer pagination a total order (ADR-0011 §1.2.1). These tests prove
/// it is real persistence metadata — never a public CLR property, never reused after soft
/// delete, never exposed through the API/OpenAPI surface — and that it resolves ties that
/// <c>name, created_at</c> alone cannot.
/// </summary>
public sealed partial class S2HttpIntegrationTests
{
    [Fact]
    public void CreationSequence_is_internal_shadow_metadata_with_database_generated_allocation()
    {
        using var db = _fixture.CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Customer))!;
        var property = entityType.FindProperty("CreationSequence");

        property.Should().NotBeNull("the mission requires an EF shadow property named CreationSequence");
        property!.IsShadowProperty().Should().BeTrue();
        property.ClrType.Should().Be(typeof(long));
        property.GetColumnName().Should().Be("creation_sequence");
        property.IsNullable.Should().BeFalse();
        property.ValueGenerated.Should().Be(ValueGenerated.OnAdd);
        property.GetDefaultValueSql().Should().Be("nextval('customers.customer_creation_sequence_seq')");
        property.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Throw);

        var index = entityType.GetIndexes().SingleOrDefault(i => i.Properties.Count == 1 && i.Properties[0] == property);
        index.Should().NotBeNull("the database must independently enforce uniqueness");
        index!.IsUnique.Should().BeTrue();

        typeof(Customer).GetProperty("CreationSequence", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().BeNull("CreationSequence must never be a public (or even private) CLR property on Customer — shadow only");
    }

    [Fact]
    public async Task After_save_modification_of_the_shadow_sequence_is_rejected_not_silently_applied()
    {
        await using var db = _fixture.CreateContext();
        var customer = new Customer(PersonType.Individual, "Sequência protegida", null, null, null, null, null);
        db.Add(customer);
        await db.SaveChangesAsync();

        db.Entry(customer).Property("CreationSequence").CurrentValue = 999_999_999L;
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "EF's AfterSaveBehavior.Throw must prevent application code from mutating an allocated sequence value");
    }

    [Fact]
    public async Task Concurrent_customer_creation_allocates_distinct_positive_sequences_without_application_serialization()
    {
        await using var dbA = _fixture.CreateContext();
        await using var dbB = _fixture.CreateContext();
        var customerA = new Customer(PersonType.Individual, "Concorrente A", null, null, null, null, null);
        var customerB = new Customer(PersonType.Individual, "Concorrente B", null, null, null, null, null);
        dbA.Add(customerA);
        dbB.Add(customerB);

        await Task.WhenAll(dbA.SaveChangesAsync(), dbB.SaveChangesAsync());

        var sequenceA = (long)dbA.Entry(customerA).Property("CreationSequence").CurrentValue!;
        var sequenceB = (long)dbB.Entry(customerB).Property("CreationSequence").CurrentValue!;

        sequenceA.Should().BePositive();
        sequenceB.Should().BePositive();
        sequenceA.Should().NotBe(sequenceB, "PostgreSQL must allocate distinct values for concurrent inserts with no application-side locking");
    }

    [Fact]
    public async Task Soft_deleted_customer_sequence_is_never_reused_by_the_next_customer()
    {
        await using var db = _fixture.CreateContext();
        var first = new Customer(PersonType.Individual, "Exclusão suave A", null, null, null, null, null);
        db.Add(first);
        await db.SaveChangesAsync();
        var sequenceOfDeleted = (long)db.Entry(first).Property("CreationSequence").CurrentValue!;

        first.SoftDelete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var second = new Customer(PersonType.Individual, "Exclusão suave B", null, null, null, null, null);
        db.Add(second);
        await db.SaveChangesAsync();
        var sequenceOfNewCustomer = (long)db.Entry(second).Property("CreationSequence").CurrentValue!;

        sequenceOfNewCustomer.Should().NotBe(sequenceOfDeleted);
        sequenceOfNewCustomer.Should().BeGreaterThan(sequenceOfDeleted, "normal sequence progression — no reseeding after soft delete");
    }

    [Fact]
    public async Task Duplicate_name_and_created_at_are_resolved_deterministically_across_paged_iterations()
    {
        const string sharedName = "Mesmo Nome Duplicado Empate";
        var fixedCreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ids = new List<Guid>();

        await using (var db = _fixture.CreateContext())
        {
            for (var i = 0; i < 8; i++)
            {
                var customer = new Customer(PersonType.Individual, sharedName, null, null, null, null, null);
                db.Add(customer);
                await db.SaveChangesAsync();
                ids.Add(customer.Id);
            }

            // Force the exact tie Name+CreatedAt cannot break on its own (ADR-0011 §1.2.1
            // revision history) — CreationSequence must be the one column left to differ.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE customers.customer SET created_at = {fixedCreatedAt} WHERE id = ANY({ids.ToArray()})");
        }

        async Task<List<(Guid Id, long Sequence)>> FetchOrderedAsync()
        {
            await using var db = _fixture.CreateContext();
            var rows = await db.Set<Customer>().AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .OrderBy(x => x.Name)
                .ThenBy(x => EF.Property<DateTimeOffset>(x, "CreatedAt"))
                .ThenBy(x => EF.Property<long>(x, "CreationSequence"))
                .Select(x => new { x.Id, Sequence = EF.Property<long>(x, "CreationSequence") })
                .ToListAsync();
            return rows.Select(x => (x.Id, x.Sequence)).ToList();
        }

        var iteration1 = await FetchOrderedAsync();
        var iteration2 = await FetchOrderedAsync();
        var iteration3 = await FetchOrderedAsync();

        iteration1.Should().Equal(iteration2, "repeated execution must produce identical ordering");
        iteration2.Should().Equal(iteration3);
        iteration1.Select(x => x.Sequence).Should().BeInAscendingOrder("CreationSequence must strictly order the tied rows");
        iteration1.Select(x => x.Sequence).Should().OnlyHaveUniqueItems("the final tie-break must be a strict total order");
        iteration1.Select(x => x.Id).Should().BeEquivalentTo(ids, options => options.WithoutStrictOrdering());

        const int pageSize = 3;
        var collectedAcrossPages = new List<Guid>();
        for (var page = 0; page * pageSize < ids.Count; page++)
        {
            await using var db = _fixture.CreateContext();
            var rows = await db.Set<Customer>().AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .OrderBy(x => x.Name)
                .ThenBy(x => EF.Property<DateTimeOffset>(x, "CreatedAt"))
                .ThenBy(x => EF.Property<long>(x, "CreationSequence"))
                .Skip(page * pageSize).Take(pageSize)
                .Select(x => x.Id)
                .ToListAsync();
            collectedAcrossPages.AddRange(rows);
        }

        collectedAcrossPages.Should().OnlyHaveUniqueItems("no customer may appear on two pages");
        collectedAcrossPages.Should().BeEquivalentTo(ids, options => options.WithoutStrictOrdering(), "every customer must appear exactly once across all pages");
    }

    [Fact]
    public async Task Customer_list_and_detail_responses_never_expose_the_internal_sequence()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await Json(await owner.PostAsync("/api/customers", Customer("Sem exposição de sequência", "52998224725")));
        var id = created.RootElement.GetProperty("id").GetGuid();

        var listRaw = await (await owner.GetAsync("/api/customers?page=1&pageSize=10")).Content.ReadAsStringAsync();
        var detailRaw = await (await owner.GetAsync($"/api/customers/{id}")).Content.ReadAsStringAsync();

        foreach (var payload in new[] { listRaw, detailRaw })
        {
            payload.Should().NotContainEquivalentOf("creationSequence");
            payload.Should().NotContainEquivalentOf("creation_sequence");
        }
    }

    [Fact]
    public async Task OpenApi_document_never_names_the_internal_sequence_in_Customer_schemas()
    {
        var (client, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await response.Content.ReadAsStringAsync();

        document.Should().NotContainEquivalentOf("creationSequence");
        document.Should().NotContainEquivalentOf("creation_sequence");
    }
}
