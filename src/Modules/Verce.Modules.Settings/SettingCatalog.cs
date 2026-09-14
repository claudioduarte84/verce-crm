using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;

namespace Verce.Modules.Settings;

public sealed record SettingDefinition(
    string Key,
    string DefaultValue,
    AppSettingValueType ValueType,
    string Scope,
    string Description,
    Func<string, bool> IsValid);

/// <summary>The single S2 authority for setting inventory, defaults, types and documented
/// semantic constraints. API updates, seed data and runtime consumers all use this catalogue.</summary>
public static class SettingCatalog
{
    private const long AbsoluteImageSafetyCeiling = 5_242_880;

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        Int("quote.default_validity_days", "15", "quote", "Validade padrão de orçamentos", value => value >= 1),
        Bool("quote.allow_direct_approval", "true", "quote", "Permite aprovação direta"),
        Decimal("pricing.default_margin_percent", "0.35", "pricing", "Margem padrão", value => value >= 0 && value < 1),
        Choice("pricing.price_rounding_policy", "CENT", "pricing", "Política de arredondamento", "CENT", "TEN_CENTS", "WHOLE", "NINETY_NINE", "NONE"),
        Decimal("pricing.margin_warning_denominator", "0.10", "pricing", "Denominador de alerta", _ => true),
        Decimal("costing.default_labor_hourly_rate", "0.00", "costing", "Mão de obra padrão", value => value >= 0),
        Decimal("costing.default_wastage_rate", "0.00", "costing", "Perda padrão", value => value >= 0 && value < 1),
        Decimal("energy.overhead_factor", "0.00", "energy", "Fator de overhead", value => value >= 0),
        Choice("inventory.filament_price_policy", "LAST_PURCHASE", "inventory", "Política de preço de filamento", "LAST_PURCHASE", "MANUAL"),
        Choice("ui.default_theme", "verce-default", "ui", "Tema padrão", "verce-default"),
        RequiredString("branding.product_name", "VERCE 3D", "branding", "Nome do produto", 200),
        RequiredString("branding.product_subtitle", "Laboratório de Custos", "branding", "Subtítulo do produto", 200),
        String("documents.default_payment_terms", "", "documents", "Condições de pagamento"),
        String("documents.default_delivery_terms", "", "documents", "Condições de entrega"),
        String("documents.default_warranty", "", "documents", "Garantia padrão"),
        Int("documents.preview_retention_days", "30", "documents", "Retenção de preview", value => value >= 1),
        Int("uploads.max_image_bytes", AbsoluteImageSafetyCeiling.ToString(CultureInfo.InvariantCulture), "uploads", "Tamanho máximo de imagem", value => value is >= 1 and <= AbsoluteImageSafetyCeiling),
    ];

    private static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static SettingDefinition GetRequired(string key) =>
        ByKey.TryGetValue(key, out var definition) ? definition : throw new ArgumentException("SETTING_KEY_INVALID");

    public static string Validate(string key, string? value, AppSettingValueType valueType)
    {
        var definition = GetRequired(key);
        if (definition.ValueType != valueType) throw new ArgumentException("SETTING_TYPE_INVALID");
        var normalized = value?.Trim() ?? string.Empty;
        if (!definition.IsValid(normalized)) throw new ArgumentException("SETTING_VALUE_INVALID");
        return normalized;
    }

    private static SettingDefinition Int(string key, string value, string scope, string description, Func<long, bool>? predicate = null) =>
        new(key, value, AppSettingValueType.Int, scope, description, text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && (predicate?.Invoke(parsed) ?? true));

    private static SettingDefinition Decimal(string key, string value, string scope, string description, Func<decimal, bool> predicate) =>
        new(key, value, AppSettingValueType.Decimal, scope, description, text => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && predicate(parsed));

    private static SettingDefinition Bool(string key, string value, string scope, string description) =>
        new(key, value, AppSettingValueType.Bool, scope, description, text => bool.TryParse(text, out _));

    private static SettingDefinition Choice(string key, string value, string scope, string description, params string[] choices) =>
        new(key, value, AppSettingValueType.String, scope, description, choices.ToHashSet(StringComparer.Ordinal).Contains);

    private static SettingDefinition RequiredString(string key, string value, string scope, string description, int maxLength) =>
        new(key, value, AppSettingValueType.String, scope, description, text => text.Length is > 0 && text.Length <= maxLength);

    private static SettingDefinition String(string key, string value, string scope, string description) =>
        new(key, value, AppSettingValueType.String, scope, description, _ => true);
}

public sealed class AppSettingValueReader(VerceDbContext db)
{
    public async Task<long> GetMaximumImageBytesAsync(CancellationToken cancellationToken)
    {
        var definition = SettingCatalog.GetRequired("uploads.max_image_bytes");
        var tracked = db.Set<AppSetting>().Local.SingleOrDefault(item => item.Key == definition.Key);
        var value = tracked?.Value ?? await db.Set<AppSetting>().AsNoTracking()
            .Where(item => item.Key == definition.Key)
            .Select(item => item.Value)
            .SingleOrDefaultAsync(cancellationToken) ?? definition.DefaultValue;
        var normalized = SettingCatalog.Validate(definition.Key, value, definition.ValueType);
        return long.Parse(normalized, CultureInfo.InvariantCulture);
    }
}
