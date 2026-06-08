using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DeathrollManager.Models;

namespace DeathrollManager.Services;

public class GameStateService
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly string historyFilePath;

    public DeathrollGame? ActiveGame { get; private set; }
    public List<DeathrollGame> History { get; private set; } = new();

    // Fires whenever game state changes so windows can refresh
    public event Action? StateChanged;

    // Fires when a game ends, passing the completed game
    public event Action<DeathrollGame>? GameCompleted;

    public GameStateService(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log    = log;

        historyFilePath = Path.Combine(
            Plugin.PluginInterface.GetPluginConfigDirectory(),
            "history.json");

        LoadHistory();
    }

    public void StartGame(string player1, string player2, int startingNumber, long bet, string venue = "")
    {
        if (ActiveGame is { Status: GameStatus.InProgress or GameStatus.WaitingForFirstRoll })
            AbandonGame();

        ActiveGame = new DeathrollGame
        {
            Player1Name    = player1.Trim(),
            Player2Name    = player2.Trim(),
            StartingNumber = startingNumber,
            BetAmount      = bet,
            VenueName      = venue.Trim(),
            Status         = GameStatus.WaitingForFirstRoll,
        };

        log.Information($"[DeathrollManager] Game started: {player1} vs {player2}, starting {startingNumber}, bet {bet}");
        StateChanged?.Invoke();
    }

    // Returns true if the roll was accepted into the active game
    public bool TryAddRoll(string playerName, int rolledValue, int maxValue)
    {
        if (ActiveGame == null) return false;
        if (ActiveGame.Status == GameStatus.Completed || ActiveGame.Status == GameStatus.Abandoned)
            return false;

        // Validate: max must match the current game max
        if (maxValue != ActiveGame.CurrentMax) return false;

        // Validate: correct player's turn (exact or first-name match)
        if (!NamesMatch(playerName, ActiveGame.CurrentPlayerTurn))
            return false;

        // Store the registered name so roll history and turn logic stay consistent.
        var roll = new GameRoll
        {
            PlayerName  = ActiveGame.CurrentPlayerTurn,
            RolledValue = rolledValue,
            MaxValue    = maxValue,
            Timestamp   = DateTime.Now,
        };

        ActiveGame.Rolls.Add(roll);
        ActiveGame.Status = GameStatus.InProgress;

        if (rolledValue == 1)
            FinalizeGame();
        else
            StateChanged?.Invoke();

        return true;
    }

    public void AbandonGame()
    {
        if (ActiveGame == null) return;
        ActiveGame.Status      = GameStatus.Abandoned;
        ActiveGame.CompletedAt = DateTime.Now;
        History.Insert(0, ActiveGame);
        ActiveGame = null;
        SaveHistory();
        StateChanged?.Invoke();
    }

    // Allows matching by full name OR first name only (handles "Starry" matching "Starry Nightfall").
    private static bool NamesMatch(string fromChat, string registered)
    {
        if (string.Equals(fromChat, registered, StringComparison.OrdinalIgnoreCase)) return true;
        var chatFirst = fromChat.Split(' ')[0];
        var regFirst  = registered.Split(' ')[0];
        return string.Equals(chatFirst, regFirst, StringComparison.OrdinalIgnoreCase);
    }

    private void FinalizeGame()
    {
        if (ActiveGame == null) return;
        ActiveGame.Status      = GameStatus.Completed;
        ActiveGame.CompletedAt = DateTime.Now;

        var completed = ActiveGame;
        History.Insert(0, completed);
        ActiveGame = null;

        SaveHistory();
        log.Information($"[DeathrollManager] Game over — loser: {completed.LoserName}");
        GameCompleted?.Invoke(completed);
        StateChanged?.Invoke();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(History, JsonOpts);
            File.WriteAllText(historyFilePath, json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[DeathrollManager] Failed to save history");
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(historyFilePath)) return;
        try
        {
            var json = File.ReadAllText(historyFilePath);
            History = JsonSerializer.Deserialize<List<DeathrollGame>>(json) ?? new();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[DeathrollManager] Failed to load history");
            History = new();
        }
    }
}
