namespace DeathrollManager.Models;

public enum MatchStatus { Pending, InProgress, Completed, Bye }
public enum BracketSide { Winners, Losers, GrandFinals }

public class TournamentMatch
{
    public string?      Player1    { get; set; }   // null = TBD
    public string?      Player2    { get; set; }   // null = TBD, "BYE" = auto-advance
    public string?      Winner     { get; set; }
    public MatchStatus  Status     { get; set; } = MatchStatus.Pending;
    public int          RoundIndex { get; set; }
    public int          MatchIndex { get; set; }   // 0-based index within round
    public BracketSide  Side       { get; set; } = BracketSide.Winners;

    /// <summary>Set when the match completes — references the DeathrollGame.Id in GameStateService.History.</summary>
    public System.Guid? GameId { get; set; }

    public bool IsBye        => Status == MatchStatus.Bye;
    public bool IsCompleted  => Status == MatchStatus.Completed;
    public bool BothPlayersReady => Player1 != null && Player2 != null && !IsBye;

    public bool HasPlayer(string name) =>
        string.Equals(Player1, name, System.StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Player2, name, System.StringComparison.OrdinalIgnoreCase);
}
