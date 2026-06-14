using System;
using System.Collections.Generic;

namespace DeathrollManager.Models;

public enum GameStatus { WaitingForFirstRoll, InProgress, Completed, Abandoned }

public class DeathrollGame
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public string Player1Name { get; set; } = string.Empty;
    public string Player2Name { get; set; } = string.Empty;
    public int    StartingNumber { get; set; }
    public long   BetAmount   { get; set; }
    public string VenueName   { get; set; } = string.Empty;

    public List<GameRoll> Rolls { get; set; } = new();

    public GameStatus Status      { get; set; } = GameStatus.WaitingForFirstRoll;
    public DateTime   StartedAt   { get; set; } = DateTime.Now;
    public DateTime?  CompletedAt { get; set; }

    // Set true ONLY by the first-roll auto-correction in GameStateService when it
    // swaps seats, so undo of that first roll can restore the original order.
    // SwapPlayers() does NOT set this — callers own the flag, because a deliberate
    // manual swap must not be auto-reversed by undo.
    public bool FirstRollSwapped { get; set; }

    // Derived convenience properties
    public string? WinnerName => Status == GameStatus.Completed && Rolls.Count > 0
        ? (Rolls[^1].PlayerName == Player1Name ? Player2Name : Player1Name)
        : null;

    public string? LoserName => Status == GameStatus.Completed && Rolls.Count > 0
        ? Rolls[^1].PlayerName
        : null;

    public int CurrentMax => Rolls.Count > 0 ? Rolls[^1].RolledValue : StartingNumber;

    public string CurrentPlayerTurn =>
        Rolls.Count == 0
            ? Player1Name
            : Rolls[^1].PlayerName == Player1Name ? Player2Name : Player1Name;

    // 0.0 = just started (safe), 1.0 = max is nearly 0 (dangerous)
    public float DangerLevel =>
        StartingNumber > 0 ? 1f - (float)CurrentMax / StartingNumber : 0f;

    public float SafeFraction =>
        StartingNumber > 0 ? (float)CurrentMax / StartingNumber : 1f;

    public TimeSpan Duration =>
        CompletedAt.HasValue ? CompletedAt.Value - StartedAt : DateTime.Now - StartedAt;

    // Swaps which player is seated first. Only valid before any roll exists,
    // so existing roll attribution can never be scrambled. Returns true if it
    // actually swapped.
    public bool SwapPlayers()
    {
        if (Rolls.Count != 0) return false;
        (Player1Name, Player2Name) = (Player2Name, Player1Name);
        return true;
    }
}
