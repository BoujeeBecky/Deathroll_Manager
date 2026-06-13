using System;
using Dalamud.Bindings.ImGui;
using DeathrollManager.Models;

namespace DeathrollManager.Windows;

/// <summary>
/// Per-roll countdown display for venues with "you have N seconds to roll" rules.
/// Purely visual — never auto-forfeits anyone. Counts from the last roll
/// (or game start) and turns red through the danger gradient as it expires.
/// </summary>
internal static class RollTimer
{
    private static int? SecondsRemaining(Configuration config, DeathrollGame game)
    {
        if (config.RollTimerSeconds <= 0) return null;
        if (game.Status is not (GameStatus.InProgress or GameStatus.WaitingForFirstRoll)) return null;
        var last = game.Rolls.Count > 0 ? game.Rolls[^1].Timestamp : game.StartedAt;
        return config.RollTimerSeconds - (int)(DateTime.Now - last).TotalSeconds;
    }

    private static (string text, System.Numerics.Vector4 color)? Format(Configuration config, DeathrollGame game)
    {
        var remain = SecondsRemaining(config, game);
        if (remain == null) return null;

        if (remain.Value >= 0)
        {
            float frac = 1f - (float)remain.Value / config.RollTimerSeconds; // 0 fresh → 1 expired
            return ($"⏱ {remain.Value}s", Theme.DangerGradient(frac));
        }

        float pulse = 0.5f + (float)(Math.Sin(ImGui.GetTime() * 6.0) * 0.5);
        return ($"⏰ TIME!  ({-remain.Value}s over)", Theme.Danger with { W = 0.5f + pulse * 0.5f });
    }

    /// <summary>Left-aligned inline variant. Returns true if anything was drawn.</summary>
    public static bool DrawInline(Configuration config, DeathrollGame game)
    {
        if (Format(config, game) is not { } f) return false;
        ImGui.TextColored(f.color, f.text);
        return true;
    }

    /// <summary>Horizontally centered variant for the Game tab turn indicator.</summary>
    public static void DrawCentered(Configuration config, DeathrollGame game)
    {
        if (Format(config, game) is not { } f) return;
        float avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX((avail - ImGui.CalcTextSize(f.text).X) * 0.5f);
        ImGui.TextColored(f.color, f.text);
    }
}
