using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Verce.Platform;

namespace Verce.Infrastructure.Marketplaces.MercadoLivre;

/// <summary>
/// ADR-0025 §A.2/A.6. The application (VERCE) credential — never the seller's — for the real
/// Mercado Livre OAuth app. Application secret is delivered from User Secrets (Development) or a
/// mounted <c>0400</c> file (production-grade path); it is never logged, never returned in a DTO,
/// never stored in the database, and <see cref="appsettings.json"/> may only ever hold
/// <see cref="Enabled"/>=false and the numeric margin default.
/// </summary>
public sealed class MercadoLivreOptions
{
    public const string SectionName = "Marketplaces:MercadoLivre";

    public bool Enabled { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ClientSecretFile { get; init; }
    public string? RedirectUri { get; init; }
    public int AccessTokenRefreshMarginSeconds { get; init; } = 300;

    /// <summary>Resolved once at startup (User Secrets value, or the mounted file's content read
    /// once) — never re-read per request, never logged, never bound from configuration directly
    /// (only <see cref="MercadoLivreOptionsValidator"/> and tests ever assign it).</summary>
    public string? ResolvedClientSecret { get; set; }
}

/// <summary>
/// §A.2 startup validation, fully enforced via <c>ValidateOnStart</c>: "Messages name keys, never
/// values" — every failure message below names only a configuration key, never a submitted value.
/// The dynamic "origin equal to the origin serving /api to the browser" cross-check is out of an
/// options validator's reach (Kestrel's bound addresses are not yet resolved at options-validation
/// time) and is therefore a deliberately narrower, statically-checkable proxy: RedirectUri's origin
/// must agree with the ONE authoritative <see cref="PublicOriginOptions.PublicOrigin"/> (A-09) —
/// never a second, independently-declared value only this class knows about. Operators state the
/// origin they intend to serve from once, and BOTH the cookie policy's https assumption and this
/// redirect check depend on that same value; RedirectUri's path must also be the exact fixed
/// callback path.
/// </summary>
public sealed class MercadoLivreOptionsValidator : IValidateOptions<MercadoLivreOptions>
{
    private const string FixedCallbackPath = "/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback";
    private static readonly Regex ClientIdPattern = new("^[0-9]{1,32}$", RegexOptions.Compiled);
    private readonly IOptions<PublicOriginOptions> _publicOrigin;

    public MercadoLivreOptionsValidator(IOptions<PublicOriginOptions> publicOrigin) => _publicOrigin = publicOrigin;

    public ValidateOptionsResult Validate(string? name, MercadoLivreOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(options.ClientId) || !ClientIdPattern.IsMatch(options.ClientId))
            return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre:ClientId must match ^[0-9]{1,32}$.");

        var hasSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);
        var hasSecretFile = !string.IsNullOrWhiteSpace(options.ClientSecretFile);
        if (hasSecret == hasSecretFile)
            return ValidateOptionsResult.Fail("Exactly one of Marketplaces:MercadoLivre:ClientSecret or ClientSecretFile must be set.");

        string secret;
        if (hasSecret)
        {
            secret = options.ClientSecret!;
        }
        else
        {
            if (!Path.IsPathFullyQualified(options.ClientSecretFile!))
                return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre:ClientSecretFile must be an absolute path.");
            try { secret = File.ReadAllText(options.ClientSecretFile!).TrimEnd('\r', '\n'); }
            catch (Exception) { return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre:ClientSecretFile could not be read."); }
        }
        if (secret.Length is < 1 or > 512 || !secret.All(c => c is >= (char)0x20 and <= (char)0x7E))
            return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre client secret must be 1..512 printable ASCII characters.");
        options.ResolvedClientSecret = secret;

        if (string.IsNullOrWhiteSpace(options.RedirectUri) || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment)
            || redirect.AbsolutePath != FixedCallbackPath)
            return ValidateOptionsResult.Fail($"Marketplaces:MercadoLivre:RedirectUri must be an absolute https URL, no query/fragment, path exactly {FixedCallbackPath}.");

        // A-09: the registered redirect must agree with the ONE authoritative Verce:PublicOrigin —
        // scheme, host AND port, compared ordinally on the host (DNS is case-insensitive; Uri
        // already lowercases Host). This is a static, documented substitute for a live
        // Kestrel-binding check (bound addresses do not exist yet at options-validation time), and
        // it deliberately reads the SAME setting the cookie policy validator depends on rather than
        // re-declaring a second "expected origin" only this class would know about.
        var publicOrigin = _publicOrigin.Value.PublicOrigin;
        if (string.IsNullOrWhiteSpace(publicOrigin) || !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var serverOrigin)
            || !string.IsNullOrEmpty(serverOrigin.PathAndQuery.TrimEnd('/')) || !string.IsNullOrEmpty(serverOrigin.Fragment))
            return ValidateOptionsResult.Fail("Verce:PublicOrigin must be an absolute origin (scheme+host+port), no path/query/fragment.");
        if (!string.Equals(serverOrigin.Scheme, redirect.Scheme, StringComparison.Ordinal)
            || !string.Equals(serverOrigin.Host, redirect.Host, StringComparison.Ordinal)
            || serverOrigin.Port != redirect.Port)
            return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre:RedirectUri must have the same scheme, host and port as the authoritative Verce:PublicOrigin.");

        if (options.AccessTokenRefreshMarginSeconds is < 0 or > 3600)
            return ValidateOptionsResult.Fail("Marketplaces:MercadoLivre:AccessTokenRefreshMarginSeconds must be 0..3600.");

        return ValidateOptionsResult.Success;
    }
}
