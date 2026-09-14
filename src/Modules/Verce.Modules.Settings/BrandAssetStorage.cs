using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Verce.Modules.Settings;

/// <summary>ADR-0015 §6 / SECURITY §8.1 implementation. The caller never gets a user-owned
/// filesystem path: only a validated, canonical content-addressed key.</summary>
public sealed class BrandAssetStorage
{
    private readonly string _root;
    private readonly AppSettingValueReader _settings;
    public BrandAssetStorage(IOptions<BrandAssetStorageOptions> options, AppSettingValueReader settings)
    {
        _root = Path.GetFullPath(options.Value.StorageRoot);
        _settings = settings;
    }
    public async Task<StoredFile> StoreAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var maxBytes = await _settings.GetMaximumImageBytesAsync(cancellationToken);
        if (file is null || file.Length <= 0 || file.Length > maxBytes) throw new BrandAssetUploadException("UPLOAD_SIZE_INVALID");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new BrandAssetUploadException("UPLOAD_EXTENSION_INVALID");
        await using var source = file.OpenReadStream();
        using var raw = new MemoryStream();
        await source.CopyToAsync(raw, cancellationToken);
        var bytes = raw.ToArray();
        var contentType = DetectContentType(bytes) ?? throw new BrandAssetUploadException("UPLOAD_MAGIC_BYTES_INVALID");
        var canonicalExtension = contentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => throw new BrandAssetUploadException("UPLOAD_CONTENT_TYPE_INVALID") };
        if (!ExtensionMatches(ext, contentType)) throw new BrandAssetUploadException("UPLOAD_TYPE_MISMATCH");
        await using var imageInput = new MemoryStream(bytes, writable: false);
        Image<Rgba32> image;
        try
        {
            image = await Image.LoadAsync<Rgba32>(imageInput, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidImageContentException or UnknownImageFormatException)
        {
            throw new BrandAssetUploadException("UPLOAD_DECODE_INVALID");
        }
        using (image)
        {
            if (image.Width is <= 0 or > 8000 || image.Height is <= 0 or > 8000) throw new BrandAssetUploadException("UPLOAD_DIMENSIONS_INVALID");
            await using var encoded = new MemoryStream();
            switch (contentType)
            {
                case "image/png": await image.SaveAsync(encoded, new PngEncoder(), cancellationToken); break;
                case "image/jpeg": await image.SaveAsync(encoded, new JpegEncoder(), cancellationToken); break;
                case "image/webp": await image.SaveAsync(encoded, new WebpEncoder(), cancellationToken); break;
            }
            var storedBytes = encoded.ToArray();
            if (storedBytes.LongLength > maxBytes) throw new BrandAssetUploadException("UPLOAD_SIZE_INVALID");
            var hash = Convert.ToHexString(SHA256.HashData(storedBytes)).ToLowerInvariant();
            var key = Path.Combine(hash[..2], hash + canonicalExtension).Replace('\\', '/');
            var fullPath = Path.Combine(_root, hash[..2], hash + canonicalExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var created = false;
            if (!File.Exists(fullPath))
            {
                var temporaryPath = fullPath + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, storedBytes, cancellationToken);
                    try { File.Move(temporaryPath, fullPath, overwrite: false); created = true; }
                    catch (IOException) when (File.Exists(fullPath)) { }
                }
                finally
                {
                    // Request-owned temporary files are always safe to remove. Canonical hashes
                    // are never compensated inline because another transaction may reference one.
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            var originalFileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
            return new StoredFile(new StoredAssetUpload(key, hash, contentType, storedBytes.LongLength, image.Width, image.Height, originalFileName), fullPath, created);
        }
    }
    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(storageKey)) throw new BrandAssetUploadException("ASSET_PATH_INVALID");
        var fullPath = Path.GetFullPath(Path.Combine(_root, storageKey));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) throw new FileNotFoundException();
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }
    private static bool ExtensionMatches(string extension, string contentType) => contentType switch { "image/png" => extension == ".png", "image/jpeg" => extension is ".jpg" or ".jpeg", "image/webp" => extension == ".webp", _ => false };
    private static string? DetectContentType(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
public sealed record StoredFile(StoredAssetUpload Upload, string FullPath, bool Created);
public sealed class BrandAssetUploadException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class BrandAssetStorageOptions
{
    public string StorageRoot { get; set; } = string.Empty;
}
