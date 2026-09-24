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
[JsonConverter(typeof(JsonStringEnumConverter<MarketplaceConnectionState>))]
public enum MarketplaceConnectionState { NOT_CONFIGURED, DISCONNECTED, CONNECTED, ERROR }
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
    { ProviderCode=MarketplaceProvider.RequiredCode(providerCode); ExternalAccountId=MarketplaceProvider.Required(externalAccountId,1,200); if(salesChannelId==Guid.Empty) throw new ArgumentException("MARKETPLACE_ACCOUNT_CHANNEL_REQUIRED"); SalesChannelId=salesChannelId; DisplayName=MarketplaceProvider.Required(displayName,2,200); CredentialReference=Optional(credentialReference,300); Active=true; }
    public string ProviderCode {get;private set;}=string.Empty; public string ExternalAccountId {get;private set;}=string.Empty; public Guid SalesChannelId {get;private set;} public string DisplayName {get;private set;}=string.Empty; public bool Active {get;private set;} public string? CredentialReference {get;private set;} public MarketplaceConnectionState ConnectionState {get;private set;}=MarketplaceConnectionState.NOT_CONFIGURED; public MarketplaceSyncState SyncState {get;private set;}=MarketplaceSyncState.NEVER_SYNCED; public DateTimeOffset? LastSyncAttemptAt {get;private set;} public DateTimeOffset? LastSuccessfulSyncAt {get;private set;} public DateTimeOffset? LastFailureAt {get;private set;} public string? LastError {get;private set;} public IReadOnlyList<MarketplaceAccountCapability> Capabilities=>_capabilities.AsReadOnly();
    public void Update(string displayName,string? credentialReference){DisplayName=MarketplaceProvider.Required(displayName,2,200);CredentialReference=Optional(credentialReference,300);}
    public void RejectSalesChannelReassignment(Guid salesChannelId){if(salesChannelId!=SalesChannelId) throw new ArgumentException("MARKETPLACE_ACCOUNT_CHANNEL_IMMUTABLE");}
    public void Activate()=>Active=true; public void Deactivate()=>Active=false;
    public void RecordRuntimeFailure(string error,DateTimeOffset now){ConnectionState=MarketplaceConnectionState.ERROR;SyncState=MarketplaceSyncState.ERROR;LastSyncAttemptAt=now;LastFailureAt=now;LastError=Optional(error,1000)??"RUNTIME_FAILURE";}
    public void SetCapability(string capability,AccountCapabilityState state){var code=MarketplaceProviderCapability.CapabilityCodeValue(capability);var current=_capabilities.SingleOrDefault(x=>x.CapabilityCode==code);if(current is null)_capabilities.Add(new MarketplaceAccountCapability(Id,code,state));else current.SetState(state);}
    private static string? Optional(string? value,int max){if(string.IsNullOrWhiteSpace(value))return null;var v=value.Trim();if(v.Length>max)throw new ArgumentException("FIELD_TOO_LONG");return v;}
}
public sealed class MarketplaceAccountCapability : IOwnedBy<MarketplaceAccount>, IJoinTable
{ private MarketplaceAccountCapability(){} internal MarketplaceAccountCapability(Guid accountId,string capability,AccountCapabilityState state){MarketplaceAccountId=accountId;CapabilityCode=capability;State=state;} public Guid MarketplaceAccountId{get;private set;} public Guid ParentId=>MarketplaceAccountId; public string CapabilityCode{get;private set;}=string.Empty; public AccountCapabilityState State{get;private set;} public DateTimeOffset UpdatedAt{get;private set;}=DateTimeOffset.UtcNow; public DateTimeOffset? VerifiedAt{get;private set;} internal void SetState(AccountCapabilityState state){State=state;UpdatedAt=DateTimeOffset.UtcNow;} }

[Auditable]
public sealed class MarketplaceListing : AggregateRoot
{
    private readonly List<MarketplaceListingObservation> _observations=[]; private readonly List<MarketplaceListingTag> _tags=[];
    private MarketplaceListing() { }
    public MarketplaceListing(Guid accountId,string externalListingId,string? externalSku,MarketplaceListingStatus observedStatus, string? title=null, decimal? observedPrice=null)
    {if(accountId==Guid.Empty)throw new ArgumentException("MARKETPLACE_LISTING_ACCOUNT_REQUIRED");MarketplaceAccountId=accountId;ExternalListingId=MarketplaceProvider.Required(externalListingId,1,200);ExternalSku=Optional(externalSku,200);TitleSnapshot=Optional(title,500);SetObserved(observedStatus,observedPrice);LinkageState=MarketplaceLinkageState.UNLINKED;var now=DateTimeOffset.UtcNow;ApplyObservation("initial:"+ExternalListingId, SafeFingerprint(ExternalSku,TitleSnapshot,ObservedStatus,ObservedPrice,ProviderNativeStatus), ObservationProvenance.MANUAL, now, now, ObservedStatus, ObservedPrice, ExternalSku, TitleSnapshot, ProviderNativeStatus);}
    public Guid MarketplaceAccountId{get;private set;} public string ExternalListingId{get;private set;}=string.Empty; public string? ExternalSku{get;private set;} public Guid? ProductId{get;private set;} public Guid? ChannelOfferId{get;private set;} public string? TitleSnapshot{get;private set;} public decimal? ObservedPrice{get;private set;} public string? ListingUrl{get;private set;} public MarketplaceListingStatus ObservedStatus{get;private set;} public string? ProviderNativeStatus{get;private set;} public MarketplaceLinkageState LinkageState{get;private set;} public MarketplaceSyncState SyncState{get;private set;}=MarketplaceSyncState.NEVER_SYNCED; public DateTimeOffset? ProviderObservedAt{get;private set;} public DateTimeOffset? LastSyncAttemptAt{get;private set;} public DateTimeOffset? LastSuccessfulSyncAt{get;private set;} public string? SyncError{get;private set;} public IReadOnlyList<MarketplaceListingObservation> Observations=>_observations.AsReadOnly(); public IReadOnlyList<MarketplaceListingTag> Tags=>_tags.AsReadOnly();
    public void Link(Guid productId, Guid? offerId, Guid? offerProductId, Guid? offerSalesChannelId, Guid accountSalesChannelId){if(productId==Guid.Empty)throw new ArgumentException("LISTING_PRODUCT_REQUIRED");if(offerId is not null && (offerProductId!=productId||offerSalesChannelId!=accountSalesChannelId))throw new ArgumentException("LISTING_OFFER_MISMATCH");ProductId=productId;ChannelOfferId=offerId;LinkageState=MarketplaceLinkageState.LINKED;}
    public void MarkNeedsReview(){ProductId=null;ChannelOfferId=null;LinkageState=MarketplaceLinkageState.NEEDS_REVIEW;}
    public void Unlink(){ProductId=null;ChannelOfferId=null;LinkageState=MarketplaceLinkageState.UNLINKED;}
    public void SetObserved(MarketplaceListingStatus status,decimal? price){if(price is <=0)throw new ArgumentException("LISTING_OBSERVED_PRICE_INVALID");ObservedStatus=status;ObservedPrice=price;}
    public bool ApplyObservation(string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset observedAt,DateTimeOffset ingestedAt,MarketplaceListingStatus status,decimal? price,string? externalSku=null,string? title=null,string? providerNativeStatus=null)
    {var existing=_observations.SingleOrDefault(x=>x.ObservationKey==key);if(existing is not null){if(existing.Fingerprint!=fingerprint)throw new ArgumentException("LISTING_OBSERVATION_KEY_CONFLICT");return false;}if(_observations.LastOrDefault()?.Fingerprint==fingerprint)return false;SetObserved(status,price);ExternalSku=Optional(externalSku,200)??ExternalSku;TitleSnapshot=Optional(title,500)??TitleSnapshot;ProviderNativeStatus=Optional(providerNativeStatus,200)??ProviderNativeStatus;ProviderObservedAt=observedAt;_observations.Add(new MarketplaceListingObservation(Id,key,fingerprint,provenance,observedAt,ingestedAt,ExternalSku,TitleSnapshot,ObservedStatus,ObservedPrice,ProviderNativeStatus));return true;}
    public bool AddObservation(string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset observedAt,DateTimeOffset ingestedAt)
    { if (_observations.Any(x => x.ObservationKey == key)) return false; return ApplyObservation(key,fingerprint,provenance,observedAt,ingestedAt,ObservedStatus,ObservedPrice,ExternalSku,TitleSnapshot,ProviderNativeStatus); }
    private static string SafeFingerprint(string? sku,string? title,MarketplaceListingStatus status,decimal? price,string? native)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",sku??string.Empty,title??string.Empty,status,price?.ToString(System.Globalization.CultureInfo.InvariantCulture)??string.Empty,native??string.Empty)))).ToLowerInvariant();
    private static string? Optional(string? value,int max){if(string.IsNullOrWhiteSpace(value))return null;var v=value.Trim();if(v.Length>max)throw new ArgumentException("FIELD_TOO_LONG");return v;}
}
public sealed class MarketplaceListingObservation : Entity, IOwnedBy<MarketplaceListing> { private MarketplaceListingObservation(){} internal MarketplaceListingObservation(Guid id,string key,string fingerprint,ObservationProvenance provenance,DateTimeOffset observedAt,DateTimeOffset ingestedAt,string? externalSku,string? title,MarketplaceListingStatus status,decimal? price,string? native){MarketplaceListingId=id;ObservationKey=MarketplaceProvider.Required(key,1,200);Fingerprint=MarketplaceProvider.Required(fingerprint,1,128);Provenance=provenance;ProviderObservedAt=observedAt;IngestedAt=ingestedAt;ExternalSku=externalSku;TitleSnapshot=title;ObservedStatus=status;ObservedPrice=price;ProviderNativeStatus=native;} public Guid MarketplaceListingId{get;private set;} public Guid ParentId=>MarketplaceListingId;public string? ExternalSku{get;private set;} public string? TitleSnapshot{get;private set;} public decimal? ObservedPrice{get;private set;} public MarketplaceListingStatus ObservedStatus{get;private set;} public string? ProviderNativeStatus{get;private set;} public string ObservationKey{get;private set;}=string.Empty;public string Fingerprint{get;private set;}=string.Empty;public ObservationProvenance Provenance{get;private set;} public DateTimeOffset ProviderObservedAt{get;private set;} public DateTimeOffset IngestedAt{get;private set;} }

[Auditable]
public sealed class ProductCommercialProfile : AggregateRoot { private readonly List<ProductCommercialImage> _images=[];private readonly List<ProductCommercialTag> _tags=[];private ProductCommercialProfile(){}public ProductCommercialProfile(Guid productId){if(productId==Guid.Empty)throw new ArgumentException("COMMERCIAL_PROFILE_PRODUCT_REQUIRED");ProductId=productId;}public Guid ProductId{get;private set;}public IReadOnlyList<ProductCommercialImage> Images=>_images.AsReadOnly();public IReadOnlyList<ProductCommercialTag> Tags=>_tags.AsReadOnly();public void AddImage(Guid brandAssetId,CommercialImageRole role,int sortOrder,string? altText){if(brandAssetId==Guid.Empty)throw new ArgumentException("COMMERCIAL_IMAGE_ASSET_REQUIRED");if(sortOrder<0)throw new ArgumentException("COMMERCIAL_IMAGE_SORT_ORDER_INVALID");if(_images.Any(x=>x.BrandAssetId==brandAssetId))throw new ArgumentException("COMMERCIAL_IMAGE_ASSET_DUPLICATE");if(role==CommercialImageRole.PRIMARY&&_images.Any(x=>x.Role==role))throw new ArgumentException("COMMERCIAL_IMAGE_PRIMARY_EXISTS");_images.Add(new ProductCommercialImage(Id,brandAssetId,role,sortOrder,altText));}public void RemoveImage(Guid imageId){var image=_images.SingleOrDefault(x=>x.Id==imageId)??throw new InvalidOperationException("COMMERCIAL_IMAGE_NOT_FOUND");_images.Remove(image);}public void AssignTag(Guid tagId){if(tagId==Guid.Empty)throw new ArgumentException("COMMERCIAL_TAG_REQUIRED");if(_tags.Any(x=>x.CommercialTagId==tagId))return;_tags.Add(new ProductCommercialTag(Id,tagId));}public void RemoveTag(Guid tagId){var tag=_tags.SingleOrDefault(x=>x.CommercialTagId==tagId);if(tag is not null)_tags.Remove(tag);}}
public sealed class ProductCommercialImage : Entity, IOwnedBy<ProductCommercialProfile>{private ProductCommercialImage(){}internal ProductCommercialImage(Guid profileId,Guid assetId,CommercialImageRole role,int sort,string? alt){ProductCommercialProfileId=profileId;BrandAssetId=assetId;Role=role;SortOrder=sort;AltText=string.IsNullOrWhiteSpace(alt)?null:alt.Trim();}public Guid ProductCommercialProfileId{get;private set;}public Guid ParentId=>ProductCommercialProfileId;public Guid BrandAssetId{get;private set;}public CommercialImageRole Role{get;private set;}public int SortOrder{get;private set;}public string? AltText{get;private set;}}
[Auditable]
public sealed class CommercialTag:AggregateRoot{private CommercialTag(){}public CommercialTag(string code,string name){Code=MarketplaceProvider.RequiredCode(code);Name=MarketplaceProvider.Required(name,2,100);Active=true;}public string Code{get;private set;}=string.Empty;public string Name{get;private set;}=string.Empty;public bool Active{get;private set;}public void Update(string name,bool active){Name=MarketplaceProvider.Required(name,2,100);Active=active;}}
public sealed class ProductCommercialTag:IOwnedBy<ProductCommercialProfile>,IJoinTable{private ProductCommercialTag(){}public ProductCommercialTag(Guid profileId,Guid tagId){ProductCommercialProfileId=profileId;CommercialTagId=tagId;}public Guid ProductCommercialProfileId{get;private set;}public Guid ParentId=>ProductCommercialProfileId;public Guid CommercialTagId{get;private set;}}
public sealed class MarketplaceListingTag:IOwnedBy<MarketplaceListing>,IJoinTable{private MarketplaceListingTag(){}public MarketplaceListingTag(Guid listingId,Guid tagId){MarketplaceListingId=listingId;CommercialTagId=tagId;}public Guid MarketplaceListingId{get;private set;}public Guid ParentId=>MarketplaceListingId;public Guid CommercialTagId{get;private set;}}
