using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Verce.Modules.Settings;

/// <summary>
/// H-S2-001: deterministic, source-controlled-in-code default VERCE branding, replacing the
/// transparent 1×1 placeholder every seeded Brand Asset used to share. No external image file
/// and no font dependency (font licensing/embedding is an explicit open input — ARCHITECTURE-DEBT
/// §3 — so the mark is pure geometry, never text) — just the frozen v1 palette
/// (ADR-0015 seed identity): Graphite background, an orange diamond mark, a Steel Blue accent.
/// Uses raw pixel access rather than a shape-drawing library: the marks are simple enough that
/// adding SixLabors.ImageSharp.Drawing as a dependency would be unjustified for this alone.
/// </summary>
public static class BrandSeedAssets
{
    private static readonly Rgba32 Graphite = new(0x14, 0x18, 0x1D);
    private static readonly Rgba32 SignalOrange = new(0xC2, 0x41, 0x0C);
    private static readonly Rgba32 SteelBlue = new(0x0E, 0x74, 0x90);

    /// <summary>Wide lockup used for the application's main and document-default logo — a
    /// diamond mark on the left third plus a Steel Blue accent bar along the bottom.</summary>
    public static byte[] WideLockupPng() => Render(480, 120, markOnLeftThird: true, accentStripe: true);

    /// <summary>Square mark used for the compact/collapsed logo — the diamond alone, centered.</summary>
    public static byte[] CompactMarkPng() => Render(160, 160, markOnLeftThird: false, accentStripe: false);

    /// <summary>Small, bold square mark for the browser favicon — same geometry as the compact
    /// mark but with a larger diamond relative to the canvas so it stays legible at 16–32px.</summary>
    public static byte[] FaviconMarkPng() => Render(64, 64, markOnLeftThird: false, accentStripe: false, markScale: 0.8m);

    // ADR-0002 bans float/double from every module directory, with no semantic carve-out for
    // "this isn't money" — decimal it is, even for a pixel-geometry ratio.
    private static byte[] Render(int width, int height, bool markOnLeftThird, bool accentStripe, decimal markScale = 0.6m)
    {
        using var image = new Image<Rgba32>(width, height);
        var markCenterX = markOnLeftThird ? height / 2 : width / 2;
        var markCenterY = height / 2;
        var markRadius = (int)(Math.Min(width, height) / 2m * markScale);
        var stripeHeight = accentStripe ? Math.Max(3, height / 24) : 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (accentStripe && y >= height - stripeHeight)
                    {
                        row[x] = SteelBlue;
                        continue;
                    }

                    // Manhattan-distance test: a filled square rotated 45° is a diamond.
                    var manhattan = Math.Abs(x - markCenterX) + Math.Abs(y - markCenterY);
                    row[x] = manhattan <= markRadius ? SignalOrange : Graphite;
                }
            }
        });

        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        return encoded.ToArray();
    }
}
