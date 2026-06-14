using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DeathrollManager.Helpers;
using DeathrollManager.Models;

namespace DeathrollManager.Services;

public class GameStateService
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly string historyFilePath;

    public DeathrollGame? ActiveGame { get; private set; }
    public List<DeathrollGame> History { get; private set; } = new();

    // ── Undo / redo ────────────────────────────────────────────────────────
    // Each undo pushes the removed roll here (newest last); redo pops it back.
    // The cap bounds both directions: once 10 rolls are undone, undo stops
    // until something is redone. Any NEW roll invalidates the stack — the
    // timeline has diverged and the undone rolls no longer apply.
    private const int MaxUndoSteps = 10;
    private readonly List<GameRoll> redoStack = new();

    public bool CanUndo => ActiveGame is { Rolls.Count: > 0 } && redoStack.Count < MaxUndoSteps;
    public bool CanRedo => ActiveGame != null && redoStack.Count > 0;
    public int  RedoCount => redoStack.Count;

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

        redoStack.Clear();
        log.Information($"[DeathrollManager] Game started: {player1} vs {player2}, starting {startingNumber}, bet {bet}");
        StateChanged?.Invoke();
    }

    // Host-driven manual swap of who rolls first. Guarded by the model to only
    // act while no rolls exist, so it can never scramble recorded turns. Does
    // NOT set FirstRollSwapped — a deliberate host choice shouldn't be auto-undone.
    public bool SwapPlayers()
    {
        if (ActiveGame == null) return false;
        if (!ActiveGame.SwapPlayers()) return false;
        log.Information($"[DeathrollManager] Players swapped: {ActiveGame.Player1Name} now rolls first");
        StateChanged?.Invoke();
        return true;
    }

    // Returns true if the roll was accepted into the active game
    public bool TryAddRoll(string playerName, int rolledValue, int maxValue)
    {
        if (ActiveGame == null) return false;
        if (ActiveGame.Status == GameStatus.Completed || ActiveGame.Status == GameStatus.Abandoned)
            return false;

        // Validate: max must match the current game max
        if (maxValue != ActiveGame.CurrentMax) return false;

        // Validate: correct player's turn (exact or first-name match).
        // Special case — first roll: if the OTHER registered player opens the game
        // (bracket order didn't match who actually rolled first), swap turn order
        // instead of dropping the roll. No rolls exist yet, so the swap is safe.
        if (!NamesMatch(playerName, ActiveGame.CurrentPlayerTurn))
        {
            if (ActiveGame.Status == GameStatus.WaitingForFirstRoll &&
                NamesMatch(playerName, ActiveGame.Player2Name))
            {
                ActiveGame.SwapPlayers();
                ActiveGame.FirstRollSwapped = true;
                log.Information($"[DeathrollManager] First roll came from {ActiveGame.Player1Name} — turn order swapped");
            }
            else
            {
                return false;
            }
        }

        // Store the registered name so roll history and turn logic stay consistent.
        var roll = new GameRoll
        {
            PlayerName  = ActiveGame.CurrentPlayerTurn,
            RolledValue = rolledValue,
            MaxValue    = maxValue,
            Timestamp   = DateTime.Now,
        };

        redoStack.Clear(); // a new roll makes any undone rolls unreplayable
        ActiveGame.Rolls.Add(roll);
        ActiveGame.Status = GameStatus.InProgress;

        if (rolledValue == 1)
            FinalizeGame();
        else
            StateChanged?.Invoke();

        return true;
    }

    // Host-entered roll for when chat detection missed one. Uses the current max,
    // so the host only types the rolled value. Same validation path as chat rolls.
    public bool TryAddManualRoll(string playerName, int rolledValue)
    {
        if (ActiveGame == null) return false;
        if (rolledValue < 1 || rolledValue > ActiveGame.CurrentMax) return false;
        return TryAddRoll(playerName, rolledValue, ActiveGame.CurrentMax);
    }

    // Removes the most recent roll of the active game — typo'd manual entry or
    // a mis-detected chat roll. Turn order rewinds with it automatically.
    // Limited to 10 consecutive steps (the redo stack cap).
    public bool UndoLastRoll()
    {
        if (!CanUndo || ActiveGame == null) return false;

        var removed = ActiveGame.Rolls[^1];
        ActiveGame.Rolls.RemoveAt(ActiveGame.Rolls.Count - 1);
        redoStack.Add(removed);
        if (ActiveGame.Rolls.Count == 0)
        {
            ActiveGame.Status = GameStatus.WaitingForFirstRoll;
            // Undoing across the first roll restores the original seating that the
            // auto-swap flipped, so the original entry order comes back intact.
            if (ActiveGame.FirstRollSwapped)
            {
                ActiveGame.SwapPlayers();
                ActiveGame.FirstRollSwapped = false;
            }
        }

        log.Information($"[DeathrollManager] Roll undone: {removed.PlayerName} rolled {removed.RolledValue}");
        StateChanged?.Invoke();
        return true;
    }

    // Re-applies the most recently undone roll. Redoing a 1 re-finalizes the
    // game exactly like a live roll would.
    public bool RedoLastUndoneRoll()
    {
        if (ActiveGame == null || redoStack.Count == 0) return false;

        var roll = redoStack[^1];

        // Redoing the first roll must reproduce the auto-swap the live roll
        // produced (the matching undo reversed it). If this roll came from the
        // player now seated second, re-apply the swap before validating so the
        // redone state matches the original post-first-roll state exactly.
        bool reSwapFirstRoll = ActiveGame.Rolls.Count == 0 &&
                               NamesMatch(roll.PlayerName, ActiveGame.Player2Name);
        if (reSwapFirstRoll)
        {
            ActiveGame.SwapPlayers();
            ActiveGame.FirstRollSwapped = true;
        }

        // Sanity check — should always hold, since new rolls clear the stack.
        if (roll.MaxValue != ActiveGame.CurrentMax ||
            !NamesMatch(roll.PlayerName, ActiveGame.CurrentPlayerTurn))
        {
            // Roll back the speculative re-swap before bailing out.
            if (reSwapFirstRoll)
            {
                ActiveGame.SwapPlayers();
                ActiveGame.FirstRollSwapped = false;
            }
            log.Warning("[DeathrollManager] Redo stack diverged from game state — clearing");
            redoStack.Clear();
            StateChanged?.Invoke();
            return false;
        }

        redoStack.RemoveAt(redoStack.Count - 1);
        ActiveGame.Rolls.Add(roll);
        ActiveGame.Status = GameStatus.InProgress;

        log.Information($"[DeathrollManager] Roll redone: {roll.PlayerName} rolled {roll.RolledValue}");
        if (roll.RolledValue == 1)
            FinalizeGame();
        else
            StateChanged?.Invoke();
        return true;
    }

    // Pulls the most recently completed game back out of History and reopens it
    // with its final roll removed — for when a game-ending 1 was recorded by
    // mistake. Caller must ensure the game isn't linked to a tournament match;
    // the bracket has already advanced and can't be rewound from here.
    public bool ReopenLastCompletedGame()
    {
        if (ActiveGame != null) return false;
        if (History.Count == 0 || History[0].Status != GameStatus.Completed) return false;

        var game = History[0];
        History.RemoveAt(0);

        redoStack.Clear();
        if (game.Rolls.Count > 0)
        {
            redoStack.Add(game.Rolls[^1]); // the fatal roll — redo can re-end the game
            game.Rolls.RemoveAt(game.Rolls.Count - 1);
        }
        game.Status      = game.Rolls.Count == 0 ? GameStatus.WaitingForFirstRoll : GameStatus.InProgress;
        game.CompletedAt = null;
        ActiveGame       = game;

        SaveHistory();
        log.Information($"[DeathrollManager] Reopened game: {game.Player1Name} vs {game.Player2Name}, max back to {game.CurrentMax}");
        StateChanged?.Invoke();
        return true;
    }

    // Keeps a live game consistent when a tournament player is renamed —
    // registered names AND recorded roll names must move together, since
    // CurrentPlayerTurn derives from comparing the two.
    public void RenameInActiveGame(string oldName, string newName)
    {
        if (ActiveGame == null) return;
        bool touched = false;
        if (string.Equals(ActiveGame.Player1Name, oldName, StringComparison.OrdinalIgnoreCase))
        { ActiveGame.Player1Name = newName; touched = true; }
        if (string.Equals(ActiveGame.Player2Name, oldName, StringComparison.OrdinalIgnoreCase))
        { ActiveGame.Player2Name = newName; touched = true; }
        if (!touched) return;

        foreach (var roll in ActiveGame.Rolls)
            if (string.Equals(roll.PlayerName, oldName, StringComparison.OrdinalIgnoreCase))
                roll.PlayerName = newName;
        StateChanged?.Invoke();
    }

    public void AbandonGame()
    {
        if (ActiveGame == null) return;
        redoStack.Clear();
        ActiveGame.Status      = GameStatus.Abandoned;
        ActiveGame.CompletedAt = DateTime.Now;
        History.Insert(0, ActiveGame);
        ActiveGame = null;
        SaveHistory();
        StateChanged?.Invoke();
    }

    // Allows matching by full name OR first name only (handles "Starry" matching "Starry Nightfall").
    private static bool NamesMatch(string fromChat, string registered) =>
        PlayerNames.Match(fromChat, registered);

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

    // ── Plain-text export ─────────────────────────────────────────────────

    /// <summary>Appends one game's roll-by-roll lines (shared by all text exports).</summary>
    public static void AppendRollLines(StringBuilder sb, DeathrollGame game, string indent = "    ")
    {
        foreach (var roll in game.Rolls)
            sb.AppendLine($"{indent}{roll.PlayerName}: {roll.RolledValue:N0} / {roll.MaxValue:N0}" +
                          (roll.IsGameOver ? "  💀" : ""));
    }

    /// <summary>Appends a one-game summary block: header line, result, rolls.</summary>
    public static void AppendGameBlock(StringBuilder sb, DeathrollGame game)
    {
        sb.AppendLine($"{game.Player1Name} vs {game.Player2Name}  ·  {game.StartedAt:yyyy-MM-dd HH:mm}" +
                      (string.IsNullOrWhiteSpace(game.VenueName) ? "" : $"  ·  {game.VenueName}"));
        sb.AppendLine($"  Starting: {game.StartingNumber:N0}" +
                      (game.BetAmount > 0 ? $"  ·  Bet: {game.BetAmount:N0} gil" : "") +
                      $"  ·  {game.Rolls.Count} rolls  ·  {game.Duration:mm\\:ss}");
        if (game.Status == GameStatus.Completed)
            sb.AppendLine($"  🏆 {game.WinnerName} wins  ·  💀 {game.LoserName} rolled 1");
        else
            sb.AppendLine($"  Status: {game.Status}");
        AppendRollLines(sb, game);
    }

    /// <summary>Full game history as plain text, newest first.</summary>
    public string ExportHistoryText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Deathroll Game History ({History.Count} games) ===");
        sb.AppendLine();
        foreach (var game in History)
        {
            AppendGameBlock(sb, game);
            sb.AppendLine();
        }
        return sb.ToString();
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
