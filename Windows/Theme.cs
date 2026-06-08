using System;
using System.Numerics;

namespace DeathrollManager.Windows;

/// <summary>Shared color palette for the plugin UI.</summary>
internal static class Theme
{
    public static readonly Vector4 Gold        = new(1.00f, 0.84f, 0.00f, 1f);
    public static readonly Vector4 Player1     = new(0.40f, 0.65f, 1.00f, 1f); // soft blue
    public static readonly Vector4 Player2     = new(1.00f, 0.55f, 0.25f, 1f); // warm orange
    public static readonly Vector4 Safe        = new(0.15f, 0.90f, 0.45f, 1f); // bright green
    public static readonly Vector4 Warning     = new(1.00f, 0.80f, 0.10f, 1f); // amber
    public static readonly Vector4 Danger      = new(0.95f, 0.20f, 0.20f, 1f); // hot red
    public static readonly Vector4 Muted       = new(0.60f, 0.60f, 0.60f, 1f);
    public static readonly Vector4 White       = new(0.93f, 0.93f, 0.93f, 1f);
    public static readonly Vector4 DimBg       = new(0.08f, 0.08f, 0.15f, 0.95f);
    public static readonly Vector4 CardBg      = new(0.12f, 0.12f, 0.22f, 1.00f);
    public static readonly Vector4 CardBorder  = new(0.30f, 0.30f, 0.55f, 1.00f);
    public static readonly Vector4 WinGreen    = new(0.10f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 LosRed      = new(0.85f, 0.10f, 0.10f, 1f);

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
