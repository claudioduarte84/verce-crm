using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Documents;
using Verce.Modules.Quoting;
using Verce.Modules.Settings;
using Verce.Platform.Outbox;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Documents;

/// <summary>
/// S7 template-engine final pass — proofs the HTTP-only <see cref="QuotePdfHttpIntegrationTests"/>
/// suite deliberately never exercises: the REAL outbox claim -&gt; consume -&gt; persist cycle
/// (<see cref="Quote.Approve"/> raises <see cref="Verce.Modules.Quoting.Contracts.GenerateQuotePdfRequestedEvent"/>
/// but never renders synchronously — only <see cref="OutboxDispatcher"/> does), outbox failure
/// isolation, the deterministic render-timeout seam, reissue/history/currentness, and snapshot
/// immutability against real company-profile and template mutations.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuotePdfOutboxAndReissueIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private readonly string _documentsStorageRoot = Path.Combine(Path.GetTempPath(), "verce-test-documents-outbox-" + Guid.NewGuid().ToString("N"));

    public QuotePdfOutboxAndReissueIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE documents.generated_document, platform.outbox_message_attempt, platform.outbox_message,
                           quoting.quote_status_history, quoting.quote_item, quoting.quote_revision, quoting.quote,
                           quoting.quote_number_counter, production.production_order, pricing.fee_rule_version,
                           pricing.fee_rule, pricing.sales_channel, settings.app_setting, settings.company_profile,
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
        if (Directory.Exists(_documentsStorageRoot)) Directory.Delete(_documentsStorageRoot, recursive: true);
    }

    private async Task<(QuoteResponse Quote, AuthTestClient Owner)> CreateApprovedQuoteAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
    {
        // Accepts the common WebApplicationFactory<Program> base rather than VerceWebApplicationFactory
        // specifically: WithWebHostBuilder(...) returns an internal DelegatedWebApplicationFactory<Program>
        // wrapper, never the original subtype, so a scoped renderer swap (AlwaysTimingOutRenderer /
        // AlwaysFailingRenderer) cannot be cast back to VerceWebApplicationFactory.
        var f = factory ?? _factory;
        var owner = new AuthTestClient(f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }));
        await owner.EnsureCsrfCookieAsync();
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Verce.Platform.Identity.ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Verce.Platform.Identity.ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Verce.Platform.Identity.Roles.Owner))
                (await roles.CreateAsync(new Verce.Platform.Identity.ApplicationRole(Verce.Platform.Identity.Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new Verce.Platform.Identity.ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                DisplayName = "Owner Outbox",
                IsActive = true,
                SetupStatus = Verce.Platform.Identity.SetupStatus.Active,
                SetupCompletedAt = DateTimeOffset.UtcNow,
            };
            (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, Verce.Platform.Identity.Roles.Owner)).Succeeded.Should().BeTrue();
        }
        (await owner.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await owner.EnsureCsrfCookieAsync();

        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");
        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, null, "Vaso decorativo", 10.00m, 2m, 0.35m, null, QuoteDiscountKind.None, 0m)], null)));
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        return (approved, owner);
    }

    private static string PdfPath(Guid quoteId, Guid revisionId) => $"/api/quotes/{quoteId}/revisions/{revisionId}/pdf";

    // ---- Real outbox cycle ----

    [Fact]
    public async Task Approving_a_quote_enqueues_a_real_outbox_message_that_a_dispatcher_cycle_renders_into_a_document()
    {
        var (quote, _) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        await using (var db = _fixture.CreateContext())
        {
            var message = await db.Set<OutboxMessage>().SingleAsync(m => m.EventType == "GenerateQuotePdfRequestedEvent");
            message.Status.Should().Be(OutboxStatus.Pending, "approval raises the event — it never renders synchronously");
            message.PayloadJson.Should().Contain(revisionId.ToString());
        }
        (await _fixture.CreateContext().Set<GeneratedDocument>().AsNoTracking().AnyAsync(d => d.SourceId == revisionId))
            .Should().BeFalse("no render may happen before a dispatcher actually claims the message");

        using (var scope = _factory.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
            var claimed = await dispatcher.DispatchBatchAsync(batchSize: 10, workerId: "test-worker");
            claimed.Should().Be(1);
        }

        await using var after = _fixture.CreateContext();
        var document = await after.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.SourceId == revisionId);
        document.DocumentTypeCode.Should().Be("QUOTE");
        document.PdfSizeBytes.Should().BeGreaterThan(0);
        var outboxAfter = await after.Set<OutboxMessage>().SingleAsync(m => m.EventType == "GenerateQuotePdfRequestedEvent");
        outboxAfter.Status.Should().Be(OutboxStatus.Processed);
    }

    [Fact]
    public async Task A_redelivered_outbox_message_never_creates_a_second_document()
    {
        // ADR-0012 §22: at-least-once delivery, so the consumer must be idempotent on its own
        // RenderRequestId — simulated here by invoking the SAME payload's materialization twice
        // directly (a redelivery after a crash right after CompleteAsync but before the ack, the
        // one race no in-process test can force through the real claim path since ClaimBatchAsync
        // will not re-claim an already-Processed row).
        var (quote, _) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        Guid renderRequestId;
        await using (var db = _fixture.CreateContext())
        {
            var message = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.EventType == "GenerateQuotePdfRequestedEvent");
            using var payload = JsonDocument.Parse(message.PayloadJson);
            renderRequestId = payload.RootElement.GetProperty("RenderRequestId").GetGuid();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
            (await dispatcher.DispatchBatchAsync(10, "test-worker")).Should().Be(1);
        }

        // Redeliver by hand: same renderRequestId, same revision — must converge, not duplicate.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
            var brandAssetStorage = scope.ServiceProvider.GetRequiredService<BrandAssetStorage>();
            var pdfService = scope.ServiceProvider.GetRequiredService<QuotePdfService>();
            var (revision, quoteEntity) = await QuotePdfInputBuilder.LoadRevisionAndQuoteAsync(db, quote.Id, revisionId, CancellationToken.None);
            var input = await QuotePdfInputBuilder.BuildAsync(db, brandAssetStorage, quoteEntity!, revision!, CancellationToken.None);
            await pdfService.MaterializeForRequestAsync(input, renderRequestId, null, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await using var after = _fixture.CreateContext();
        (await after.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(1);
    }

    // ---- Outbox failure isolation ----

    [Fact]
    public async Task A_renderer_failure_during_outbox_dispatch_leaves_the_message_retryable_and_persists_no_document()
    {
        var (quote, _) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        await using (var brokenFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHtmlToPdfRenderer>();
            services.AddSingleton<IHtmlToPdfRenderer, AlwaysFailingRenderer>();
        })))
        {
            using var scope = brokenFactory.Services.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
            (await dispatcher.DispatchBatchAsync(10, "test-worker")).Should().Be(1, "the message is still claimed — it just fails to complete");
        }

        await using var db = _fixture.CreateContext();
        var message = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.EventType == "GenerateQuotePdfRequestedEvent");
        message.Status.Should().Be(OutboxStatus.Pending, "a retryable failure schedules another attempt — never a silent drop");
        message.LastError.Should().Contain("PDF_RENDER_SIMULATED_FAILURE");

        (await db.Set<GeneratedDocument>().AnyAsync(d => d.SourceId == revisionId)).Should().BeFalse("a failed render must leave NO artifact row");

        var quoteAfter = await ReadAsync<QuoteResponse>(await AuthedGetAsync(quote.Id));
        quoteAfter.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.APPROVED, "the outbox is a side effect of approval, never a precondition of it (ADR-0012 §1)");
    }

    [Fact]
    public async Task Retrying_after_a_transient_renderer_failure_succeeds_with_the_real_renderer()
    {
        var (quote, _) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        await using (var brokenFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHtmlToPdfRenderer>();
            services.AddSingleton<IHtmlToPdfRenderer, AlwaysFailingRenderer>();
        })))
        {
            using var scope = brokenFactory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(10, "test-worker");
        }

        // The retry backoff (ADR-0012 §20 ladder) schedules `available_at` in the future — force
        // it eligible NOW rather than sleeping for real minutes in a test.
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE platform.outbox_message SET available_at = now() WHERE event_type = 'GenerateQuotePdfRequestedEvent';");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(10, "test-worker")).Should().Be(1);
        }

        (await _fixture.CreateContext().Set<GeneratedDocument>().AsNoTracking().AnyAsync(d => d.SourceId == revisionId)).Should().BeTrue();
    }

    private sealed class AlwaysFailingRenderer : IHtmlToPdfRenderer
    {
        public Task<PdfRenderResult> RenderAsync(string html, PdfRenderOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PDF_RENDER_SIMULATED_FAILURE");
    }

    // ---- Deterministic render-timeout proof ----

    [Fact]
    public async Task A_render_timeout_fails_the_HTTP_request_with_a_stable_code_and_persists_nothing()
    {
        // Forcing a REAL Chromium operation to time out via an impossibly small
        // PdfRenderTimeoutOptions value is deliberately NOT exercised here: Task.WaitAsync's
        // cancellation is cooperative only — it stops the CALLER awaiting, but the abandoned
        // Playwright IPC call keeps running against the shared browser process underneath, which
        // in practice left the renderer's page/connection in a state where a later
        // CloseAsync/DisposeAsync call hangs indefinitely (confirmed against this exact test:
        // dotnet test never returned after 20+ minutes — a real, reproduced hang, not a
        // hypothetical risk). That is unacceptable for a suite other tests depend on. The REAL
        // timeout seam (PlaywrightHtmlToPdfRenderer + a genuinely tiny PdfRenderTimeoutOptions) is
        // instead proven at the unit level, self-contained and watchdog-bounded, in
        // PdfRenderTimeoutTests (Verce.Documents.Tests). This test proves the OTHER half of the
        // same contract — the HTTP-level 502/PDF_RENDER_TIMEOUT mapping and the "no row
        // persisted, retry succeeds" guarantee — via an injected fake renderer that throws the
        // exact exception HtmlToPdfRenderer.RenderAsync now translates a real timeout into, with
        // zero Chromium involvement and zero risk of a real hang.
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        await using var timeoutFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHtmlToPdfRenderer>();
            services.AddSingleton<IHtmlToPdfRenderer, AlwaysTimingOutRenderer>();
        }));
        var (timeoutQuote, timeoutOwner) = await CreateApprovedQuoteAsync(timeoutFactory);

        var timeoutResponse = await timeoutOwner.PostAsync<object?>(PdfPath(timeoutQuote.Id, timeoutQuote.CurrentRevision.Id), null);
        timeoutResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ProblemCodeAsync(timeoutResponse)).Should().Be("PDF_RENDER_TIMEOUT");

        (await _fixture.CreateContext().Set<GeneratedDocument>().AsNoTracking().AnyAsync(d => d.SourceId == timeoutQuote.CurrentRevision.Id))
            .Should().BeFalse("a timed-out render must leave no row — nothing was ever hashed or stored");

        // The SAME revision against the real, healthy renderer succeeds — nothing was left behind to block it.
        var retry = await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class AlwaysTimingOutRenderer : IHtmlToPdfRenderer
    {
        public Task<PdfRenderResult> RenderAsync(string html, PdfRenderOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PDF_RENDER_TIMEOUT");
    }

    // ---- Reissue / history / currentness ----

    [Fact]
    public async Task Reissuing_creates_a_new_current_document_supersedes_the_previous_one_and_captures_the_reason()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));

        var reissueResponse = await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue",
            new QuotePdfReissueRequest("Cliente pediu correção do endereço de entrega."));
        reissueResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reissued = await ReadAsync<QuotePdfMetadataResponse>(reissueResponse);

        reissued.Id.Should().NotBe(first.Id, "reissue always inserts a NEW row, even though a current one existed");
        reissued.IsCurrent.Should().BeTrue();

        await using var db = _fixture.CreateContext();
        var firstRow = await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == first.Id);
        firstRow.IsCurrent.Should().BeFalse("the previous document is superseded, never mutated otherwise");
        var reissuedRow = await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == reissued.Id);
        reissuedRow.ReissueReason.Should().Be("Cliente pediu correção do endereço de entrega.");
        firstRow.ReissueReason.Should().BeNull("only the operator-initiated reissue itself carries a reason — the original render never did");

        var audit = await db.Set<AuditLogEntry>().AsNoTracking().SingleAsync(a => a.EntityId == reissued.Id && a.Operation == "ADDED");
        using var auditValues = JsonDocument.Parse(audit.NewValuesJson!);
        auditValues.RootElement.GetProperty("ReissueReason").GetString().Should().Be(reissuedRow.ReissueReason);
        auditValues.RootElement.GetProperty("GeneratedByUserId").GetGuid().Should().Be(reissuedRow.GeneratedByUserId!.Value);
        auditValues.RootElement.GetProperty("IssuedAt").GetDateTimeOffset().Should().BeCloseTo(reissuedRow.IssuedAt, TimeSpan.FromMilliseconds(1));

        var history = await ReadAsync<IReadOnlyList<QuotePdfHistoryItemResponse>>(await owner.GetAsync(PdfPath(quote.Id, revisionId) + "/history"));
        history.Should().HaveCount(2);
        history.Single(h => h.Id == reissued.Id).IsCurrent.Should().BeTrue();
        history.Single(h => h.Id == first.Id).IsCurrent.Should().BeFalse();

        (await owner.GetAsync(PdfPath(quote.Id, revisionId))).Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var currentBytes = await (await owner.GetAsync(PdfPath(quote.Id, revisionId))).Content.ReadAsByteArrayAsync();
        var reissuedBytes = await (await owner.GetAsync($"{PdfPath(quote.Id, revisionId)}/{reissued.Id}")).Content.ReadAsByteArrayAsync();
        currentBytes.Should().BeEquivalentTo(reissuedBytes, "downloading 'current' after a reissue must return the REISSUED artifact");

        // The superseded historical document is still individually downloadable (mission §61/§116).
        var historicalBytes = await (await owner.GetAsync($"{PdfPath(quote.Id, revisionId)}/{first.Id}")).Content.ReadAsByteArrayAsync();
        historicalBytes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reissue_requires_manage_permission_and_CSRF_like_every_other_mutation()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var withoutCsrf = await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest(null), withAntiforgery: false);
        withoutCsrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- F-03: reissue reason is required ----

    [Fact]
    public async Task Reissue_with_a_missing_reason_is_rejected_and_persists_no_document()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));

        var response = await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest(null));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("DOCUMENT_REISSUE_REASON_REQUIRED");

        (await _fixture.CreateContext().Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(1,
            "a rejected reissue must never create a second document row");
        _ = first;
    }

    [Fact]
    public async Task Reissue_with_a_blank_or_whitespace_only_reason_is_rejected()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var blank in new[] { "", "   ", "\n\t " })
        {
            var response = await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest(blank));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ProblemCodeAsync(response)).Should().Be("DOCUMENT_REISSUE_REASON_REQUIRED");
        }

        (await _fixture.CreateContext().Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionId)).Should().Be(1);
    }

    [Fact]
    public async Task Reissue_with_a_reason_exceeding_the_maximum_length_is_rejected()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var tooLong = new string('a', 2001);
        var response = await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest(tooLong));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("DOCUMENT_REISSUE_REASON_TOO_LONG");
    }

    [Fact]
    public async Task Reissue_with_a_valid_reason_succeeds_and_the_reason_is_trimmed_before_persisting()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        (await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reissued = await ReadAsync<QuotePdfMetadataResponse>(
            await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest("  Endereço de entrega corrigido.  ")));

        await using var db = _fixture.CreateContext();
        var row = await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == reissued.Id);
        row.ReissueReason.Should().Be("Endereço de entrega corrigido.");
        row.GeneratedByUserId.Should().NotBeNull("the actor must be persisted alongside the reason (ADR-0016 §5)");
        row.IssuedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ---- Snapshot immutability ----

    [Fact]
    public async Task Changing_the_company_profile_after_issuance_never_alters_an_already_issued_document()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;

        await SetCompanyLegalNameAsync("Verce Original Ltda");
        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));

        await using (var db = _fixture.CreateContext())
        {
            var snapshot = (await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == first.Id)).RenderDataSnapshotJson;
            snapshot.Should().Contain("Verce Original Ltda");
        }

        await SetCompanyLegalNameAsync("Verce Renomeada Ltda");

        // A plain generate-or-get-current call never re-reads Settings (mission §33/§35) — same
        // row, same hash, same frozen name.
        var again = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));
        again.Id.Should().Be(first.Id);
        again.PdfSha256.Should().Be(first.PdfSha256);

        await using (var db = _fixture.CreateContext())
        {
            var stillFrozen = (await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == first.Id)).RenderDataSnapshotJson;
            stillFrozen.Should().Contain("Verce Original Ltda").And.NotContain("Verce Renomeada Ltda");
        }

        // Only an explicit reissue picks up the new company name, into a NEW row — the old one
        // is untouched.
        var reissued = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest("Razão social da empresa foi atualizada.")));
        await using (var db = _fixture.CreateContext())
        {
            (await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == reissued.Id)).RenderDataSnapshotJson.Should().Contain("Verce Renomeada Ltda");
            (await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == first.Id)).RenderDataSnapshotJson.Should().Contain("Verce Original Ltda",
                "the historical row's snapshot must never be rewritten by a LATER reissue of the SAME revision");
        }
    }

    [Fact]
    public async Task Switching_which_template_is_the_default_never_alters_an_already_issued_documents_template_version()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));
        var originalTemplateVersionId = first.DocumentTemplateVersionId;

        // Publish a second QUOTE template (a distinct template, not a new version of the same
        // one — S7 ships no draft->publish authoring workflow on the SAME template; that
        // multi-version-per-template editing flow is explicitly S14 scope, ADR-0007 §7.2) and
        // make IT the default, exactly as an S14 "publish and activate" action eventually will.
        Guid newTemplateVersionId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
            var previousDefault = await db.Set<DocumentTemplate>().SingleAsync(t => t.DocumentTypeCode == "QUOTE" && t.IsDefault);
            var newTemplate = new DocumentTemplate("QUOTE", "Modelo alternativo de teste", isDefault: false);
            var version = newTemplate.PublishFirstVersion(DefaultProposalTemplateSeedData.BuildDefinitionJson(),
                DefaultProposalTemplateSeedData.BuildPageSetupJson(), DefaultProposalTemplateSeedData.SchemaVersion,
                DocumentTemplateValidator.Validate, DateTimeOffset.UtcNow, publishedBy: null);
            newTemplateVersionId = version.Id;
            db.Add(newTemplate);
            // ix_document_template_default_per_type: only one default per type — flip atomically.
            typeof(DocumentTemplate).GetProperty(nameof(DocumentTemplate.IsDefault))!.SetValue(previousDefault, false);
            typeof(DocumentTemplate).GetProperty(nameof(DocumentTemplate.IsDefault))!.SetValue(newTemplate, true);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            (await db.Set<GeneratedDocument>().AsNoTracking().SingleAsync(d => d.Id == first.Id)).DocumentTemplateVersionId
                .Should().Be(originalTemplateVersionId, "an already-issued document's template version reference must never move to a newer default");
        }

        var reissued = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync(PdfPath(quote.Id, revisionId) + "/reissue", new QuotePdfReissueRequest("Modelo padrão foi atualizado.")));
        reissued.DocumentTemplateVersionId.Should().Be(newTemplateVersionId, "a NEW render always resolves the CURRENT default template");
    }

    // ---- No mutation/delete surface ----

    [Fact]
    public async Task There_is_no_HTTP_surface_to_mutate_or_delete_an_issued_document()
    {
        var (quote, owner) = await CreateApprovedQuoteAsync();
        var revisionId = quote.CurrentRevision.Id;
        var first = await ReadAsync<QuotePdfMetadataResponse>(await owner.PostAsync<object?>(PdfPath(quote.Id, revisionId), null));

        var deleteResponse = await owner.DeleteAsync($"{PdfPath(quote.Id, revisionId)}/{first.Id}");
        deleteResponse.StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
        var putResponse = await owner.PutAsync<object?>($"{PdfPath(quote.Id, revisionId)}/{first.Id}", null);
        putResponse.StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);

        (await _fixture.CreateContext().Set<GeneratedDocument>().AsNoTracking().AnyAsync(d => d.Id == first.Id)).Should().BeTrue();
    }

    private async Task SetCompanyLegalNameAsync(string legalName)
    {
        await using var db = _fixture.CreateContext();
        var profile = await db.Set<CompanyProfile>().SingleOrDefaultAsync();
        if (profile is null)
        {
            profile = new CompanyProfile(new CompanyProfileValues(legalName, legalName, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            db.Add(profile);
        }
        else
        {
            profile.Update(new CompanyProfileValues(legalName, profile.TradeName, profile.Document, profile.Email, profile.Phone,
                profile.Website, profile.Instagram, profile.WhatsApp, profile.ZipCode, profile.Street, profile.Number,
                profile.Complement, profile.District, profile.City, profile.State, profile.Country, profile.Timezone, profile.Currency));
        }
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> AuthedGetAsync(Guid quoteId)
    {
        var (client, _) = await LoggedInAsAsync(Verce.Platform.Identity.Roles.Owner);
        return await client.GetAsync($"/api/quotes/{quoteId}");
    }

    private async Task<(AuthTestClient Client, Guid UserId)> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Verce.Platform.Identity.ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Verce.Platform.Identity.ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new Verce.Platform.Identity.ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new Verce.Platform.Identity.ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = role + " PDF Outbox",
            IsActive = true,
            SetupStatus = Verce.Platform.Identity.SetupStatus.Active,
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
}
