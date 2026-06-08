using System;

namespace DeathrollManager.Models;

public class GameRoll
{
    public string PlayerName { get; set; } = string.Empty;
    public int RolledValue  { get; set; }
    public int MaxValue     { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsGameOver => RolledValue == 1;

    // Fraction of the max remaining after this roll (1.0 = safe, ~0 = nearly dead)
    public float SafeFractionAfter(int startingNumber) =>
        startingNumber > 0 ? (float)RolledValue / startingNumber : 1f;
}
