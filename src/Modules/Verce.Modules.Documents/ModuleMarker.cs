namespace Verce.Modules.Documents;

/// <summary>
/// Marker for the Documents module boundary (ARCHITECTURE.md §3 module map).
/// Owns: <see cref="GeneratedDocument"/>, content-addressed artifact storage
/// (<see cref="IDocumentStorage"/>), the built-in Quote PDF V1 render pipeline
/// (<see cref="QuotePdfInput"/>, <see cref="QuotePdfHtmlTemplate"/>, <see cref="IHtmlToPdfRenderer"/>).
/// S7 ships the minimal slice ADR-0016 anticipates — a single hard-coded, non-user-editable
/// template. The S14 Document Studio (generic block/binding catalogue, DocumentType,
/// DocumentTemplate versioning, arbitrary templates) is explicitly out of scope and NOT built
/// here; this module's shape is deliberately additive so S14 can extend it later.
/// </summary>
public static class DocumentsModuleMarker
{
    public const string ModuleName = "Documents";
}
