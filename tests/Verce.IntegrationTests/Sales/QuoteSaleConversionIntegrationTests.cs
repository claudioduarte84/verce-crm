using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.Api.Sales;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Sales;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Sales;

[Collection(PostgresCollection.Name)]
public sealed class QuoteSaleConversionIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    public QuoteSaleConversionIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE sales.sale, quoting.quote_status_history, quoting.quote_item, quoting.quote_revision, quoting.quote,
                           quoting.quote_number_counter, production.production_order, pricing.price_bracket, pricing.fee_rule_version,
                           pricing.fee_rule, pricing.sales_channel, settings.app_setting, platform.account_setup_token,
                           platform.user_role, platform.user_claim, platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true" });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Approval_creates_production_but_no_sale_and_explicit_conversion_replays_then_allows_reconversion_after_cancel()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var quote = await CreateQuoteAsync(owner, "Conversão S8A");
        var generatedAttempt = await owner.PostAsync($"/api/sales/from-quote/{quote.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid()));
        generatedAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(generatedAttempt)).Should().Be("QUOTE_REVISION_NOT_SALE_ELIGIBLE");

        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        await using (var db = _fixture.CreateContext())
        {
            (await db.Set<Sale>().CountAsync(x => x.QuoteRevisionId == approved.CurrentRevision.Id)).Should().Be(0);
        }

        var requestId = Guid.NewGuid();
        var first = await ReadAsync<SaleResponse>(await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(requestId)));
        first.Source.Should().Be(SaleSource.QUOTE_CONVERSION);
        first.Items.Should().ContainSingle();
        first.Items[0].LineTotalAmount.Should().Be(approved.CurrentRevision.Items.Single().LineTotalAmount);
        first.Items[0].UnitCostAmount.Should().Be(approved.CurrentRevision.Items.Single().UnitTotalCost);
        first.Items[0].ChannelFeeAmount.Should().Be(approved.CurrentRevision.Items.Single().LineFeeAmount);
        var replay = await ReadAsync<SaleResponse>(await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(requestId)));
        replay.Id.Should().Be(first.Id);

        var secondRequest = await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid()));
        secondRequest.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(secondRequest)).Should().Be("SALE_ALREADY_EXISTS_FOR_REVISION");

        (await owner.PostAsync($"/api/sales/{first.Id}/cancel", new CancelSaleRequest("Cliente desistiu", first.Version))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var reconverted = await ReadAsync<SaleResponse>(await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid())));
        reconverted.Id.Should().NotBe(first.Id);
        reconverted.Status.Should().Be(SaleStatus.CONFIRMED);
    }

    [Fact]
    public async Task Viewer_can_read_sales_but_cannot_convert_or_cancel()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var quote = await CreateQuoteAsync(owner, "Permissões S8A");
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var sale = await ReadAsync<SaleResponse>(await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid())));
        var viewer = await LoggedInAsync(Roles.Viewer);
        (await viewer.GetAsync("/api/sales")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/sales/{sale.Id}/cancel", new CancelSaleRequest("Não pode", sale.Version))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Concurrent_conversion_attempts_converge_to_one_live_sale_and_a_stable_conflict()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var quote = await CreateQuoteAsync(owner, "Concorrência S8A");
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var attempts = await Task.WhenAll(
            owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(firstId)),
            owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(secondId)));
        attempts.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(1);
        attempts.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        var conflict = attempts.Single(x => x.StatusCode == HttpStatusCode.Conflict);
        (await ProblemCodeAsync(conflict)).Should().Be("SALE_ALREADY_EXISTS_FOR_REVISION");
        await using var db = _fixture.CreateContext();
        (await db.Set<Sale>().CountAsync(x => x.QuoteRevisionId == approved.CurrentRevision.Id && x.Status != SaleStatus.CANCELED)).Should().Be(1);
    }

    [Fact]
    public async Task Operator_can_manage_sales_and_stale_cancel_version_is_rejected()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var quote = await CreateQuoteAsync(owner, "Operador S8A");
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var actor = await LoggedInAsync(Roles.Operator);
        (await actor.GetAsync("/api/sales")).StatusCode.Should().Be(HttpStatusCode.OK);
        var sale = await ReadAsync<SaleResponse>(await actor.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid())));
        (await actor.PostAsync($"/api/sales/{sale.Id}/cancel", new CancelSaleRequest("Operador", sale.Version))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stale = await actor.PostAsync($"/api/sales/{sale.Id}/cancel", new CancelSaleRequest("Versão antiga", sale.Version));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(stale)).Should().Be("CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task An_approved_revision_remains_convertible_after_a_real_later_revision_supersedes_it()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var quote = await CreateQuoteAsync(owner, "Revisão aprovada histórica");
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var direct = (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"))).Single(x => x.Code == "DIRECT");
        var revised = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/revise", new QuoteReviseRequest(
            null, direct.Id, [new QuoteItemRequest(null, null, "Revisão atual", 10m, 1m, .35m, null, QuoteDiscountKind.None, 0m)], null, approved.Version)));
        revised.CurrentRevision.Id.Should().NotBe(approved.CurrentRevision.Id);
        var revisions = await ReadAsync<IReadOnlyList<QuoteRevisionResponse>>(await owner.GetAsync($"/api/quotes/{quote.Id}/revisions"));
        var historical = revisions.Single(x => x.Id == approved.CurrentRevision.Id);
        historical.Status.Should().Be(QuoteRevisionStatus.APPROVED);
        historical.SupersededByRevisionId.Should().Be(revised.CurrentRevision.Id);

        var response = await owner.PostAsync($"/api/sales/from-quote/{historical.Id}", new ConvertQuoteRequest(Guid.NewGuid()));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sale = await ReadAsync<SaleResponse>(response);
        sale.Source.Should().Be(SaleSource.QUOTE_CONVERSION);
        sale.QuoteRevisionId.Should().Be(historical.Id);
        await using var db = _fixture.CreateContext();
        (await db.Set<Sale>().CountAsync(x => x.QuoteRevisionId == historical.Id && x.Status != SaleStatus.CANCELED)).Should().Be(1);
    }

    private async Task<QuoteResponse> CreateQuoteAsync(AuthTestClient client, string description)
    {
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await client.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");
        return await ReadAsync<QuoteResponse>(await client.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, null, description, 10m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null)));
    }

    private async Task<AuthTestClient> LoggedInAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role, IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
