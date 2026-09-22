using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verce.Modules.Documents;
using Verce.Modules.Quoting.Contracts;
using Verce.Modules.Settings;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;

namespace Verce.Api.Quoting;

/// <summary>
/// S7/S14 scope authority gate §12/§54-59: the outbox consumer for
/// <see cref="GenerateQuotePdfRequestedEvent"/> — approval never depends on Chromium succeeding
/// (ADR-0012 §1). Registered scoped, so every dispatch cycle gets a fresh <see cref="VerceDbContext"/>
/// (mirrors <c>Verce.Modules.Quoting.Contracts</c> event handlers). Delivery is at-least-once, so
/// this MUST be idempotent (ADR-0012 §22): it always calls
/// <see cref="QuotePdfService.MaterializeForRequestAsync"/> with the event's own
/// <see cref="GenerateQuotePdfRequestedEvent.RenderRequestId"/> — a retried delivery of the SAME
/// message converges on the SAME row; it never creates a second document.
///
/// A render/storage failure here throws, which the dispatcher turns into a retryable outbox
/// failure (ADR-0012 Part II) — it never marks the message processed and never leaves a corrupt
/// row, because <see cref="QuotePdfService"/> writes nothing until the full render succeeds.
/// </summary>
public sealed class GenerateQuotePdfRequestedConsumer : IIntegrationEventConsumer
{
    // Strictly below OutboxProcessor.DefaultLeaseDuration (validated at startup by
    // OutboxConsumerRegistry) — comfortably above the renderer's own 30s internal budget so a
    // slow-but-succeeding render is never pre-empted by the outbox's own timeout first.
    private static readonly TimeSpan ConsumerTimeout = TimeSpan.FromSeconds(45);

    private readonly VerceDbContext _db;
    private readonly BrandAssetStorage _brandAssetStorage;
    private readonly QuotePdfService _pdfService;

    public GenerateQuotePdfRequestedConsumer(VerceDbContext db, BrandAssetStorage brandAssetStorage, QuotePdfService pdfService)
    {
        _db = db;
        _brandAssetStorage = brandAssetStorage;
        _pdfService = pdfService;
    }

    public string EventType => nameof(GenerateQuotePdfRequestedEvent);
    public TimeSpan Timeout => ConsumerTimeout;

    public async Task HandleAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<GenerateQuotePdfRequestedPayload>(message.PayloadJson)
            ?? throw new NonRetryableOutboxException("GENERATE_QUOTE_PDF_REQUESTED_PAYLOAD_INVALID");

        var (revision, quote) = await QuotePdfInputBuilder.LoadRevisionAndQuoteAsync(_db, payload.QuoteId, payload.QuoteRevisionId, cancellationToken);
        if (revision is null || quote is null)
            // The revision existed at approval time (this event is only ever raised from
            // Quote.Approve) and revisions are never deleted — a missing row here means a payload
            // or test-data defect, not a transient condition retrying could fix.
            throw new NonRetryableOutboxException($"QUOTE_REVISION_NOT_FOUND:{payload.QuoteRevisionId:D}");

        var input = await QuotePdfInputBuilder.BuildAsync(_db, _brandAssetStorage, quote, revision, cancellationToken);
        await _pdfService.MaterializeForRequestAsync(input, payload.RenderRequestId, actorUserId: null, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>Mirrors <see cref="GenerateQuotePdfRequestedEvent"/>'s own public shape exactly —
    /// deserialized from the outbox's persisted <c>PayloadJson</c> (written by
    /// <c>JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType())</c> in
    /// <c>UnitOfWork</c>), never re-referencing the event type itself so this consumer stays a
    /// plain reader of already-persisted, already-trusted data.</summary>
    private sealed record GenerateQuotePdfRequestedPayload(Guid QuoteId, Guid QuoteRevisionId, Guid RenderRequestId);
}
