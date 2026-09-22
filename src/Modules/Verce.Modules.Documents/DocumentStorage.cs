using Microsoft.Extensions.Options;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0016 §3: content-addressed storage at <c>{sha256[0:2]}/{sha256}.{ext}</c> — mirrors
/// <c>Verce.Modules.Settings.BrandAssetStorage</c>'s exact, already-reviewed pattern (atomic
/// temp-file-then-rename write, canonical-path read guard against traversal). Files are never
/// deleted (ADR-0016 §3) — no delete method exists here.
/// </summary>
public interface IDocumentStorage
{
    /// <summary>Writes <paramref name="bytes"/> under the content-addressed key for
    /// <paramref name="sha256"/>/<paramref name="extension"/> — a no-op if that exact key already
    /// exists (identical renders deduplicate naturally). Returns the storage key.</summary>
    Task<string> StoreAsync(byte[] bytes, string sha256, string extension, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken);
}

public sealed class DocumentStorageOptions
{
    public string StorageRoot { get; set; } = string.Empty;
}

public sealed class DocumentStorage : IDocumentStorage
{
    private readonly string _root;

    public DocumentStorage(IOptions<DocumentStorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StorageRoot);
    }

    public async Task<string> StoreAsync(byte[] bytes, string sha256, string extension, CancellationToken cancellationToken)
    {
        var key = Path.Combine(sha256[..2], sha256 + extension).Replace('\\', '/');
        var fullPath = Path.Combine(_root, sha256[..2], sha256 + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath))
        {
            var temporaryPath = fullPath + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                try { File.Move(temporaryPath, fullPath, overwrite: false); }
                catch (IOException) when (File.Exists(fullPath)) { /* another writer won the race for this identical hash */ }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        return key;
    }

    public Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(storageKey))
            throw new ArgumentException("DOCUMENT_STORAGE_KEY_INVALID");
        var fullPath = Path.GetFullPath(Path.Combine(_root, storageKey));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException();
        return File.ReadAllBytesAsync(fullPath, cancellationToken);
    }
}
