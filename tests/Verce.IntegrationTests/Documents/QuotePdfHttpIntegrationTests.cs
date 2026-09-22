using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Documents;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Documents;

/// <summary>
/// S7 §45: Quote PDF V1 against the REAL host, REAL PostgreSQL and REAL headless Chromium (no
/// mock renderer) — the specific revision rendered, the historical snapshot used, customer-facing
/// fields present, internal cost/margin fields absent, idempotent materialization, a real
/// concurrent race, auth/CSRF, Content-Type and filename.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuotePdfHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    // mission §92/§134: a unique, disposable Documents storage root per test run — never the
    // AppContext.BaseDirectory fallback, which is a machine-wide, shared, never-cleaned path.
    private readonly string _documentsStorageRoot = Path.Combine(Path.GetTempPath(), "verce-test-documents-" + Guid.NewGuid().ToString("N"));

    public QuotePdfHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE documents.generated_document, quoting.quote_status_history, quoting.quote_item,
                           quoting.quote_revision, quoting.quote, quoting.quote_number_counter,
                           production.production_order, pricing.fee_rule_version, pricing.fee_rule,
                           pricing.sales_channel, settings.app_setting, settings.company_profile,
                           platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
            ["Documents:StorageRoot"] = _documentsStorageRoot,
        });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        // Cleaned even on failure — no leftover PDF/HTML on this machine (mission §93/§135).
        if (Directory.Exists(_documentsStorageRoot)) Directory.Delete(_documentsStorageRoot, recursive: true);
    }

    private async Task<(QuoteResponse Quote, AuthTestClient Owner)> CreateApprovedQuoteAsync()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");
        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, null, "Vaso decorativo", 10.00m, 2m, 0.35m, null, QuoteDiscountKind.None, 0m)], null)));
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        return (approved, owner);
    }

    private static string PdfPath(Guid quoteId, Guid revisionId) => $"/api/quotes/{quoteId}/revisions/{revisionId}/pdf";

    [Fact]
    public async Task Generating_a_PDF_renders_the_specific_revisions_snapshot_with_customer_facing_fields_and_no_internal_cost_data()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        var generateResponse = await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        generateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await ReadAsync<QuotePdfMetadataResponse>(generateResponse);
        metadata.QuoteRevisionId.Should().Be(revisionId);
        metadata.DocumentTypeCode.Should().Be("QUOTE");
        metadata.PdfSizeBytes.Should().BeGreaterThan(0);

        await using var db = _fixture.CreateContext();
        var document = await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.SourceId == revisionId);
        document.RenderDataSnapshotJson.Should().Contain("Vaso decorativo");
        document.RenderDataSnapshotJson.Should().NotContain("EstimatedUnitCost", "the render snapshot itself must never carry cost fields");
        document.PdfSha256.Should().HaveLength(64);
        document.HtmlSha256.Should().HaveLength(64);
        document.RenderEngineVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Generation_succeeds_when_multiple_brand_assets_share_the_same_type_code()
    {
        // Regression: which asset is "the" document logo is decided by BrandingAssignment (a
        // role -> asset pointer, unique per role), never by an asset's own BrandAssetTypeCode —
        // that code is just a category, and nothing stops two differently-named assets (e.g. two
        // logos a user uploaded over time) from sharing it. ResolveLogoAsync used to filter
        // BrandAsset directly by (TypeCode, IsActive) with SingleOrDefaultAsync, which threw
        // "Sequence contains more than one element" the moment a second same-typed asset existed
        // — exactly what a real shop naturally accumulates through ordinary Settings/Brand usage.
        await using (var db = _fixture.CreateContext())
        {
            db.Add(new Verce.Modules.Settings.BrandAsset("PRIMARY_LOGO", "Logo antigo"));
            db.Add(new Verce.Modules.Settings.BrandAsset("PRIMARY_LOGO", "Logo novo"));
            await db.SaveChangesAsync();
        }

        var (quote, owner) = await CreateApprovedQuoteAsync();
        var generateResponse = await owner.PostAsync<object?>(PdfPath(quote.Id, quote.CurrentRevision.Id), null);
        generateResponse.StatusCode.Should().Be(HttpStatusCode.OK, "an ambiguous BrandAssetTypeCode must never fail PDF generation");
    }

    [Fact]
    public async Task Downloading_the_generated_PDF_returns_valid_pdf_bytes_with_correct_content_type_and_a_safe_filename()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var downloadResponse = await owner.GetAsync(PdfPath(quote.Id, revisionId));
        downloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        downloadResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");

        var disposition = downloadResponse.Content.Headers.ContentDisposition;
        disposition.Should().NotBeNull();
        disposition!.FileNameStar.Should().NotContain("/").And.NotContain("\\");
        disposition.FileNameStar.Should().Contain(quote.Number);
    }

    [Fact]
    public async Task Downloading_before_any_generation_returns_404()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var response = await owner.GetAsync(PdfPath(quote.Id, quote.CurrentRevision.Id));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Repeating_generation_for_the_same_revision_is_idempotent_and_returns_the_same_artifact()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));
        var second = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));

        second.Id.Should().Be(first.Id);
        second.PdfSha256.Should().Be(first.PdfSha256);

        await using var db = _fixture.CreateContext();
        (await db.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(1);
    }

    [Fact]
    public async Task Two_concurrent_first_generation_requests_converge_to_exactly_one_artifact_with_no_unique_violation_leakage()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        var task1 = owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        var task2 = owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        var responses = await Task.WhenAll(task1, task2);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK, "no unique-violation/500 may ever leak to either caller");
        var metadataResults = await Task.WhenAll(responses.Select(ReadAsync<QuotePdfMetadataResponse>));
        metadataResults[0].Id.Should().Be(metadataResults[1].Id, "exactly one immutable artifact identity, regardless of which request won the race");

        await using var db = _fixture.CreateContext();
        (await db.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(1);
    }

    [Fact]
    public async Task Generation_requires_authentication_manage_permission_and_CSRF()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.PostAsync(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var withoutCsrf = await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null, withAntiforgery: false);
        withoutCsrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);
        var viewerAttempt = await viewer.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        viewerAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden, "PDF materialization is a manage-level mutation (mission §40/§59)");

        // A Viewer CAN still download an already-materialized artifact (read permission).
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync(PdfPath(quote.Id, revisionId))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_renderer_failure_persists_no_artifact_and_leaves_a_retry_possible()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        // Mission §43: a failing renderer must never leave a corrupt/zero-byte "completed" row —
        // and a subsequent retry (with a healthy renderer) must still succeed normally.
        await using (var brokenFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHtmlToPdfRenderer>();
            services.AddSingleton<IHtmlToPdfRenderer, AlwaysFailingRenderer>();
        })))
        {
            // A fresh Owner login against the BROKEN factory — Quote visibility is not
            // user-scoped, so this Owner can reach the SAME quote created above.
            var brokenOwnerEmail = Guid.NewGuid().ToString("N") + "@example.test";
            const string brokenOwnerPassword = "a-perfectly-fine-12char-password";
            using (var scope = brokenFactory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = new ApplicationUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = brokenOwnerEmail,
                    Email = brokenOwnerEmail,
                    DisplayName = "Owner Broken",
                    IsActive = true,
                    SetupStatus = SetupStatus.Active,
                    SetupCompletedAt = DateTimeOffset.UtcNow,
                };
                (await users.CreateAsync(user, brokenOwnerPassword)).Succeeded.Should().BeTrue();
                (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
            }
            var brokenOwner = new AuthTestClient(brokenFactory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }));
            await brokenOwner.EnsureCsrfCookieAsync();
            (await brokenOwner.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(brokenOwnerEmail, brokenOwnerPassword))).StatusCode
                .Should().Be(HttpStatusCode.NoContent);
            await brokenOwner.EnsureCsrfCookieAsync();

            var failedAttempt = await brokenOwner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
            failedAttempt.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await ProblemCodeAsync(failedAttempt)).Should().StartWith("PDF_RENDER_");
        }

        await using (var db = _fixture.CreateContext())
        {
            (await db.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(0,
                "a failed render must leave NO row — never a corrupt/incomplete artifact");
        }

        // Retry against the REAL renderer succeeds normally — nothing was left behind to block it.
        var retry = await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class AlwaysFailingRenderer : IHtmlToPdfRenderer
    {
        public Task<PdfRenderResult> RenderAsync(string html, PdfRenderOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PDF_RENDER_SIMULATED_FAILURE");
    }

    [Fact]
    public async Task A_revision_id_that_does_not_belong_to_the_quote_is_rejected_as_not_found()
    {
        var (quoteA, owner) = await CreateApprovedQuoteAsync();
        var (quoteB, _) = await CreateApprovedQuoteAsync();

        var response = await owner.PostAsync<object?>(PdfPath(quoteA.Id, quoteB.CurrentRevision.Id), null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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
            DisplayName = role + " PDF",
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
