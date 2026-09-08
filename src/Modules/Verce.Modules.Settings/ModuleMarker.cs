namespace Verce.Modules.Settings;

/// <summary>
/// Marker for the Settings module boundary (ARCHITECTURE.md §3 module map).
/// Owns: CompanyProfile, BrandAsset(+versions), BrandingAssignment, app settings.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class SettingsModuleMarker
{
    public const string ModuleName = "Settings";
}
