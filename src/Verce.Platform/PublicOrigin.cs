using Microsoft.Extensions.Options;

namespace Verce.Platform;

/// <summary>
/// A-09 (S8C.2 Phase A final consolidation): the SINGLE server-owned authoritative public origin
/// (scheme+host+effective port) this deployment is configured to serve <c>/api</c> from — e.g.
/// <c>https://verce.example.com</c>, or <c>https://localhost:5443</c>/a registered hosts-file
/// hostname for the frozen live-validation preparation paths. There is deliberately only ONE such
/// value in the entire application: it is what the <c>__Host-</c> cookie policy's https-only
/// assumption depends on (a <c>Secure</c>-only cookie is unusable from a non-https origin), and it
/// is the SAME value any provider redirect-origin check (e.g. Mercado Livre's OAuth RedirectUri)
/// must agree with — never a second, independently-declared "truth" a human has to keep in sync.
/// </summary>
public sealed class PublicOriginOptions
{
    public const string SectionName = "Verce";
    public string? PublicOrigin { get; init; }
}

/// <summary>
/// Startup validation (<c>ValidateOnStart</c>): the configured origin must be a syntactically valid
/// absolute origin with no path/query/fragment, and it must be https — the application's
/// authentication and antiforgery cookies are both <c>__Host-</c>-prefixed with
/// <c>SecurePolicy.Always</c> (see <c>Program.cs</c>), which a non-https origin could never
/// actually carry. This is the one place that fact is asserted; every other consumer (e.g. the
/// Mercado Livre redirect-origin check) reads the SAME <see cref="PublicOriginOptions.PublicOrigin"/>
/// value rather than re-declaring it.
/// </summary>
public sealed class PublicOriginOptionsValidator : IValidateOptions<PublicOriginOptions>
{
    public ValidateOptionsResult Validate(string? name, PublicOriginOptions options)
    {
        // Not every host/composition needs to declare this (e.g. a test host with no
        // origin-sensitive feature enabled) — but whenever a value IS supplied, its FORMAT is
        // validated here, everywhere, once. A consumer that requires it unconditionally (e.g.
        // MercadoLivreOptionsValidator, only when ML itself is enabled) enforces its own presence.
        if (string.IsNullOrWhiteSpace(options.PublicOrigin)) return ValidateOptionsResult.Success;
        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.PathAndQuery.TrimEnd('/')) || !string.IsNullOrEmpty(origin.Fragment))
            return ValidateOptionsResult.Fail("Verce:PublicOrigin must be an absolute origin (scheme+host+port), no path/query/fragment.");
        if (origin.Scheme != Uri.UriSchemeHttps)
            return ValidateOptionsResult.Fail("Verce:PublicOrigin must use https — the application's __Host- cookies require a secure origin (SecurePolicy.Always).");
        return ValidateOptionsResult.Success;
    }
}
