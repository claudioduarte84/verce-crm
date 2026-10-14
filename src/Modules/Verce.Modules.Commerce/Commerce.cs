using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Commerce;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelOfferStatus>))]
public enum ChannelOfferStatus { ACTIVE, INACTIVE }
[JsonConverter(typeof(JsonStringEnumConverter<ChannelOfferPriceSource>))]
public enum ChannelOfferPriceSource { PRICING_ENGINE, MANUAL, IMPORTED_OBSERVED }
[JsonConverter(typeof(JsonStringEnumConverter<ProviderCapabilityState>))]
public enum ProviderCapabilityState { UNKNOWN, SUPPORTED, UNSUPPORTED }
[JsonConverter(typeof(JsonStringEnumConverter<AccountCapabilityState>))]
public enum AccountCapabilityState { UNKNOWN, GRANTED, DENIED }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAuthorizationState>))]
public enum MarketplaceAuthorizationState { NOT_CONNECTED, CONNECTED, REAUTHORIZATION_REQUIRED, REVOKED }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceRuntimeAvailability>))]
public enum MarketplaceRuntimeAvailability { UNKNOWN, AVAILABLE, UNAVAILABLE }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAuthorizationSessionStatus>))]
public enum MarketplaceAuthorizationSessionStatus { PENDING, CLAIMED, COMPLETED, FAILED, EXPIRED, REVOKED }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAccountOperationKind>))]
public enum MarketplaceAccountOperationKind { CONNECT_NEW, RECONNECT, REFRESH, DISCONNECT }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAccountOperationPhase>))]
public enum MarketplaceAccountOperationPhase { PREPARED, EXTERNAL_IN_FLIGHT, SECRET_PERSISTED }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAccountOperationDecision>))]
public enum MarketplaceAccountOperationDecision { PENDING, CONFIRMED, FAIL_CLOSED }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAccountOperationCleanupState>))]
public enum MarketplaceAccountOperationCleanupState { NOT_REQUIRED, PENDING, DONE }
[JsonConverter(typeof(JsonStringEnumConverter<CredentialImpact>))]
public enum CredentialImpact { UNKNOWN, PRESERVES_EXISTING, MAY_SUPERSEDE_EXISTING }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceSyncState>))]
public enum MarketplaceSyncState { NEVER_SYNCED, SYNCED, ERROR }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingStatus>))]
public enum MarketplaceListingStatus { DRAFT, ACTIVE, PAUSED, INACTIVE, ERROR }
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceLinkageState>))]
public enum MarketplaceLinkageState { UNLINKED, NEEDS_REVIEW, LINKED }
[JsonConverter(typeof(JsonStringEnumConverter<ObservationProvenance>))]
public enum ObservationProvenance { MANUAL, IMPORTED, PROVIDER_SYNC }
[JsonConverter(typeof(JsonStringEnumConverter<CommercialImageRole>))]
public enum CommercialImageRole { PRIMARY, GALLERY }
[JsonConverter(typeof(JsonStringEnumConverter<SalesMetricCoverage>))]
public enum SalesMetricCoverage { COMPLETE, PARTIAL, UNKNOWN }

public static class CommercePolicy
{
    public static bool IsCommerciallyActive(bool productActive, IEnumerable<ChannelOfferStatus> offerStatuses) =>
        productActive && offerStatuses.Any(x => x == ChannelOfferStatus.ACTIVE);

    public static SalesMetricCoverage ResolveSalesCoverage(bool directChannel, bool hasKnownSales) =>
        directChannel ? SalesMetricCoverage.COMPLETE : hasKnownSales ? SalesMetricCoverage.PARTIAL : SalesMetricCoverage.UNKNOWN;

    public static bool HasEffectiveCapability(ProviderCapabilityState provider, AccountCapabilityState account, bool accountActive) =>
        accountActive && provider == ProviderCapabilityState.SUPPORTED && account == AccountCapabilityState.GRANTED;

    /// <summary>Business execution is deliberately stricter than control-plane authorization/probe work.</summary>
    public static bool HasEffectiveExecution(ProviderCapabilityState provider, AccountCapabilityState grant, bool accountActive,
        bool salesChannelExistsAndActiveMarketplace, MarketplaceAuthorizationState authorization, MarketplaceRuntimeAvailability runtime,
        bool providerFenceClear, bool confirmedCredentialResolvable) =>
        HasEffectiveCapability(provider, grant, accountActive) && salesChannelExistsAndActiveMarketplace &&
        authorization == MarketplaceAuthorizationState.CONNECTED && runtime != MarketplaceRuntimeAvailability.UNAVAILABLE &&
        providerFenceClear && confirmedCredentialResolvable;
}

[Auditable]
public sealed class ChannelOffer : AggregateRoot
{
    private ChannelOffer() { }
    public ChannelOffer(Guid productId, Guid salesChannelId, decimal intendedUnitPrice, ChannelOfferPriceSource priceSource, decimal? sellerPaidShippingAmount = null)
    {
        if (productId == Guid.Empty || salesChannelId == Guid.Empty) throw new ArgumentException("CHANNEL_OFFER_REFERENCE_REQUIRED");
        ProductId = productId; SalesChannelId = salesChannelId; SetCommercialTerms(intendedUnitPrice, priceSource, sellerPaidShippingAmount); Status = ChannelOfferStatus.INACTIVE;
    }
    public Guid ProductId { get; private set; }
    public Guid SalesChannelId { get; private set; }
    public ChannelOfferStatus Status { get; private set; }
    public decimal IntendedUnitPrice { get; private set; }
    public ChannelOfferPriceSource PriceSource { get; private set; }
    public decimal? SellerPaidShippingAmount { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public void SetCommercialTerms(decimal price, ChannelOfferPriceSource source, decimal? shipping) { if(price <= 0) throw new ArgumentException("CHANNEL_OFFER_PRICE_INVALID"); if(shipping < 0) throw new ArgumentException("CHANNEL_OFFER_SHIPPING_INVALID"); IntendedUnitPrice=price; PriceSource=source; SellerPaidShippingAmount=shipping; }
    public void Activate(DateTimeOffset now) { Status=ChannelOfferStatus.ACTIVE; ActivatedAt=now; DeactivatedAt=null; }
    public void Deactivate(DateTimeOffset now) { Status=ChannelOfferStatus.INACTIVE; DeactivatedAt=now; }
}

public sealed class MarketplaceProvider : IReferenceData
{
    private MarketplaceProvider() { Code = string.Empty; Name = string.Empty; }
    public MarketplaceProvider(string code, string name) { Code = RequiredCode(code); Name = Required(name, 2, 100); }
    public string Code { get; private set; }
    public string Name { get; private set; }
    internal static string RequiredCode(string value) => Required(value, 2, 64).ToUpperInvariant();
    internal static string Required(string? value, int min, int max) { var v=value?.Trim()??string.Empty; if(v.Length<min||v.Length>max) throw new ArgumentException("COMMERCE_TEXT_INVALID"); return v; }
}
public sealed class MarketplaceProviderCapability : IJoinTable
{
    private MarketplaceProviderCapability() { ProviderCode=CapabilityCode=string.Empty; }
    public MarketplaceProviderCapability(string providerCode,string capabilityCode,ProviderCapabilityState state=ProviderCapabilityState.UNKNOWN){ProviderCode=MarketplaceProvider.RequiredCode(providerCode);CapabilityCode=CapabilityCodeValue(capabilityCode);State=state;}
    public string ProviderCode {get;private set;} public string CapabilityCode {get;private set;} public ProviderCapabilityState State {get;private set;} public string Source {get;private set;}="MANUAL"; public DateTimeOffset? VerifiedAt {get;private set;} public DateTimeOffset UpdatedAt {get;private set;}=DateTimeOffset.UtcNow;
    public static string CapabilityCodeValue(string value) => value is "LISTINGS_READ" or "LISTINGS_WRITE" or "ORDERS_READ" or "FEES_QUOTE" or "ANALYTICS_READ" or "ADS_READ" or "SHIPPING_READ" or "INVENTORY_SYNC" ? value : throw new ArgumentException("COMMERCE_CAPABILITY_INVALID");
}

[Auditable]
public sealed class MarketplaceAccount : AggregateRoot
{
    private readonly List<MarketplaceAccountCapability> _capabilities=[];
    private MarketplaceAccount() { }
    public MarketplaceAccount(string providerCode, string externalAccountId, Guid salesChannelId, string displayName, string? credentialReference=null)
        : this(Guid.CreateVersion7(), providerCode, externalAccountId, salesChannelId, displayName, credentialReference) { }
    /// <summary>Used by the authorization workflow so the account identity written into the
    /// protected envelope is the aggregate identity that is later committed.</summary>
    public MarketplaceAccount(Guid id, string providerCode, string externalAccountId, Guid salesChannelId, string displayName, string? credentialReference=null)
        : base(id)
    { if (id == Guid.Empty) throw new ArgumentException("MARKETPLACE_ACCOUNT_ID_REQUIRED"); ProviderCode=MarketplaceProvider.RequiredCode(providerCode); ExternalAccountId=MarketplaceProvider.Required(externalAccountId,1,200); if(salesChannelId==Guid.Empty) throw new ArgumentException("MARKETPLACE_ACCOUNT_CHANNEL_REQUIRED"); SalesChannelId=salesChannelId; DisplayName=MarketplaceProvider.Required(displayName,2,200); if (!string.IsNullOrWhiteSpace(credentialReference)) throw new ArgumentException("MARKETPLACE_CREDENTIAL_REFERENCE_SERVER_MANAGED"); Active=true; Connection=MarketplaceAccountConnection.NotConnected(Id); }
    public string ProviderCode {get;private set;}=string.Empty; public string ExternalAccountId {get;private set;}=string.Empty; public Guid SalesChannelId {get;private set;} public string DisplayName {get;private set;}=string.Empty; public bool Active {get;private set;} public string? CredentialReference {get;private set;} public MarketplaceSyncState SyncState {get;private set;}=MarketplaceSyncState.NEVER_SYNCED; public DateTimeOffset? LastSyncAttemptAt {get;private set;} public DateTimeOffset? LastSuccessfulSyncAt {get;private set;} public MarketplaceAccountConnection Connection { get; private set; } = null!; public IReadOnlyList<MarketplaceAccountCapability> Capabilities=>_capabilities.AsReadOnly();
    public void Update(string displayName){DisplayName=MarketplaceProvider.Required(displayName,2,200);}
    public void RejectSalesChannelReassignment(Guid salesChannelId){if(salesChannelId!=SalesChannelId) throw new ArgumentException("MARKETPLACE_ACCOUNT_CHANNEL_IMMUTABLE");}
    public void Activate()=>Active=true; public void Deactivate()=>Active=false;
    public void RecordRuntimeFailure(string classification, DateTimeOffset now) => Connection.MarkUnavailable("TRANSIENT", classification, now);
    public void RecordRuntimeFailure(string classification, string safeCode, DateTimeOffset now, DateTimeOffset? retryAfterUntil = null) => Connection.MarkUnavailable(classification, safeCode, now, retryAfterUntil);
    public void MarkAvailable(DateTimeOffset now) => Connection.MarkAvailable(now);
    /// <summary>D-03: writes go through the aggregate root; a persisted provider Retry-After guard
    /// that probe/business execution honors across restarts, independent of a runtime transition.</summary>
    public void RecordRetryAfter(DateTimeOffset until, DateTimeOffset now) => Connection.RecordRetryAfter(until, now);
    public void SetCapability(string capability,AccountCapabilityState state, string source = "LEGACY_MANUAL", string? providerReasonCode = null, DateTimeOffset? verifiedAt = null){var code=MarketplaceProviderCapability.CapabilityCodeValue(capability);var current=_capabilities.SingleOrDefault(x=>x.CapabilityCode==code);if(current is null)_capabilities.Add(new MarketplaceAccountCapability(Id,code,state,source,providerReasonCode,verifiedAt));else current.SetState(state,source,providerReasonCode,verifiedAt);}
    // D-01: the provider-returned access-token expiry must actually be persisted on EVERY
    // confirmation path (initial callback AND refresh) or every use would immediately look
    // expired and force a refresh. `isRefresh` additionally stamps LastRefreshAt so refresh
    // metadata is distinguishable from an initial connect/reconnect confirmation.
    public void InstallConfirmedCredential(string reference, long version, Guid operationId, DateTimeOffset now, DateTimeOffset? accessExpiresAt = null, bool isRefresh = false) { CredentialReference=RequiredCredentialReference(reference); Connection.Confirm(operationId, version, now, accessExpiresAt, isRefresh); }
    public void RequireReauthorization(string safeCode, DateTimeOffset now) => Connection.RequireReauthorization(safeCode, now);
    public void Revoke(DateTimeOffset now) => Connection.Revoke(now);
    public void ClearCredential(DateTimeOffset now) { CredentialReference=null; Connection.ClearConfirmedCredential(now); }
    public void EnsureConnection() => Connection ??= MarketplaceAccountConnection.NotConnected(Id);
    private static string RequiredCredentialReference(string value){var v=Optional(value,300);return v ?? throw new ArgumentException("MARKETPLACE_CREDENTIAL_REFERENCE_REQUIRED");}
    private static string? Optional(string? value,int max){if(string.IsNullOrWhiteSpace(value))return null;var v=value.Trim();if(v.Length>max)throw new ArgumentException("FIELD_TOO_LONG");return v;}
}
public sealed class MarketplaceAccountCapability : IOwnedBy<MarketplaceAccount>, IJoinTable
{ private MarketplaceAccountCapability(){} internal MarketplaceAccountCapability(Guid accountId,string capability,AccountCapabilityState state,string source,string? providerReasonCode,DateTimeOffset? verifiedAt){MarketplaceAccountId=accountId;CapabilityCode=capability;SetState(state,source,providerReasonCode,verifiedAt);} public Guid MarketplaceAccountId{get;private set;} public Guid ParentId=>MarketplaceAccountId; public string CapabilityCode{get;private set;}=string.Empty; public AccountCapabilityState State{get;private set;} public string Source{get;private set;}="LEGACY_MANUAL"; public string? ProviderReasonCode{get;private set;} public DateTimeOffset UpdatedAt{get;private set;}=DateTimeOffset.UtcNow; public DateTimeOffset? VerifiedAt{get;private set;} internal void SetState(AccountCapabilityState state,string source="LEGACY_MANUAL",string? providerReasonCode=null,DateTimeOffset? verifiedAt=null){if(source.Length>32)throw new ArgumentException("MARKETPLACE_CAPABILITY_SOURCE_INVALID");if(providerReasonCode?.Length>64)throw new ArgumentException("MARKETPLACE_CAPABILITY_REASON_INVALID");State=state;Source=source;ProviderReasonCode=providerReasonCode;VerifiedAt=verifiedAt;UpdatedAt=DateTimeOffset.UtcNow;} }

public sealed record MarketplaceListingLinkageDecision(MarketplaceLinkageState State, Guid? ProductId, Guid? SkuMappingId);

public static class MarketplaceListingFingerprint
{
    public static string ComputeV2(NormalizedListingFacts facts) => ComputeV2(facts.ExternalSku, facts.Title, facts.ObservedPrice, facts.ObservedStatus, facts.ProviderNativeStatus, facts.VariationCount);
    public static string ComputeV2(string? sku, string? title, decimal? price, MarketplaceListingStatus status, string? native, int? variationCount)
    {
        static string Field(object? value) => value is null ? "-1:" : $"{value.ToString()!.Length}:{value}";
        var priceValue = price?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var payload = "v2|" + string.Concat(Field(sku), Field(title), Field(priceValue), Field(status), Field(native), Field(variationCount));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

[Auditable]
public sealed class MarketplaceListing : AggregateRoot
{
    private readonly List<MarketplaceListingObservation> _observations=[]; private readonly List<MarketplaceListingTag> _tags=[];
    private MarketplaceListing() { }
    public MarketplaceListing(Guid accountId,string externalListingId,string? externalSku,MarketplaceListingStatus observedStatus, string? title=null, decimal? observedPrice=null)
    { if(accountId==Guid.Empty)throw new ArgumentException("MARKETPLACE_LISTING_ACCOUNT_REQUIRED"); MarketplaceAccountId=accountId; ExternalListingId=MarketplaceProvider.Required(externalListingId,1,200); ExternalSku=Optional(externalSku,200); TitleSnapshot=Optional(title,500); SetObserved(observedStatus,observedPrice); LinkageState=MarketplaceLinkageState.UNLINKED; var now=DateTimeOffset.UtcNow; ApplyObservation("initial:"+ExternalListingId, MarketplaceListingFingerprint.ComputeV2(ExternalSku,TitleSnapshot,ObservedPrice,ObservedStatus,ProviderNativeStatus,VariationCount), ObservationProvenance.MANUAL, now, now, ObservedStatus, ObservedPrice, ExternalSku, TitleSnapshot, ProviderNativeStatus); }
    public static MarketplaceListing ImportFromProvider(Guid accountId, NormalizedListingFacts facts, Guid runId, MarketplaceListingLinkageDecision linkage, DateTimeOffset now)
    { var listing=new MarketplaceListing(accountId,facts.ExternalListingId,facts.ExternalSku,facts.ObservedStatus,facts.Title,facts.ObservedPrice); listing._observations.Clear(); listing.LatestObservationFingerprint=null; listing.ApplyLinkageDecision(linkage); listing.ApplyProviderFacts(facts,runId,now); return listing; }
    public Guid MarketplaceAccountId{get;private set;} public string ExternalListingId{get;private set;}=string.Empty; public string? ExternalSku{get;private set;} public Guid? ProductId{get;private set;} public Guid? ChannelOfferId{get;private set;} public string? TitleSnapshot{get;private set;} public decimal? ObservedPrice{get;private set;} public string? ListingUrl{get;private set;} public MarketplaceListingStatus ObservedStatus{get;private set;} public string? ProviderNativeStatus{get;private set;} public MarketplaceLinkageState LinkageState{get;private set;} public MarketplaceListingLinkageSource? LinkageSource{get;private set;} public Guid? SkuMappingId{get;private set;} public bool AutoLinkSuppressed{get;private set;} public string? LatestObservationFingerprint{get;private set;} public int? VariationCount{get;private set;} public MarketplaceSyncState SyncState{get;private set;}=MarketplaceSyncState.NEVER_SYNCED; public DateTimeOffset? ProviderObservedAt{get;private set;} public DateTimeOffset? LastSyncAttemptAt{get;private set;} public DateTimeOffset? LastSuccessfulSyncAt{get;private set;} public string? SyncError{get;private set;} public IReadOnlyList<MarketplaceListingObservation> Observations=>_observations.AsReadOnly(); public IReadOnlyList<MarketplaceListingTag> Tags=>_tags.AsReadOnly();
    public void Link(Guid productId, Guid? offerId, Guid? offerProductId, Guid? offerSalesChannelId, Guid accountSalesChannelId){if(productId==Guid.Empty)throw new ArgumentException("LISTING_PRODUCT_REQUIRED");if(offerId is not null && (offerProductId!=productId||offerSalesChannelId!=accountSalesChannelId))throw new ArgumentException("LISTING_OFFER_MISMATCH");ProductId=productId;ChannelOfferId=offerId;LinkageState=MarketplaceLinkageState.LINKED;LinkageSource=MarketplaceListingLinkageSource.MANUAL;SkuMappingId=null;AutoLinkSuppressed=false;}
    public void MarkNeedsReview(){ProductId=null;ChannelOfferId=null;LinkageState=MarketplaceLinkageState.NEEDS_REVIEW;LinkageSource=null;SkuMappingId=null;}
    public void Unlink(){ProductId=null;ChannelOfferId=null;LinkageState=MarketplaceLinkageState.UNLINKED;LinkageSource=null;SkuMappingId=null;AutoLinkSuppressed=true;}
    public void SetObserved(MarketplaceListingStatus status,decimal? price){if(price is <=0)throw new ArgumentException("LISTING_OBSERVED_PRICE_INVALID");ObservedStatus=status;ObservedPrice=price;}
    public bool ApplyProviderFacts(NormalizedListingFacts facts, Guid runId, DateTimeOffset now)
    { if(facts.ExternalListingId!=ExternalListingId)throw new ArgumentException("MARKETPLACE_LISTING_ID_MISMATCH"); var fingerprint=MarketplaceListingFingerprint.ComputeV2(facts); var appended=LatestObservationFingerprint!=fingerprint; ExternalSku=Optional(facts.ExternalSku,200); TitleSnapshot=Optional(facts.Title,500); SetObserved(facts.ObservedStatus,facts.ObservedPrice); ProviderNativeStatus=Optional(facts.ProviderNativeStatus,200); ListingUrl=Optional(facts.ListingUrl,2000); VariationCount=facts.VariationCount; ProviderObservedAt=facts.ProviderObservedAt; SyncState=MarketplaceSyncState.SYNCED; SyncError=null; LastSyncAttemptAt=LastSuccessfulSyncAt=now; if(appended){LatestObservationFingerprint=fingerprint;_observations.Add(new MarketplaceListingObservation(Id,$"ps:{runId:N}:{fingerprint}",fingerprint,ObservationProvenance.PROVIDER_SYNC,facts.ProviderObservedAt,now,ExternalSku,TitleSnapshot,ObservedStatus,ObservedPrice,ProviderNativeStatus,VariationCount));} return appended; }
    public void ApplyLinkageDecision(MarketplaceListingLinkageDecision decision)
    { if(LinkageState!=MarketplaceLinkageState.UNLINKED||AutoLinkSuppressed)return; if(decision.State==MarketplaceLinkageState.LINKED && decision.ProductId is not null&&decision.SkuMappingId is not null){ProductId=decision.ProductId;ChannelOfferId=null;LinkageState=MarketplaceLinkageState.LINKED;LinkageSource=MarketplaceListingLinkageSource.DETERMINISTIC_SKU;SkuMappingId=decision.SkuMappingId;} else if(decision.State==MarketplaceLinkageState.NEEDS_REVIEW)MarkNeedsReview(); }
    public void RecordProviderReadFailure(string safeCode,DateTimeOffset now){SyncState=MarketplaceSyncState.ERROR;SyncError=MarketplaceListingSyncKernel.RequiredSafeCode(safeCode);LastSyncAttemptAt=now;}
    /// <summary><c>latest_observation_fingerprint</c> is <c>char(64)</c> (ADR-0025 §H.3). The S8B manual-observation contract still accepts a client fingerprint of 1..128 characters,
    /// and the observation row keeps it whole; only a value that is exactly a 64-character (V2-shaped) fingerprint can be the dedupe marker. Anything else is "no marker" (unknown),
    /// never truncated, never blank-padded — the same rule the S8C.2 migration backfill applies.</summary>
    private static string? LatestFingerprintMarker(string fingerprint)=>fingerprint.Length==64?fingerprint:null;
    /// <summary>A V2-shaped (64-character) fingerprint dedupes against the persisted <c>latest_observation_fingerprint</c> marker (ADR-0025 §I). A client fingerprint that cannot live in that marker
    /// (S8B allows 1..128 characters) keeps the S8B rule: it is a duplicate only of the last observation of the loaded history.</summary>
    private bool IsConsecutiveDuplicate(string fingerprint)=>fingerprint.Length==64?LatestObservationFingerprint==fingerprint:_observations.LastOrDefault()?.Fingerprint==fingerprint;
    public bool ApplyObservation(string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset? observedAt,DateTimeOffset ingestedAt,MarketplaceListingStatus status,decimal? price,string? externalSku=null,string? title=null,string? providerNativeStatus=null)
    {var existing=_observations.SingleOrDefault(x=>x.ObservationKey==key);if(existing is not null){if(existing.Fingerprint!=fingerprint)throw new ArgumentException("LISTING_OBSERVATION_KEY_CONFLICT");return false;}if(IsConsecutiveDuplicate(fingerprint))return false;SetObserved(status,price);ExternalSku=Optional(externalSku,200);TitleSnapshot=Optional(title,500);ProviderNativeStatus=Optional(providerNativeStatus,200);ProviderObservedAt=observedAt;LatestObservationFingerprint=LatestFingerprintMarker(fingerprint);_observations.Add(new MarketplaceListingObservation(Id,key,fingerprint,provenance,observedAt,ingestedAt,ExternalSku,TitleSnapshot,ObservedStatus,ObservedPrice,ProviderNativeStatus,VariationCount));return true;}
    public bool AddObservation(string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset? observedAt,DateTimeOffset ingestedAt) => !_observations.Any(x=>x.ObservationKey==key)&&ApplyObservation(key,fingerprint,provenance,observedAt,ingestedAt,ObservedStatus,ObservedPrice,ExternalSku,TitleSnapshot,ProviderNativeStatus);
    private static string? Optional(string? value,int max){if(string.IsNullOrWhiteSpace(value))return null;var v=value.Trim();if(v.Length>max)throw new ArgumentException("FIELD_TOO_LONG");return v;}
}
public sealed class MarketplaceListingObservation : Entity, IOwnedBy<MarketplaceListing> { private MarketplaceListingObservation(){} internal MarketplaceListingObservation(Guid id,string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset? observedAt,DateTimeOffset ingestedAt,string? externalSku,string? title,MarketplaceListingStatus status,decimal? price,string? native,int? variationCount){MarketplaceListingId=id;ObservationKey=MarketplaceProvider.Required(key,1,200);Fingerprint=MarketplaceProvider.Required(fingerprint,1,128);Provenance=provenance;ProviderObservedAt=observedAt;IngestedAt=ingestedAt;ExternalSku=externalSku;TitleSnapshot=title;ObservedStatus=status;ObservedPrice=price;ProviderNativeStatus=native;VariationCount=variationCount;} public Guid MarketplaceListingId{get;private set;} public Guid ParentId=>MarketplaceListingId;public string? ExternalSku{get;private set;} public string? TitleSnapshot{get;private set;} public decimal? ObservedPrice{get;private set;} public MarketplaceListingStatus ObservedStatus{get;private set;} public string? ProviderNativeStatus{get;private set;} public string ObservationKey{get;private set;}=string.Empty;public string Fingerprint{get;private set;}=string.Empty;public ObservationProvenance Provenance{get;private set;} public DateTimeOffset? ProviderObservedAt{get;private set;} public DateTimeOffset IngestedAt{get;private set;} public int? VariationCount{get;private set;} }

[Auditable]
public sealed class ProductCommercialProfile : AggregateRoot { private readonly List<ProductCommercialImage> _images=[];private readonly List<ProductCommercialTag> _tags=[];private ProductCommercialProfile(){}public ProductCommercialProfile(Guid productId){if(productId==Guid.Empty)throw new ArgumentException("COMMERCIAL_PROFILE_PRODUCT_REQUIRED");ProductId=productId;}public Guid ProductId{get;private set;}public IReadOnlyList<ProductCommercialImage> Images=>_images.AsReadOnly();public IReadOnlyList<ProductCommercialTag> Tags=>_tags.AsReadOnly();public void AddImage(Guid brandAssetId,CommercialImageRole role,int sortOrder,string? altText){if(brandAssetId==Guid.Empty)throw new ArgumentException("COMMERCIAL_IMAGE_ASSET_REQUIRED");if(sortOrder<0)throw new ArgumentException("COMMERCIAL_IMAGE_SORT_ORDER_INVALID");if(_images.Any(x=>x.BrandAssetId==brandAssetId))throw new ArgumentException("COMMERCIAL_IMAGE_ASSET_DUPLICATE");if(role==CommercialImageRole.PRIMARY&&_images.Any(x=>x.Role==role))throw new ArgumentException("COMMERCIAL_IMAGE_PRIMARY_EXISTS");_images.Add(new ProductCommercialImage(Id,brandAssetId,role,sortOrder,altText));}public void RemoveImage(Guid imageId){var image=_images.SingleOrDefault(x=>x.Id==imageId)??throw new InvalidOperationException("COMMERCIAL_IMAGE_NOT_FOUND");_images.Remove(image);}public void AssignTag(Guid tagId){if(tagId==Guid.Empty)throw new ArgumentException("COMMERCIAL_TAG_REQUIRED");if(_tags.Any(x=>x.CommercialTagId==tagId))return;_tags.Add(new ProductCommercialTag(Id,tagId));}public void RemoveTag(Guid tagId){var tag=_tags.SingleOrDefault(x=>x.CommercialTagId==tagId);if(tag is not null)_tags.Remove(tag);}}
public sealed class ProductCommercialImage : Entity, IOwnedBy<ProductCommercialProfile>{private ProductCommercialImage(){}internal ProductCommercialImage(Guid profileId,Guid assetId,CommercialImageRole role,int sort,string? alt){ProductCommercialProfileId=profileId;BrandAssetId=assetId;Role=role;SortOrder=sort;AltText=string.IsNullOrWhiteSpace(alt)?null:alt.Trim();}public Guid ProductCommercialProfileId{get;private set;}public Guid ParentId=>ProductCommercialProfileId;public Guid BrandAssetId{get;private set;}public CommercialImageRole Role{get;private set;}public int SortOrder{get;private set;}public string? AltText{get;private set;}}
[Auditable]
public sealed class CommercialTag:AggregateRoot{private CommercialTag(){}public CommercialTag(string code,string name){Code=MarketplaceProvider.RequiredCode(code);Name=MarketplaceProvider.Required(name,2,100);Active=true;}public string Code{get;private set;}=string.Empty;public string Name{get;private set;}=string.Empty;public bool Active{get;private set;}public void Update(string name,bool active){Name=MarketplaceProvider.Required(name,2,100);Active=active;}}
public sealed class ProductCommercialTag:IOwnedBy<ProductCommercialProfile>,IJoinTable{private ProductCommercialTag(){}public ProductCommercialTag(Guid profileId,Guid tagId){ProductCommercialProfileId=profileId;CommercialTagId=tagId;}public Guid ProductCommercialProfileId{get;private set;}public Guid ParentId=>ProductCommercialProfileId;public Guid CommercialTagId{get;private set;}}
public sealed class MarketplaceListingTag:IOwnedBy<MarketplaceListing>,IJoinTable{private MarketplaceListingTag(){}public MarketplaceListingTag(Guid listingId,Guid tagId){MarketplaceListingId=listingId;CommercialTagId=tagId;}public Guid MarketplaceListingId{get;private set;}public Guid ParentId=>MarketplaceListingId;public Guid CommercialTagId{get;private set;}}
