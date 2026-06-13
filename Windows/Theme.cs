using System;
using System.Linq;
using System.Numerics;

namespace DeathrollManager.Windows;

/// <summary>A named color palette variant. Semantic colors (Safe/Warning/Danger)
/// are NOT themed — danger must always read as danger.</summary>
internal sealed record ThemePreset(
    string  Name,
    Vector4 Accent,
    Vector4 Player1,
    Vector4 Player2,
    Vector4 CardBg,
    Vector4 CardBorder,
    Vector4 DimBg);

/// <summary>Shared color palette for the plugin UI. Themed colors are mutable
/// statics swapped by Apply() — every window reads them per-frame, so a theme
/// change repaints the whole plugin instantly.</summary>
internal static class Theme
{
    // ── Semantic colors — identical in every theme ────────────────────────
    public static readonly Vector4 Safe     = new(0.15f, 0.90f, 0.45f, 1f); // bright green
    public static readonly Vector4 Warning  = new(1.00f, 0.80f, 0.10f, 1f); // amber
    public static readonly Vector4 Danger   = new(0.95f, 0.20f, 0.20f, 1f); // hot red
    public static readonly Vector4 Muted    = new(0.60f, 0.60f, 0.60f, 1f);
    public static readonly Vector4 White    = new(0.93f, 0.93f, 0.93f, 1f);
    public static readonly Vector4 WinGreen = new(0.10f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 LosRed   = new(0.85f, 0.10f, 0.10f, 1f);

    // ── Themed colors — set by Apply() ────────────────────────────────────
    public static Vector4 Gold       { get; private set; }
    public static Vector4 Player1    { get; private set; }
    public static Vector4 Player2    { get; private set; }
    public static Vector4 CardBg     { get; private set; }
    public static Vector4 CardBorder { get; private set; }
    public static Vector4 DimBg      { get; private set; }

    public static readonly ThemePreset[] Presets =
    [
        new("Classic",
            Accent:     new(1.00f, 0.84f, 0.00f, 1f),   // gold
            Player1:    new(0.40f, 0.65f, 1.00f, 1f),   // soft blue
            Player2:    new(1.00f, 0.55f, 0.25f, 1f),   // warm orange
            CardBg:     new(0.12f, 0.12f, 0.22f, 1f),
            CardBorder: new(0.30f, 0.30f, 0.55f, 1f),
            DimBg:      new(0.08f, 0.08f, 0.15f, 0.95f)),

        new("Synthwave",
            Accent:     new(1.00f, 0.25f, 0.65f, 1f),   // hot pink
            Player1:    new(0.20f, 0.90f, 1.00f, 1f),   // electric cyan
            Player2:    new(0.80f, 0.45f, 1.00f, 1f),   // neon violet
            CardBg:     new(0.10f, 0.05f, 0.18f, 1f),
            CardBorder: new(0.55f, 0.22f, 0.70f, 1f),
            DimBg:      new(0.07f, 0.03f, 0.13f, 0.95f)),

        new("Ice",
            Accent:     new(0.55f, 0.88f, 1.00f, 1f),   // glacial cyan
            Player1:    new(0.35f, 0.55f, 1.00f, 1f),   // deep blue
            Player2:    new(0.75f, 0.95f, 0.95f, 1f),   // frost white
            CardBg:     new(0.07f, 0.11f, 0.17f, 1f),
            CardBorder: new(0.30f, 0.48f, 0.66f, 1f),
            DimBg:      new(0.05f, 0.08f, 0.13f, 0.95f)),

        new("Crimson Court",
            Accent:     new(1.00f, 0.78f, 0.25f, 1f),   // rich gold
            Player1:    new(0.95f, 0.32f, 0.38f, 1f),   // crimson
            Player2:    new(0.95f, 0.85f, 0.60f, 1f),   // champagne
            CardBg:     new(0.15f, 0.06f, 0.09f, 1f),
            CardBorder: new(0.55f, 0.20f, 0.28f, 1f),
            DimBg:      new(0.10f, 0.04f, 0.06f, 0.95f)),

        new("Emerald Casino",
            Accent:     new(1.00f, 0.84f, 0.20f, 1f),   // table gold
            Player1:    new(0.25f, 0.90f, 0.55f, 1f),   // emerald
            Player2:    new(0.75f, 0.55f, 1.00f, 1f),   // violet chip
            CardBg:     new(0.05f, 0.13f, 0.09f, 1f),
            CardBorder: new(0.20f, 0.46f, 0.32f, 1f),
            DimBg:      new(0.03f, 0.09f, 0.06f, 0.95f)),

        // Green-forward with yellow pops (the "Bananna2" hierarchy) on a dark
        // olive base — yellow-dominant reads as glare and collides with Warning.
        new("Banana",
            Accent:     new(1.00f, 0.85f, 0.25f, 1f),   // ripe banana
            Player1:    new(0.98f, 0.88f, 0.45f, 1f),   // banana cream
            Player2:    new(0.55f, 0.88f, 0.40f, 1f),   // leaf green
            CardBg:     new(0.10f, 0.11f, 0.05f, 1f),
            CardBorder: new(0.48f, 0.46f, 0.18f, 1f),
            DimBg:      new(0.07f, 0.08f, 0.04f, 0.95f)),

        new("Boujee",
            Accent:     new(0.98f, 0.72f, 0.58f, 1f),   // rose gold
            Player1:    new(0.96f, 0.58f, 0.52f, 1f),   // blush rose
            Player2:    new(0.62f, 0.68f, 1.00f, 1f),   // periwinkle
            CardBg:     new(0.09f, 0.09f, 0.22f, 1f),   // deep indigo
            CardBorder: new(0.60f, 0.44f, 0.42f, 1f),
            DimBg:      new(0.06f, 0.06f, 0.16f, 0.95f)),

        new("Opulent",
            Accent:     new(1.00f, 0.82f, 0.35f, 1f),   // ornate gold
            Player1:    new(0.78f, 0.62f, 1.00f, 1f),   // royal lilac
            Player2:    new(0.48f, 0.80f, 0.58f, 1f),   // jade marble
            CardBg:     new(0.13f, 0.08f, 0.20f, 1f),   // velvet purple
            CardBorder: new(0.55f, 0.45f, 0.20f, 1f),   // antique gold
            DimBg:      new(0.09f, 0.05f, 0.14f, 0.95f)),

    ];

    static Theme() => Apply("Classic");

    /// <summary>Switches the active palette. Unknown names fall back to Classic.</summary>
    public static void Apply(string name)
    {
        var p = Presets.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

        Gold       = p.Accent;
        Player1    = p.Player1;
        Player2    = p.Player2;
        CardBg     = p.CardBg;
        CardBorder = p.CardBorder;
        DimBg      = p.DimBg;
    }

    /// <summary>Interpolate between Safe, Warning, and Danger based on a 0-1 danger level.</summary>
    public static Vector4 DangerGradient(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.5f) return Vector4.Lerp(Safe,    Warning, t * 2f);
        return              Vector4.Lerp(Warning,  Danger,  (t - 0.5f) * 2f);
    }

    public static uint ToU32(Vector4 c)
    {
        uint r = (uint)(Math.Clamp(c.X, 0, 1) * 255);
        uint g = (uint)(Math.Clamp(c.Y, 0, 1) * 255);
        uint b = (uint)(Math.Clamp(c.Z, 0, 1) * 255);
        uint a = (uint)(Math.Clamp(c.W, 0, 1) * 255);
        return r | (g << 8) | (b << 16) | (a << 24);
    }
}
