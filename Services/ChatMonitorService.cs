using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace DeathrollManager.Services;

/// <summary>
/// Listens to FFXIV chat for /random roll results and forwards them to GameStateService.
/// Message format (EN): "Random! First Last rolls a 🎲 847 (out of 1000)."
/// Local player format: "Random! You roll a 🎲 4 (out of 50)."
/// </summary>
public class ChatMonitorService : IDisposable
{
    // Handles both "You roll a 🎲 4" and "Starry Nightfall rolls a 🎲 3".
    // \D* (non-digits) safely skips the emoji ± space without consuming leading digits.
    // \S* ?(\d+) was wrong — for "🎲12" it greedily consumed "🎲1", capturing only "2".
    private static readonly Regex RollPattern = new(
        @"Random! (.+?) rolls? a \D*(\d+) \(out of (\d+)\)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IChatGui chatGui;
    private readonly GameStateService gameState;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Fired when a roll is detected that doesn't match any active game.
    // (playerName, rolledValue, outOf) — lets the UI offer to start a game.
    public event Action<string, int, int>? UnmatchedRollDetected;

    public ChatMonitorService(
        IChatGui chatGui,
        GameStateService gameState,
        Configuration config,
        IPluginLog log)
    {
        this.chatGui   = chatGui;
        this.gameState = gameState;
        this.config    = config;
        this.log       = log;

        chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage msg)
    {
        if (!config.AutoDetectGames) return;

        var text  = msg.Message.TextValue;
        var match = RollPattern.Match(text);
        if (!match.Success) return;

        // Log the raw LogKind so we can verify the chat type in /xllog if needed
        log.Debug($"[DeathrollManager] Roll matched (LogKind={(int)msg.LogKind}): {text}");

        var rawName    = match.Groups[1].Value.Trim();
        // FFXIV shows "You" for the local player's own rolls; substitute real name.
        var playerName = string.Equals(rawName, "You", StringComparison.OrdinalIgnoreCase)
            ? (Plugin.PlayerState.CharacterName ?? rawName)
            : rawName;
        var rolled     = int.Parse(match.Groups[2].Value);
        var outOf      = int.Parse(match.Groups[3].Value);

        log.Debug($"[DeathrollManager] Parsed: {playerName} rolled {rolled} out of {outOf}");

        var accepted = gameState.TryAddRoll(playerName, rolled, outOf);
        if (!accepted)
            UnmatchedRollDetected?.Invoke(playerName, rolled, outOf);
    }
}
