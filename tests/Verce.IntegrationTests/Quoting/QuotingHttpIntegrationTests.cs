using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Quoting;

/// <summary>
/// S6 (ADR-0020) end-to-end proof against the REAL host and REAL PostgreSQL: quote creation with
/// PER_ORDER allocation, the full revision/approval lifecycle, and the ADR-0020 §A.2
/// supersession × production-order matrix — the piece that only a real transactional handler
/// chain (QuoteApproved -&gt; Production, in one Unit of Work) can actually prove.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuotingHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public QuotingHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE quoting.quote_status_history, quoting.quote_item, quoting.quote_revision, quoting.quote,
                           quoting.quote_number_counter, production.production_order,
                           pricing.fee_rule_version, pricing.fee_rule, pricing.sales_channel,
                           settings.app_setting, platform.account_setup_token, platform.user_role,
                           platform.user_claim, platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
        });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Create_with_a_PER_ORDER_channel_allocates_the_whole_fee_exactly_once_across_lines()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await CreatePerOrderChannelAsync(owner, "MKT-A", 10.00m, 0.10m);

        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, channel.Id,
        [
            new QuoteItemRequest(null, null, "Linha 1", 100.00m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
            new QuoteItemRequest(null, null, "Linha 2", 50.00m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
            new QuoteItemRequest(null, null, "Linha 3", 25.00m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
        ], null)));

        // Exactly vector D from ADR-0020 §C.8: 10.00 over 100/50/25 -> 5.71 / 2.86 / 1.43.
        var items = quote.CurrentRevision.Items.OrderBy(i => i.LineNumber).ToList();
        items[0].AllocatedOrderFee.Should().Be(5.71m);
        items[1].AllocatedOrderFee.Should().Be(2.86m);
        items[2].AllocatedOrderFee.Should().Be(1.43m);
        items.Sum(i => i.AllocatedOrderFee).Should().Be(10.00m);
        quote.Number.Should().MatchRegex(@"^\d{6}-1$");
    }

    [Fact]
    public async Task Approving_a_quote_creates_exactly_one_QUEUED_production_order_and_replaying_approve_never_duplicates_it()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);

        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve",
            new QuoteVersionedRequest(quote.Version)));
        approved.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.APPROVED);
        approved.HasEverWon.Should().BeTrue();
        approved.CommercialOutcome.Should().Be("WON");

        await using var db = _fixture.CreateContext();
        var orders = await db.Set<ProductionOrder>().Where(o => o.QuoteId == quote.Id).ToListAsync();
        orders.Should().ContainSingle();
        orders[0].Status.Should().Be(ProductionOrderStatus.QUEUED);
        orders[0].QuoteRevisionId.Should().Be(approved.CurrentRevision.Id);

        // A second approval attempt is REJECTED (the revision is already decided) — it must
        // never reach Production a second time and must never create a duplicate order.
        var replay = await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(approved.Version));
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict, "STATE-MACHINES §6: QUOTE_REVISION_ALREADY_DECIDED is a state guard, mapped to 409");
        (await ProblemCodeAsync(replay)).Should().Be("QUOTE_REVISION_ALREADY_DECIDED");

        var ordersAfterReplay = await db.Set<ProductionOrder>().Where(o => o.QuoteId == quote.Id).ToListAsync();
        ordersAfterReplay.Should().ContainSingle();
    }

    [Fact]
    public async Task Approving_a_new_revision_cancels_the_previous_QUEUED_order_as_superseded_and_links_the_replacement()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);
        var r1Approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));

        // ADR-0020 §A.2: creating R2 is never blocked by R1's (QUEUED) order.
        var revised = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/revise",
            new QuoteReviseRequest(null, r1Approved.CurrentRevision.SalesChannelId,
                [new QuoteItemRequest(null, null, "Linha única", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null, r1Approved.Version)));
        revised.CurrentRevision.RevisionIndex.Should().Be(2);

        var r2Approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(revised.Version)));

        await using var db = _fixture.CreateContext();
        var orders = await db.Set<ProductionOrder>().Where(o => o.QuoteId == quote.Id).OrderBy(o => o.NumberSequence).ToListAsync();
        orders.Should().HaveCount(2);
        var r1Order = orders.Single(o => o.QuoteRevisionId == r1Approved.CurrentRevision.Id);
        var r2Order = orders.Single(o => o.QuoteRevisionId == r2Approved.CurrentRevision.Id);

        r1Order.Status.Should().Be(ProductionOrderStatus.CANCELED);
        r1Order.CancellationReason.Should().Be("SUPERSEDED_BY_REVISION");
        r1Order.SupersededByOrderId.Should().Be(r2Order.Id);
        r2Order.Status.Should().Be(ProductionOrderStatus.QUEUED);
    }

    [Fact]
    public async Task An_in_flight_previous_order_blocks_approval_of_a_newer_revision_but_never_blocks_creating_it()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);
        var r1Approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));

        // S9 hasn't shipped the operator-driven QUEUED -> IN_PRODUCTION transition yet — flip it
        // directly to simulate "the shop floor already started" for this ADR-0020 §A.3 guard test.
        await using (var db = _fixture.CreateContext())
        {
            var order = await db.Set<ProductionOrder>().SingleAsync(o => o.QuoteRevisionId == r1Approved.CurrentRevision.Id);
            typeof(ProductionOrder).GetProperty(nameof(ProductionOrder.Status))!.SetValue(order, ProductionOrderStatus.IN_PRODUCTION);
            await db.SaveChangesAsync();
        }

        // Creating R2 must still succeed — BLOCKING-01: never gated on shop-floor state.
        var revised = await owner.PostAsync($"/api/quotes/{quote.Id}/revise",
            new QuoteReviseRequest(null, r1Approved.CurrentRevision.SalesChannelId,
                [new QuoteItemRequest(null, null, "Linha única", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null, r1Approved.Version));
        revised.StatusCode.Should().Be(HttpStatusCode.OK);
        var revisedQuote = await ReadAsync<QuoteResponse>(revised);

        // But APPROVING it is rejected while the previous order is in flight.
        var approveAttempt = await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(revisedQuote.Version));
        approveAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(approveAttempt)).Should().Be("PRODUCTION_ORDER_IN_PROGRESS");
    }

    [Theory]
    [InlineData(ProductionOrderStatus.DELIVERED)]
    [InlineData(ProductionOrderStatus.CANCELED)]
    public async Task Approving_a_new_revision_over_a_terminal_previous_order_succeeds_and_leaves_it_untouched(ProductionOrderStatus terminalStatus)
    {
        // M-01/ADR-0020 §A.2 rule 2: DELIVERED and CANCELED are TERMINAL — unlike QUEUED (canceled
        // as superseded) or IN_PRODUCTION/READY/SHIPPED (blocks approval), a terminal previous
        // order is simply left alone: approval proceeds and a new order is created exactly once.
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);
        var r1Approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));

        await using (var db = _fixture.CreateContext())
        {
            var order = await db.Set<ProductionOrder>().SingleAsync(o => o.QuoteRevisionId == r1Approved.CurrentRevision.Id);
            typeof(ProductionOrder).GetProperty(nameof(ProductionOrder.Status))!.SetValue(order, terminalStatus);
            await db.SaveChangesAsync();
        }

        var revised = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/revise",
            new QuoteReviseRequest(null, r1Approved.CurrentRevision.SalesChannelId,
                [new QuoteItemRequest(null, null, "Linha única", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null, r1Approved.Version)));

        var approveResponse = await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(revised.Version));
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"a {terminalStatus} previous order must never block approval of a newer revision");
        var r2Approved = await ReadAsync<QuoteResponse>(approveResponse);

        await using var verify = _fixture.CreateContext();
        var orders = await verify.Set<ProductionOrder>().Where(o => o.QuoteId == quote.Id).OrderBy(o => o.NumberSequence).ToListAsync();
        orders.Should().HaveCount(2, "exactly one new order is created for R2 — the terminal R1 order is left untouched, never re-created or duplicated");
        var r1Order = orders.Single(o => o.QuoteRevisionId == r1Approved.CurrentRevision.Id);
        var r2Order = orders.Single(o => o.QuoteRevisionId == r2Approved.CurrentRevision.Id);

        r1Order.Status.Should().Be(terminalStatus, "a terminal order is never mutated by a later approval");
        r1Order.SupersededByOrderId.Should().BeNull("only a QUEUED order gets superseded — a terminal one keeps its own terminal history");
        r2Order.Status.Should().Be(ProductionOrderStatus.QUEUED);
    }

    [Fact]
    public async Task Getting_a_quote_by_id_reports_the_current_revisions_production_order_status()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);

        var beforeApproval = await ReadAsync<QuoteResponse>(await owner.GetAsync($"/api/quotes/{quote.Id}"));
        beforeApproval.ProductionOrderStatus.Should().BeNull("no ProductionOrder exists until the quote is approved");

        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));

        var afterApproval = await ReadAsync<QuoteResponse>(await owner.GetAsync($"/api/quotes/{quote.Id}"));
        afterApproval.ProductionOrderStatus.Should().Be(ProductionOrderStatus.QUEUED.ToString());
        approved.ProductionOrderStatus.Should().BeNull("the approve response itself never looks up Production — only GET by id does");
    }

    [Fact]
    public async Task Revisions_endpoint_returns_every_revision_oldest_first_with_full_detail_and_marks_supersession()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);
        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/revise",
            new QuoteReviseRequest(null, r1.CurrentRevision.SalesChannelId,
                [new QuoteItemRequest(null, null, "Linha única", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null, r1.Version)));

        var revisions = await ReadAsync<List<QuoteRevisionResponse>>(await owner.GetAsync($"/api/quotes/{quote.Id}/revisions"));

        revisions.Should().HaveCount(2);
        revisions[0].Id.Should().Be(r1.CurrentRevision.Id);
        revisions[0].RevisionIndex.Should().Be(1);
        revisions[0].Status.Should().Be(QuoteRevisionStatus.APPROVED, "an APPROVED revision is terminal — revising afterwards links it to its successor without changing its own status");
        revisions[0].SupersededByRevisionId.Should().Be(r2.CurrentRevision.Id);
        revisions[0].Items.Should().NotBeEmpty("historical revisions remain fully inspectable, never just a status stub");

        revisions[1].Id.Should().Be(r2.CurrentRevision.Id);
        revisions[1].RevisionIndex.Should().Be(2);
        revisions[1].SourceRevisionId.Should().Be(r1.CurrentRevision.Id);
        revisions[1].Status.Should().Be(QuoteRevisionStatus.GENERATED);
    }

    [Fact]
    public async Task Cancel_requires_a_reason_and_transitions_the_current_revision()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);

        var missingReason = await owner.PostAsync($"/api/quotes/{quote.Id}/cancel", new QuoteCancelRequest("", quote.Version));
        missingReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(missingReason)).Should().Be("QUOTE_CANCEL_REASON_REQUIRED");

        var canceled = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/cancel",
            new QuoteCancelRequest("cliente desistiu", quote.Version)));
        canceled.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.CANCELED);
        canceled.CommercialOutcome.Should().Be("LOST");
        canceled.HasEverWon.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_version_mismatch_is_rejected_as_a_conflict()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var quote = await CreateSimpleAdHocQuoteAsync(owner);
        var staleVersion = quote.Version + 999;
        var response = await owner.PostAsync($"/api/quotes/{quote.Id}/send", new QuoteVersionedRequest(staleVersion));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_and_CSRF_is_enforced_on_writes()
    {
        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/quotes")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await CreatePerOrderChannelAsync(owner, "MKT-CSRF", 5.00m, 0m);
        var withoutCsrf = await owner.PostAsync("/api/quotes",
            new QuoteCreateRequest(null, channel.Id, [new QuoteItemRequest(null, null, "X", 10m, 1m, 0.3m, null, QuoteDiscountKind.None, 0m)], null),
            withAntiforgery: false);
        withoutCsrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<SalesChannelResponse> CreatePerOrderChannelAsync(AuthTestClient owner, string code, decimal fixedFee, decimal commissionPercent)
    {
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest(code, code, SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest($"Regra {code}"));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions",
            new FeeRuleVersionCreateRequest(new DateOnly(2020, 1, 1), null, commissionPercent, fixedFee, FixedFeeApplication.PerOrder, null, null, null, false));
        return channel;
    }

    private async Task<QuoteResponse> CreateSimpleAdHocQuoteAsync(AuthTestClient owner)
    {
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");
        return await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, null, "Item avulso", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null)));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<(AuthTestClient Client, Guid UserId)> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = role + " S6",
            IsActive = true,
            SetupStatus = SetupStatus.Active,
            SetupCompletedAt = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return (client, user.Id);
    }
}
