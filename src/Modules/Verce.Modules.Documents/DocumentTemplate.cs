using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Documents;

/// <summary>
/// S7/S14 scope authority gate, OPTION A (2026-09-21): a closed, system-owned enumeration of
/// document kinds the platform can render — QUOTE (S7), PRODUCTION_ORDER and SHIPPING_LABEL
/// (future sprints' resolvers/templates; only the identity row ships now). Mirrors
/// <see cref="Verce.Modules.Settings.BrandAssetType"/>'s exact Category 3 pattern (ADR-0011 §1).
/// </summary>
public sealed class DocumentType : IReferenceData
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    private DocumentType() { }

    public DocumentType(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("DOCUMENT_TYPE_CODE_REQUIRED");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("DOCUMENT_TYPE_NAME_REQUIRED");
        Code = code.Trim();
        Name = name.Trim();
    }
}

/// <summary>
/// ADR-0007 §7: a named template for one <see cref="DocumentType"/> — the default VERCE proposal
/// is ORDINARY DATA here, never compiled renderer code. Mirrors
/// <see cref="Verce.Modules.Settings.BrandAsset"/>'s exact master-data-owns-versions shape
/// (Category 2, ADR-0011 §1): <see cref="DocumentTemplate"/> is the stable identity an operator
/// (S14) or a seed (S7) names and marks default; <see cref="DocumentTemplateVersion"/> carries
/// the actual renderable content and is immutable once PUBLISHED.
/// </summary>
[Auditable]
public sealed class DocumentTemplate : AggregateRoot
{
    private readonly List<DocumentTemplateVersion> _versions = [];

    private DocumentTemplate() { }

    public DocumentTemplate(string documentTypeCode, string name, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(documentTypeCode)) throw new ArgumentException("DOCUMENT_TEMPLATE_TYPE_REQUIRED");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("DOCUMENT_TEMPLATE_NAME_REQUIRED");
        DocumentTypeCode = documentTypeCode.Trim();
        Name = name.Trim();
        IsDefault = isDefault;
        IsActive = true;
    }

    public string DocumentTypeCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    /// <summary>ADR-0007 §7: at most one active, non-deleted default template per
    /// <see cref="DocumentType"/> — enforced by a partial unique DB index (§14 below), never
    /// only in application code.</summary>
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset? DeletedAt { get; private set; }

    public IReadOnlyCollection<DocumentTemplateVersion> Versions => _versions.AsReadOnly();
    public DocumentTemplateVersion? PublishedVersion => _versions
        .Where(v => v.Status == DocumentTemplateVersionStatus.PUBLISHED)
        .OrderByDescending(v => v.VersionNumber)
        .FirstOrDefault();

    /// <summary>Creates the FIRST version of this template, already PUBLISHED — the only way S7
    /// mints a renderable template at seed time (there is no draft->publish authoring UI yet;
    /// that is S14). <paramref name="validate"/> is the binding-catalogue/visibleWhen/page_setup
    /// validation the S14 publish workflow will also use (ADR-0007 §3).</summary>
    public DocumentTemplateVersion PublishFirstVersion(string definitionJson, string pageSetupJson, int schemaVersion,
        Action<string, string> validate, DateTimeOffset now, Guid? publishedBy)
    {
        if (_versions.Count > 0) throw new InvalidOperationException("DOCUMENT_TEMPLATE_ALREADY_HAS_VERSIONS");
        validate(definitionJson, pageSetupJson);
        var version = new DocumentTemplateVersion(Id, 1, schemaVersion, definitionJson, pageSetupJson, now, publishedBy);
        _versions.Add(version);
        return version;
    }
}

/// <summary>DRAFT -&gt; PUBLISHED -&gt; ARCHIVED (ADR-0007 §7.2). A PUBLISHED row is immutable;
/// editing always creates a NEW DRAFT at <c>version_number + 1</c> — never modeled here because
/// S7 ships no authoring workflow (S14 scope); S7 mints exactly one PUBLISHED version per
/// template, at seed time.</summary>
public enum DocumentTemplateVersionStatus { DRAFT, PUBLISHED, ARCHIVED }

/// <summary>
/// One immutable-once-published render definition (ADR-0007 §7.2). <see cref="DefinitionJson"/>
/// is the block tree (jsonb); <see cref="PageSetupJson"/> is size/margins/header-footer
/// <c>repeatOn</c> (jsonb). Only a <c>PUBLISHED</c> version may ever be rendered
/// (<see cref="RenderErrorCodes.NotPublished"/>).
/// </summary>
public sealed class DocumentTemplateVersion : Entity, IOwnedBy<DocumentTemplate>
{
    private DocumentTemplateVersion() { }

    internal DocumentTemplateVersion(Guid documentTemplateId, int versionNumber, int schemaVersion,
        string definitionJson, string pageSetupJson, DateTimeOffset now, Guid? publishedBy)
    {
        if (versionNumber < 1) throw new ArgumentException("DOCUMENT_TEMPLATE_VERSION_NUMBER_INVALID");
        if (string.IsNullOrWhiteSpace(definitionJson)) throw new ArgumentException("DOCUMENT_TEMPLATE_DEFINITION_REQUIRED");
        if (string.IsNullOrWhiteSpace(pageSetupJson)) throw new ArgumentException("DOCUMENT_TEMPLATE_PAGE_SETUP_REQUIRED");
        DocumentTemplateId = documentTemplateId;
        VersionNumber = versionNumber;
        SchemaVersion = schemaVersion;
        DefinitionJson = definitionJson;
        PageSetupJson = pageSetupJson;
        // S7 mints the version already PUBLISHED (no draft authoring workflow exists yet —
        // that is S14). A future DRAFT->PUBLISHED transition belongs to S14's own method here.
        Status = DocumentTemplateVersionStatus.PUBLISHED;
        PublishedAt = now;
        PublishedBy = publishedBy;
    }

    public Guid DocumentTemplateId { get; private set; }
    public Guid ParentId => DocumentTemplateId;
    public int VersionNumber { get; private set; }
    public DocumentTemplateVersionStatus Status { get; private set; }
    public int SchemaVersion { get; private set; }
    public string DefinitionJson { get; private set; } = string.Empty;
    public string PageSetupJson { get; private set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid? PublishedBy { get; private set; }
}

/// <summary>Stable error codes for the document-template lifecycle (ADR-0007 §7).</summary>
public static class RenderErrorCodes
{
    public const string NotPublished = "DOCUMENT_TEMPLATE_NOT_PUBLISHED";
    public const string InvalidBinding = "DOCUMENT_TEMPLATE_INVALID_BINDING";
}
