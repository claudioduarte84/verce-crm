using FluentAssertions;
using Verce.IntegrationTests.Auth;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-09 (final consolidation): "a bare unit call directly to the validator function is not
/// sufficient proof this time; it must show the real startup path failing." These tests actually
/// build and start the real <see cref="Verce.Api.Program"/> composition (via
/// <see cref="VerceWebApplicationFactory"/>, the same host every other integration test in this
/// repository boots) with the Mercado Livre local-store branch enabled, and assert that a
/// mismatched Verce:PublicOrigin vs. Marketplaces:MercadoLivre:RedirectUri makes the HOST ITSELF
/// fail to start — not merely that a validator object returns a failed result in isolation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MercadoLivreOptionsStartupValidationTests
{
    private readonly PostgresFixture _fixture;
    public MercadoLivreOptionsStartupValidationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Real_host_startup_fails_when_the_ML_redirect_origin_disagrees_with_the_authoritative_public_origin()
    {
        using var harness = new MercadoLivreRealAdapterHarness();
        var mismatched = new Dictionary<string, string?>(harness.ExtraConfiguration)
        {
            // The harness's own RedirectUri origin is "https://verce-ml-test.local" — deliberately
            // override JUST the authoritative public origin to a DIFFERENT one.
            ["Verce:PublicOrigin"] = "https://a-completely-different-origin.example",
        };

        await using var factory = new VerceWebApplicationFactory(_fixture.ConnectionString, mismatched, configureTestServices: harness.ConfigureServices);
        // WebApplicationFactory builds/starts the host lazily on first access to Server/CreateClient
        // — this is the moment ValidateOnStart's background validation actually runs and throws.
        var act = () => { _ = factory.Server; };
        act.Should().Throw<Exception>("a mismatched public origin must fail REAL host startup, not merely a standalone validator call")
            .Where(ex => ex.ToString().Contains("PublicOrigin", StringComparison.OrdinalIgnoreCase) || ex is AggregateException || ex.GetType().Name.Contains("OptionsValidation"));
    }

    [Fact]
    public async Task Real_host_starts_successfully_when_the_ML_redirect_origin_matches_the_authoritative_public_origin()
    {
        using var harness = new MercadoLivreRealAdapterHarness();
        // The harness's own configuration already declares matching origins — this proves the
        // positive case through the exact same real-startup path, not just absence of a thrown
        // exception in the mismatch test above.
        await using var factory = new VerceWebApplicationFactory(_fixture.ConnectionString, harness.ExtraConfiguration, configureTestServices: harness.ConfigureServices);
        var act = () => { _ = factory.Server; };
        act.Should().NotThrow();
    }
}
