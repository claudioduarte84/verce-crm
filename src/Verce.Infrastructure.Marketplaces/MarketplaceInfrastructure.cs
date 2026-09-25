using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Modules.Commerce;

namespace Verce.Infrastructure.Marketplaces;

public sealed class MarketplaceCredentialStoreOptions
{
    public const string SectionName = "Marketplaces";
    public string? CredentialStoreRoot { get; init; }
    public string? RepositoryRoot { get; init; }
}

public static class MarketplaceInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVerceMarketplaceInfrastructure(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var options = configuration.GetSection(MarketplaceCredentialStoreOptions.SectionName).Get<MarketplaceCredentialStoreOptions>() ?? new();
        services.Configure<MarketplaceCredentialStoreOptions>(configuration.GetSection(MarketplaceCredentialStoreOptions.SectionName));

        // Production intentionally has no local credential store or connector registration.
        // The test host explicitly opts in by setting an external root in Development.
        services.AddSingleton<ProviderExecutionGate>();
        services.AddScoped<MarketplaceAuthorizationWorkflow>();
        if (environment.IsDevelopment() && !string.IsNullOrWhiteSpace(options.CredentialStoreRoot))
        {
            services.AddSingleton<IProtectedCredentialStore, LocalProtectedCredentialStore>();
            services.AddSingleton<IMarketplaceConnectorRegistry, MarketplaceConnectorRegistry>();
            // G-06: housekeeping/startup-recovery only exist where a local store/registry are
            // actually active. Production registers neither, so there is nothing to reconcile.
            services.AddVerceMarketplaceHousekeeping(configuration);
        }
        else
        {
            // G-06: "Production startup rejects any connector registration marked test-only."
            // S8C.1 ships zero real provider connectors, so ANY IMarketplaceAuthorizationConnector
            // present here is by definition test-only — a defense-in-depth guard, not merely
            // "the production dependency graph doesn't reference the fake project." This fails
            // startup closed rather than silently building a registry that would expose it.
            if (services.Any(descriptor => descriptor.ServiceType == typeof(IMarketplaceAuthorizationConnector)))
                throw new InvalidOperationException(
                    "MARKETPLACE_TEST_ONLY_CONNECTOR_IN_NON_DEVELOPMENT_COMPOSITION: a test-only IMarketplaceAuthorizationConnector " +
                    "registration was found while composing a non-Development/no-local-store host. This is never valid outside an " +
                    "isolated test harness.");
            services.AddSingleton<IProtectedCredentialStore, UnsupportedProtectedCredentialStore>();
            services.AddSingleton<IMarketplaceConnectorRegistry, MarketplaceConnectorRegistry>();
        }
        return services;
    }
}

internal sealed class UnsupportedProtectedCredentialStore : IProtectedCredentialStore
{
    private static readonly ProtectedCredentialStoreResult Unsupported = new(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE);
    public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken cancellationToken) => Task.FromResult(Unsupported);
    public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken cancellationToken) => Task.FromResult(Unsupported);
    public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken) => Task.FromResult(Unsupported);
    public Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken cancellationToken) => Task.FromResult(new ProtectedCredentialReadResult(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE, ReadOnlyMemory<byte>.Empty));
    public Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Unsupported);
    public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken) => Task.FromResult(Unsupported);
}

public sealed class MarketplaceConnectorRegistry : IMarketplaceConnectorRegistry
{
    private readonly IReadOnlyDictionary<string, IMarketplaceAuthorizationConnector> _connectors;

    public MarketplaceConnectorRegistry(IEnumerable<IMarketplaceAuthorizationConnector> connectors)
    {
        var dictionary = new Dictionary<string, IMarketplaceAuthorizationConnector>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in connectors)
        {
            if (!dictionary.TryAdd(connector.ProviderCode, connector))
                throw new InvalidOperationException($"Duplicate marketplace connector registration: {connector.ProviderCode}.");
        }
        _connectors = dictionary;
    }

    public bool TryGet(string providerCode, out IMarketplaceAuthorizationConnector connector) =>
        _connectors.TryGetValue(providerCode, out connector!);
}

/// <summary>Development/test-only encrypted local store. Business orchestration never reads by client-supplied reference.</summary>
public sealed class LocalProtectedCredentialStore : IProtectedCredentialStore, IDisposable
{
    private readonly string _root;
    private readonly IDataProtector _protector;
    private readonly FileStream _ownershipHandle;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public LocalProtectedCredentialStore(IDataProtectionProvider dataProtection, Microsoft.Extensions.Options.IOptions<MarketplaceCredentialStoreOptions> options, IWebHostEnvironment environment)
    {
        _root = ValidateRoot(options.Value, environment);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "staged"));
        Directory.CreateDirectory(Path.Combine(_root, "candidates"));
        _ownershipHandle = new FileStream(Path.Combine(_root, ".verce-marketplace-owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _protector = dataProtection.CreateProtector("Verce3D.Marketplaces.Credentials.v1");
    }

    public async Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken cancellationToken)
    {
        if (!Validate(write, out var invalid)) return invalid;
        var gate = _locks.GetOrAdd(write.CredentialReference, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            CredentialEnvelope? current;
            try { current = await ReadEnvelopeAsync(ConfirmedPath(write.CredentialReference), cancellationToken); }
            catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }

            // RT-03: never blindly reset to version 1 under a name that may already carry history.
            // ExpectedPreviousVersion null means "this reference has no prior material" (a fresh
            // K2 generation, or a genuinely first-ever bind); non-null means the caller's own DB
            // truth expects EXACTLY that confirmed version to be physically present right now.
            // A caller detecting NOT_FOUND/VERSION_CONFLICT here is expected to retry once under a
            // brand-new reference — see MarketplaceAuthorizationWorkflow's reconnect path.
            if (write.ExpectedPreviousVersion is { } expected)
            {
                if (current is null) return new(ProtectedCredentialStoreOutcome.NOT_FOUND);
                if (current.Version != expected) return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
            }
            else if (current is not null)
            {
                // A caller asked for a "never bound before" reference, but one exists. Since
                // references are 32 CSPRNG bytes this is not a legitimate collision — treat it as
                // a conflict rather than silently reusing/overwriting unrelated material.
                return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
            }

            var nextVersion = current is null ? 1 : current.Version + 1;
            var envelope = new CredentialEnvelope(write.OperationId, write.SessionId, write.AccountId, write.ProviderCode, write.CredentialReference, nextVersion, Convert.ToBase64String(write.SecretMaterial.Span));
            var receipt = await WriteEnvelopeAsync(StagedPath(write.OperationId), envelope, cancellationToken);
            return new(ProtectedCredentialStoreOutcome.SUCCESS, receipt);
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.WRITE_FAILED); }
        finally { gate.Release(); }
    }

    public async Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            var source = StagedPath(binding.OperationId);
            var envelope = await ReadEnvelopeAsync(source, cancellationToken);
            if (envelope is null) return new(ProtectedCredentialStoreOutcome.NOT_FOUND);
            if (envelope.OperationId != binding.OperationId || envelope.SessionId != binding.SessionId || envelope.ProviderCode != binding.ProviderCode || envelope.CredentialReference != binding.CredentialReference)
                return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
            envelope = envelope with { AccountId = binding.AccountId };
            var receipt = await WriteEnvelopeAsync(CandidatePath(binding.OperationId, binding.CredentialReference), envelope, cancellationToken);
            File.Delete(source);
            return new(ProtectedCredentialStoreOutcome.SUCCESS, receipt);
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.WRITE_FAILED); }
    }

    public async Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(credentialReference, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var candidate = CandidatePath(operationId, credentialReference);
            var envelope = await ReadEnvelopeAsync(candidate, cancellationToken);
            if (envelope is null) return new(ProtectedCredentialStoreOutcome.NOT_FOUND);
            if (envelope.OperationId != operationId || envelope.Version != credentialVersion) return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
            File.Move(candidate, ConfirmedPath(credentialReference), true);
            return new(ProtectedCredentialStoreOutcome.SUCCESS, Receipt(envelope));
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.WRITE_FAILED); }
        finally { gate.Release(); }
    }

    public async Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await ReadEnvelopeAsync(ConfirmedPath(read.CredentialReference), cancellationToken);
            if (envelope is null) return new(ProtectedCredentialStoreOutcome.NOT_FOUND, ReadOnlyMemory<byte>.Empty);
            if (envelope.AccountId != read.AccountId || envelope.ProviderCode != read.ProviderCode || envelope.OperationId != read.ConfirmedOperationId || envelope.Version != read.CredentialVersion)
                return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT, ReadOnlyMemory<byte>.Empty);
            var receipt = Receipt(envelope);
            return new(ProtectedCredentialStoreOutcome.SUCCESS, Convert.FromBase64String(envelope.Secret), receipt);
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE, ReadOnlyMemory<byte>.Empty); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE, ReadOnlyMemory<byte>.Empty); }
    }

    public async Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken cancellationToken)
    {
        if (!Validate(write, out var invalid) || expectedVersion < 0) return invalid;
        var gate = _locks.GetOrAdd(write.CredentialReference, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = ConfirmedPath(write.CredentialReference);
            var current = await ReadEnvelopeAsync(path, cancellationToken);
            if (current is null && expectedVersion != 0) return new(ProtectedCredentialStoreOutcome.NOT_FOUND);
            if (current is not null && (current.Version != expectedVersion || current.AccountId != write.AccountId || current.ProviderCode != write.ProviderCode)) return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
            var envelope = new CredentialEnvelope(write.OperationId, write.SessionId, write.AccountId, write.ProviderCode, write.CredentialReference, expectedVersion + 1, Convert.ToBase64String(write.SecretMaterial.Span));
            return new(ProtectedCredentialStoreOutcome.SUCCESS, await WriteEnvelopeAsync(path, envelope, cancellationToken));
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.WRITE_FAILED); }
        finally { gate.Release(); }
    }

    public async Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(credentialReference, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var candidate = CandidatePath(operationId, credentialReference);
            if (File.Exists(candidate))
            {
                var envelope = await ReadEnvelopeAsync(candidate, cancellationToken);
                if (envelope?.OperationId != operationId || envelope.Version != credentialVersion) return new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
                File.Delete(candidate);
                return new(ProtectedCredentialStoreOutcome.SUCCESS);
            }

            // Promotion precedes the terminal database decision. It therefore remains
            // non-executable until the exact DB operation confirms it. If that decision fails,
            // remove only an envelope still owned by this failed operation.
            var confirmed = ConfirmedPath(credentialReference);
            var envelopeAtConfirmedPath = await ReadEnvelopeAsync(confirmed, cancellationToken);
            if (envelopeAtConfirmedPath?.OperationId != operationId || envelopeAtConfirmedPath.Version != credentialVersion)
                return new(ProtectedCredentialStoreOutcome.NOT_FOUND);
            File.Delete(confirmed);
            return new(ProtectedCredentialStoreOutcome.SUCCESS);
        }
        catch (CryptographicException) { return new(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ProtectedCredentialStoreOutcome.WRITE_FAILED); }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        _ownershipHandle.Dispose();
        foreach (var gate in _locks.Values) gate.Dispose();
    }

    private static bool Validate(ProtectedCredentialWrite write, out ProtectedCredentialStoreResult invalid)
    {
        invalid = new(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
        return write.OperationId != Guid.Empty && !string.IsNullOrWhiteSpace(write.ProviderCode) && !string.IsNullOrWhiteSpace(write.CredentialReference) && !write.SecretMaterial.IsEmpty;
    }

    private async Task<ProtectedCredentialReceipt> WriteEnvelopeAsync(string path, CredentialEnvelope envelope, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var protectedBytes = _protector.Protect(plain);
        var temp = path + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken);
        MoveReplaceWithRetry(temp, path);
        CryptographicOperations.ZeroMemory(plain);
        var integrity = Convert.ToHexString(SHA256.HashData(protectedBytes)).ToLowerInvariant();
        return new ProtectedCredentialReceipt(envelope.OperationId, envelope.CredentialReference, envelope.Version, integrity, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Windows can transiently refuse <c>MoveFileEx(..., MOVEFILE_REPLACE_EXISTING)</c> with
    /// <see cref="UnauthorizedAccessException"/> when a concurrent reader's handle on the
    /// destination has not yet released, even though that reader opened with
    /// <see cref="FileShare.Delete"/> — the OS-level race window is real but genuinely
    /// microseconds wide (the reader's own read completes almost immediately). A handful of
    /// short retries resolves it without weakening atomicity: the destination is still replaced
    /// in one indivisible rename, just possibly on the second or third attempt rather than the
    /// first. This is not a general-purpose retry policy — it exists only for this one narrow,
    /// well-understood OS interaction.
    /// </summary>
    private static void MoveReplaceWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(source, destination, true); return; }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { Thread.Sleep(5 * attempt); }
            catch (IOException) when (attempt < maxAttempts) { Thread.Sleep(5 * attempt); }
        }
    }

    private async Task<CredentialEnvelope?> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        byte[] cipher;
        try
        {
            // "Readers open a complete immutable file snapshot" and a concurrent atomic replace
            // must never block on — or be blocked by — a reader. On Windows, File.Move (rename)
            // over an existing destination requires delete access on that destination; a reader
            // opened with the File.ReadAllBytesAsync default share mode (Read only, no Delete)
            // makes a concurrent replace throw UnauthorizedAccessException. FileShare.Delete here
            // is what actually gives the "atomic replace never blocks a concurrent reader"
            // guarantee its name — without it, "atomic" secretly meant "atomic unless something
            // is reading it right now."
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            cipher = buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        var plain = _protector.Unprotect(cipher);
        try { return JsonSerializer.Deserialize<CredentialEnvelope>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private string StagedPath(Guid operationId) => Path.Combine(_root, "staged", operationId.ToString("N") + ".bin");
    private string CandidatePath(Guid operationId, string reference) => Path.Combine(_root, "candidates", operationId.ToString("N") + "-" + FileName(reference) + ".bin");
    private string ConfirmedPath(string reference) => Path.Combine(_root, FileName(reference) + ".bin");
    private static string FileName(string reference) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference))).ToLowerInvariant();
    private static ProtectedCredentialReceipt Receipt(CredentialEnvelope envelope) => new(envelope.OperationId, envelope.CredentialReference, envelope.Version, "local-read", DateTimeOffset.UtcNow);

    private static string ValidateRoot(MarketplaceCredentialStoreOptions options, IWebHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(options.CredentialStoreRoot)) throw new InvalidOperationException("Marketplaces:CredentialStoreRoot is required when the local credential store is enabled.");
        // Path.GetFullPath resolves a RELATIVE input against the current working directory,
        // silently turning it into something fully-qualified — checking IsPathFullyQualified
        // only AFTER that call can never observe a relative input, which is a real bypass of
        // "must be absolute". The raw configured value is what must already be absolute.
        if (!Path.IsPathFullyQualified(options.CredentialStoreRoot))
            throw new InvalidOperationException("Marketplace credential store root must be an absolute path.");
        var root = Path.GetFullPath(options.CredentialStoreRoot);
        var prohibited = new[] { environment.ContentRootPath, environment.WebRootPath, options.RepositoryRoot }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x!));
        if (prohibited.Any(p => IsSameOrDescendant(root, p) || IsSameOrDescendant(p, root)))
            throw new InvalidOperationException("Marketplace credential store root must be absolute and outside repository/content/wwwroot paths.");
        return root;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="ancestor"/> or lies
    /// under it — boundary-aware (a plain StartsWith would wrongly treat sibling directories that
    /// merely share a string prefix, e.g. "C:\Foo" and "C:\FooBar", as overlapping).</summary>
    private static bool IsSameOrDescendant(string path, string ancestor)
    {
        var normalizedAncestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CredentialEnvelope(Guid OperationId, Guid? SessionId, Guid? AccountId, string ProviderCode, string CredentialReference, long Version, string Secret);
}
