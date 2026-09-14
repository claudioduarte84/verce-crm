using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Settings;

[Auditable]
public sealed class CompanyProfile : AggregateRoot
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");
    private CompanyProfile() : base(SingletonId) { }
    public CompanyProfile(CompanyProfileValues values) : base(SingletonId) => Update(values);
    public string LegalName { get; private set; } = string.Empty; public string TradeName { get; private set; } = string.Empty;
    public string? Document { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Website { get; private set; }
    public string? Instagram { get; private set; }
    public string? WhatsApp { get; private set; }
    public string? ZipCode { get; private set; }
    public string? Street { get; private set; }
    public string? Number { get; private set; }
    public string? Complement { get; private set; }
    public string? District { get; private set; }
    public string? City { get; private set; }
    public string? State { get; private set; }
    public string Country { get; private set; } = "BR"; public string Timezone { get; private set; } = "America/Sao_Paulo"; public string Currency { get; private set; } = "BRL";
    public void Update(CompanyProfileValues value)
    {
        LegalName = Required(value.LegalName, 2, 200); TradeName = Required(value.TradeName, 2, 200); Document = Optional(value.Document, 50); Email = Optional(value.Email, 320); Phone = Optional(value.Phone, 50); Website = Optional(value.Website, 2048); Instagram = Optional(value.Instagram, 200); WhatsApp = Optional(value.WhatsApp, 50); ZipCode = Optional(value.ZipCode, 20); Street = Optional(value.Street, 200); Number = Optional(value.Number, 30); Complement = Optional(value.Complement, 200); District = Optional(value.District, 100); City = Optional(value.City, 100); State = value.State is null ? null : Required(value.State, 2, 2).ToUpperInvariant(); Country = Required(value.Country ?? "BR", 2, 2).ToUpperInvariant(); Timezone = Required(value.Timezone ?? "America/Sao_Paulo", 1, 100); Currency = Required(value.Currency ?? "BRL", 3, 3).ToUpperInvariant();
    }
    internal static string Required(string? value, int min, int max) { var v = value?.Trim() ?? string.Empty; if (v.Length < min || v.Length > max) throw new ArgumentException("Invalid company profile value."); return v; }
    internal static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("Value is too long.");
}
public sealed record CompanyProfileValues(string LegalName, string TradeName, string? Document, string? Email, string? Phone, string? Website, string? Instagram, string? WhatsApp, string? ZipCode, string? Street, string? Number, string? Complement, string? District, string? City, string? State, string? Country, string? Timezone, string? Currency);

public enum AppSettingValueType { String, Int, Decimal, Bool, Json }
[Auditable]
public sealed class AppSetting : AggregateRoot
{
    private AppSetting() { }
    public AppSetting(string key, string value, AppSettingValueType valueType, string scope, string description, bool isSecret = false) { Key = CompanyProfile.Required(key, 1, 200); SetValue(value, valueType); Scope = CompanyProfile.Required(scope, 1, 100); Description = CompanyProfile.Required(description, 1, 500); IsSecret = isSecret; }
    public string Key { get; private set; } = string.Empty; public string Value { get; private set; } = string.Empty; public AppSettingValueType ValueType { get; private set; }
    public string Scope { get; private set; } = string.Empty; public string Description { get; private set; } = string.Empty; public bool IsSecret { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public void Update(string value, Guid actorId, DateTimeOffset now) { SetValue(value, ValueType); UpdatedBy = actorId; UpdatedAt = now; }
    private void SetValue(string value, AppSettingValueType type) { Value = SettingCatalog.Validate(Key, value, type); ValueType = type; }
}

public sealed class BrandAssetType : IReferenceData { public string Code { get; private set; } = string.Empty; public string Name { get; private set; } = string.Empty; public bool IsActive { get; private set; } = true; private BrandAssetType() { } public BrandAssetType(string code, string name) { Code = CompanyProfile.Required(code, 1, 64); Name = CompanyProfile.Required(name, 1, 100); } }
[Auditable]
public sealed class BrandAsset : AggregateRoot
{
    private readonly List<BrandAssetVersion> _versions = [];
    private BrandAsset() { }
    public BrandAsset(string typeCode, string name) { BrandAssetTypeCode = CompanyProfile.Required(typeCode, 1, 64); Name = CompanyProfile.Required(name, 1, 200); }
    public string BrandAssetTypeCode { get; private set; } = string.Empty; public string Name { get; private set; } = string.Empty; public Guid? CurrentVersionId { get; private set; }
    public bool IsActive { get; private set; } = true; public DateTimeOffset? DeletedAt { get; private set; }
    public IReadOnlyCollection<BrandAssetVersion> Versions => _versions.AsReadOnly();
    public BrandAssetVersion AddVersion(StoredAssetUpload upload, Guid? uploadedBy, DateTimeOffset now)
    {
        var version = new BrandAssetVersion(Id, _versions.Count == 0 ? 1 : _versions.Max(x => x.VersionNumber) + 1, upload, uploadedBy, now);
        foreach (var existing in _versions) existing.ClearCurrent(); version.MakeCurrent(); _versions.Add(version); CurrentVersionId = version.Id; return version;
    }
    public void ActivateVersion(Guid versionId) { var version = _versions.SingleOrDefault(x => x.Id == versionId) ?? throw new InvalidOperationException("BRAND_ASSET_VERSION_NOT_FOUND"); foreach (var item in _versions) item.ClearCurrent(); version.MakeCurrent(); CurrentVersionId = version.Id; }
    public void Deactivate() => IsActive = false; public void SoftDelete(DateTimeOffset now) { DeletedAt = now; IsActive = false; }
}
[Auditable]
public sealed class BrandAssetVersion : Entity, IOwnedBy<BrandAsset>
{
    private BrandAssetVersion() { }
    internal BrandAssetVersion(Guid brandAssetId, int versionNumber, StoredAssetUpload upload, Guid? uploadedBy, DateTimeOffset uploadedAt) { BrandAssetId = brandAssetId; VersionNumber = versionNumber; FilePath = upload.FilePath; Sha256 = upload.Sha256; ContentType = upload.ContentType; FileSizeBytes = upload.FileSizeBytes; WidthPx = upload.WidthPx; HeightPx = upload.HeightPx; OriginalFileName = upload.OriginalFileName; UploadedBy = uploadedBy; UploadedAt = uploadedAt; }
    public Guid BrandAssetId { get; private set; }
    public Guid ParentId => BrandAssetId; public int VersionNumber { get; private set; }
    public string FilePath { get; private set; } = string.Empty; public string Sha256 { get; private set; } = string.Empty; public string ContentType { get; private set; } = string.Empty; public long FileSizeBytes { get; private set; }
    public int WidthPx { get; private set; }
    public int HeightPx { get; private set; }
    public string OriginalFileName { get; private set; } = string.Empty; public DateTimeOffset UploadedAt { get; private set; }
    public Guid? UploadedBy { get; private set; }
    public bool IsCurrent { get; private set; }
    internal void MakeCurrent() => IsCurrent = true; internal void ClearCurrent() => IsCurrent = false;
}
public sealed record StoredAssetUpload(string FilePath, string Sha256, string ContentType, long FileSizeBytes, int WidthPx, int HeightPx, string OriginalFileName);
[Auditable]
public sealed class BrandingAssignment : AggregateRoot { private BrandingAssignment() { } public BrandingAssignment(string role, Guid brandAssetId) { Role = CompanyProfile.Required(role, 1, 64); BrandAssetId = brandAssetId; } public string Role { get; private set; } = string.Empty; public Guid BrandAssetId { get; private set; } public void Assign(Guid assetId) => BrandAssetId = assetId; }
