using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.Marketplaces.E2EHost;

/// <summary>
/// S8C.2 D11 (handoff D11): the test-only control plane of <see cref="FakeMercadoLivreHttpHandler"/>, mapped by THIS host only (never Verce.Api):
/// <list type="bullet">
/// <item><c>POST /__e2e__/mercadolivre/state</c> sets the synthetic seller and listings (and may reset call counters);</item>
/// <item><c>GET /__e2e__/mercadolivre/calls</c> returns the safe call evidence (counts and bulk id lists, no secrets);</item>
/// <item><c>GET /__e2e__/mercadolivre/observations/{externalId}</c> returns the persisted observation history of that listing, read from the disposable database.</item>
/// </list>
/// Raw middleware on a fixed path prefix, like the S8C.1 control route, so it can never shadow a real <c>/api</c> mapping.
/// </summary>
public sealed class MercadoLivreE2EControlEndpointsStartupFilter(FakeMercadoLivreHttpHandler fake) : IStartupFilter
{
    private const string Prefix = "/__e2e__/mercadolivre/";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) { await nextMiddleware(context); return; }
            var route = path[Prefix.Length..];

            if (HttpMethods.IsPost(context.Request.Method) && route == "state")
            {
                var body = await JsonSerializer.DeserializeAsync<StateRequest>(context.Request.Body, Web, context.RequestAborted) ?? throw new InvalidOperationException("E2E_ML_STATE_BODY_REQUIRED");
                fake.Configure(new FakeMercadoLivreScenario(body.SellerId, (body.Items ?? []).Select(x => new FakeMercadoLivreItem(x.Id, x.Sku, x.Title ?? x.Id, x.Price, x.Status ?? "active", x.Variations, x.Fail, x.SellerId)).ToArray(), body.ResetCalls));
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && route == "calls")
            {
                await context.Response.WriteAsJsonAsync(fake.Calls(), Web, context.RequestAborted);
                return;
            }
            const string observations = "observations/";
            if (HttpMethods.IsGet(context.Request.Method) && route.StartsWith(observations, StringComparison.Ordinal))
            {
                var externalId = Uri.UnescapeDataString(route[observations.Length..]);
                using var scope = context.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
                var listings = await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).Where(x => x.ExternalListingId == externalId).ToListAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(new
                {
                    externalId,
                    listings = listings.Count,
                    listingIds = listings.Select(x => x.Id),
                    observations = listings.SelectMany(l => l.Observations).OrderBy(o => o.IngestedAt)
                        .Select(o => new { o.ExternalSku, title = o.TitleSnapshot, price = o.ObservedPrice, status = o.ObservedStatus.ToString(), provenance = o.Provenance.ToString(), o.VariationCount }).ToArray(),
                }, Web, context.RequestAborted);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        next(app);
    };

    private sealed class StateRequest
    {
        public long SellerId { get; set; }
        public bool ResetCalls { get; set; }
        public List<ItemRequest>? Items { get; set; }
    }

    private sealed class ItemRequest
    {
        public string Id { get; set; } = "";
        public string? Sku { get; set; }
        public string? Title { get; set; }
        public decimal? Price { get; set; }
        public string? Status { get; set; }
        public int Variations { get; set; }
        public bool Fail { get; set; }
        public long? SellerId { get; set; }
    }
}
