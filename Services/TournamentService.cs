using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using DeathrollManager.Helpers;
using DeathrollManager.Models;

namespace DeathrollManager.Services;

public class TournamentService
{
    private readonly GameStateService gameState;
    private readonly IPluginLog log;

    public Tournament? ActiveTournament { get; private set; }

    /// <summary>Fires on any bracket state change so windows can redraw.</summary>
    public event Action? TournamentStateChanged;

    /// <summary>Fires after a repair (clear result / rename) — relay must full-resync,
    /// since incremental MATCH dedup can't express changed or removed results.</summary>
    public event Action<Tournament>? BracketRepaired;

    // ── Roll-off state ─────────────────────────────────────────────────────
    // Tracks both players rolling /random 10 to decide who goes first.
    public string?  RollOffP1          { get; private set; }
    public string?  RollOffP2          { get; private set; }
    public int?     RollOffP1Roll      { get; private set; }
    public int?     RollOffP2Roll      { get; private set; }
    public string?  RollOffFirstRoller { get; private set; }
    public bool     RollOffTied        { get; private set; }
    public bool     IsRollOffActive    => RollOffP1 != null;

    public event Action? RollOffStateChanged;

    public TournamentService(GameStateService gameState, IPluginLog log)
    {
        this.gameState = gameState;
        this.log       = log;

        gameState.GameCompleted += OnDeathrollGameCompleted;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void StartRollOff(TournamentMatch match)
    {
        RollOffP1          = match.Player1;
        RollOffP2          = match.Player2;
        RollOffP1Roll      = null;
        RollOffP2Roll      = null;
        RollOffFirstRoller = null;
        RollOffTied        = false;
        log.Information($"[DeathrollManager] Roll-off armed: {RollOffP1} vs {RollOffP2}");
        RollOffStateChanged?.Invoke();
    }

    public void CancelRollOff()
    {
        if (!IsRollOffActive) return;
        RollOffP1 = RollOffP2 = null;
        RollOffP1Roll = RollOffP2Roll = null;
        RollOffFirstRoller = null;
        RollOffTied        = false;
        RollOffStateChanged?.Invoke();
    }

    /// <summary>
    /// Called from Plugin when an unmatched /random roll is detected.
    /// Returns true if the roll was consumed by the roll-off state machine.
    /// </summary>
    public bool HandleRollOffCandidate(string playerName, int rolled, int outOf)
    {
        if (!IsRollOffActive || outOf != 10) return false;

        // Lenient match — chat reports "First Last" but brackets often hold first names.
        bool isP1 = PlayerNames.Match(playerName, RollOffP1);
        bool isP2 = PlayerNames.Match(playerName, RollOffP2);
        if (!isP1 && !isP2) return false;

        if (isP1) { RollOffP1Roll = rolled; RollOffTied = false; }
        if (isP2) { RollOffP2Roll = rolled; RollOffTied = false; }

        if (RollOffP1Roll.HasValue && RollOffP2Roll.HasValue)
        {
            if (RollOffP1Roll.Value == RollOffP2Roll.Value)
            {
                RollOffTied   = true;
                RollOffP1Roll = null;
                RollOffP2Roll = null;
                log.Information($"[DeathrollManager] Roll-off tied at {rolled} — waiting for re-roll");
            }
            else
            {
                RollOffFirstRoller = RollOffP1Roll.Value > RollOffP2Roll.Value ? RollOffP1 : RollOffP2;
                log.Information($"[DeathrollManager] Roll-off: {RollOffFirstRoller} goes first");
            }
        }

        RollOffStateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Host manually picks who rolls first — works whether or not a roll-off
    /// announcement was sent (arms the state if needed). Overrides auto-detection.
    /// </summary>
    public void SetRollOffWinner(TournamentMatch match, string player)
    {
        if (!IsRollOffActive)
        {
            RollOffP1     = match.Player1;
            RollOffP2     = match.Player2;
            RollOffP1Roll = null;
            RollOffP2Roll = null;
        }
        RollOffFirstRoller = player;
        RollOffTied        = false;
        log.Information($"[DeathrollManager] First roller set manually: {player}");
        RollOffStateChanged?.Invoke();
    }

    public void CreateTournament(string name, IList<string> players, int startingNumber, long bet,
        string venue = "", BracketLayout layout = BracketLayout.VBracket, BracketFormat format = BracketFormat.SingleElim)
    {
        CancelRollOff();
        ActiveTournament = format == BracketFormat.DoubleElim
            ? Tournament.CreateDoubleElim(name, players, startingNumber, bet, venue, layout: layout)
            : Tournament.Create(name, players, startingNumber, bet, venue, layout: layout);
        log.Information($"[DeathrollManager] Tournament created: {name} ({players.Count} players, {format})");
        TournamentStateChanged?.Invoke();
    }

    public void TriggerBracketReset()
    {
        if (ActiveTournament == null) return;
        ActiveTournament.TriggerBracketReset();
        log.Information("[DeathrollManager] Bracket reset triggered");
        TournamentStateChanged?.Invoke();
    }

    public void DeclareChampionWithoutReset()
    {
        if (ActiveTournament == null) return;
        ActiveTournament.DeclareChampionWithoutReset();
        log.Information($"[DeathrollManager] Champion declared (no reset): {ActiveTournament.Champion}");
        TournamentStateChanged?.Invoke();
    }

    public void CancelTournament()
    {
        CancelRollOff();
        ActiveTournament = null;
        TournamentStateChanged?.Invoke();
    }

    /// <summary>
    /// Starts the current pending match. If firstRoller is provided (e.g. roll-off winner),
    /// that player goes first regardless of their P1/P2 position in the bracket.
    /// </summary>
    public void StartCurrentMatch(string? firstRoller = null)
    {
        if (ActiveTournament == null) return;
        var match = ActiveTournament.CurrentMatch;
        if (match == null || match.Status != MatchStatus.Pending || !match.BothPlayersReady) return;

        match.Status = MatchStatus.InProgress;

        string p1, p2;
        if (firstRoller != null &&
            string.Equals(firstRoller, match.Player2, StringComparison.OrdinalIgnoreCase))
        {
            p1 = match.Player2!;
            p2 = match.Player1!;
        }
        else
        {
            p1 = match.Player1!;
            p2 = match.Player2!;
        }

        gameState.StartGame(p1, p2, ActiveTournament.StartingNumber, ActiveTournament.BetAmount, ActiveTournament.VenueName);
        CancelRollOff();

        log.Information($"[DeathrollManager] Tournament match started: {p1} (first) vs {p2}");
        TournamentStateChanged?.Invoke();
    }

    /// <summary>
    /// True if a completed game is linked to a match in the active bracket.
    /// Such games must not be reopened — the bracket has already advanced.
    /// </summary>
    public bool IsGameLinkedToBracket(Guid gameId) =>
        ActiveTournament != null &&
        AllMatches(ActiveTournament).Any(m => m.GameId == gameId);

    // ── Manual override ────────────────────────────────────────────────────

    /// <summary>
    /// Clears a completed match result. Downstream results that depended on the
    /// winner revert to pending. Returns how many downstream results were also
    /// reverted, or -1 if there was nothing to clear.
    /// </summary>
    public int ClearMatchResult(TournamentMatch match)
    {
        if (ActiveTournament == null) return -1;
        CancelRollOff();
        int dropped = ActiveTournament.ClearResult(match);
        if (dropped >= 0)
        {
            log.Information($"[DeathrollManager] Result cleared: {match.Player1} vs {match.Player2} " +
                            $"({dropped} downstream result(s) reverted)");
            BracketRepaired?.Invoke(ActiveTournament);
            TournamentStateChanged?.Invoke();
        }
        return dropped;
    }

    /// <summary>Renames a player across the bracket and any live game tracking them.</summary>
    public void RenamePlayer(string oldName, string newName)
    {
        if (ActiveTournament == null) return;
        ActiveTournament.RenamePlayer(oldName, newName);
        gameState.RenameInActiveGame(oldName, newName);
        log.Information($"[DeathrollManager] Player renamed: {oldName} → {newName}");
        BracketRepaired?.Invoke(ActiveTournament);
        TournamentStateChanged?.Invoke();
    }

    public void ForceWinner(TournamentMatch match, string winner)
    {
        if (ActiveTournament == null) return;
        CancelRollOff();
        ActiveTournament.RecordWinner(match, winner);
        log.Information($"[DeathrollManager] Match winner forced: {winner}");
        TournamentStateChanged?.Invoke();
    }

    // ── Roll feed ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all rolls recorded so far in the active tournament, in order.
    /// Combines completed match histories (via GameId) with the current live game.
    /// </summary>
    public List<RollFeedEntry> GetRollFeed()
    {
        var result = new List<RollFeedEntry>();
        if (ActiveTournament == null) return result;

        var allMatches = AllMatches(ActiveTournament);
        foreach (var match in allMatches)
        {
            if (!match.GameId.HasValue) continue;
            var game = gameState.History.FirstOrDefault(g => g.Id == match.GameId.Value);
            if (game == null) continue;
            foreach (var roll in game.Rolls)
                result.Add(new RollFeedEntry(match, game, roll, live: false));
        }

        var activeMatch = ActiveTournament.CurrentMatch;
        if (activeMatch?.Status == MatchStatus.InProgress && gameState.ActiveGame != null)
        {
            foreach (var roll in gameState.ActiveGame.Rolls)
                result.Add(new RollFeedEntry(activeMatch, gameState.ActiveGame, roll, live: true));
        }

        return result;
    }

    private static IEnumerable<TournamentMatch> AllMatches(Tournament t)
    {
        if (t.Format == BracketFormat.DoubleElim)
        {
            foreach (var r in t.WBRounds) foreach (var m in r) yield return m;
            foreach (var r in t.LBRounds) foreach (var m in r) yield return m;
            if (t.GrandFinalsMatch != null) yield return t.GrandFinalsMatch;
            if (t.GrandFinalsReset  != null) yield return t.GrandFinalsReset;
        }
        else
        {
            foreach (var r in t.Rounds) foreach (var m in r) yield return m;
        }
    }

    /// <summary>Returns the completed DeathrollGame for a given match, or null.</summary>
    public DeathrollGame? GetMatchGame(TournamentMatch match)
    {
        if (!match.GameId.HasValue) return null;
        return gameState.History.FirstOrDefault(g => g.Id == match.GameId.Value);
    }

    // ── Bracket text export ───────────────────────────────────────────────

    /// <summary>Plain-text bracket summary suitable for pasting into chat or Discord.</summary>
    public string ExportBracketText() => BuildReport(includeRolls: false);

    /// <summary>Full match report — every match plus its roll-by-roll history.</summary>
    public string ExportFullReport() => BuildReport(includeRolls: true);

    /// <summary>One match with its complete roll history as plain text.</summary>
    public string ExportMatchText(TournamentMatch match)
    {
        var sb = new StringBuilder();
        if (ActiveTournament != null)
            sb.AppendLine($"{ActiveTournament.Name} — {MatchLabel(ActiveTournament, match)}");

        var game = GetMatchGame(match);
        if (game == null)
        {
            sb.AppendLine($"{match.Player1 ?? "TBD"} vs {match.Player2 ?? "TBD"}" +
                          (match.Winner != null ? $" → {match.Winner} wins (no roll record)" : ""));
            return sb.ToString();
        }

        GameStateService.AppendGameBlock(sb, game);
        return sb.ToString();
    }

    private string BuildReport(bool includeRolls)
    {
        if (ActiveTournament == null) return "(no active tournament)";
        var t  = ActiveTournament;
        var sb = new StringBuilder();

        sb.AppendLine(includeRolls ? $"=== {t.Name} — Full Match Report ===" : $"=== {t.Name} ===");
        if (!string.IsNullOrWhiteSpace(t.VenueName))
            sb.AppendLine($"Venue: {t.VenueName}");
        sb.AppendLine($"Starting: {t.StartingNumber:N0}" +
                      (t.BetAmount > 0 ? $"  |  Bet: {t.BetAmount:N0} gil" : ""));
        sb.AppendLine();

        // AllMatches yields in display order (single: rounds; double: WB, LB, GF);
        // emit a section header whenever the round label changes.
        string? currentGroup = null;
        foreach (var m in AllMatches(t))
        {
            if (m.IsBye) continue;

            var group = MatchLabel(t, m);
            if (group != currentGroup)
            {
                if (currentGroup != null) sb.AppendLine();
                sb.AppendLine($"── {group} ──");
                currentGroup = group;
            }

            string status = m.Status switch
            {
                MatchStatus.Completed  => $"→ {m.Winner} wins",
                MatchStatus.InProgress => "(in progress)",
                _                      => "(pending)",
            };
            sb.AppendLine($"  {m.Player1 ?? "TBD"} vs {m.Player2 ?? "TBD"}  {status}");

            if (!includeRolls) continue;

            var game = GetMatchGame(m);
            if (game == null && m.Status == MatchStatus.InProgress &&
                gameState.ActiveGame is { } live &&
                m.HasPlayer(live.Player1Name) && m.HasPlayer(live.Player2Name))
                game = live; // include the live game's rolls so far

            if (game != null && game.Rolls.Count > 0)
                GameStateService.AppendRollLines(sb, game);
            else if (m.Status == MatchStatus.Completed)
                sb.AppendLine("    (no roll record — winner was set manually)");
        }

        sb.AppendLine();
        if (t.IsComplete)
            sb.AppendLine($"🏆 CHAMPION: {t.Champion}");

        return sb.ToString();
    }

    private static string MatchLabel(Tournament t, TournamentMatch m)
    {
        if (t.Format == BracketFormat.DoubleElim)
        {
            return m.Side switch
            {
                BracketSide.GrandFinals => m.MatchIndex == 1 ? "Grand Finals Reset" : "Grand Finals",
                BracketSide.Losers      => $"Losers Round {m.RoundIndex + 1}",
                _                       => $"Winners Round {m.RoundIndex + 1}",
            };
        }
        return m.RoundIndex == t.NumRounds - 1 ? "Finals" : $"Round {m.RoundIndex + 1}";
    }

    // ── Hook into GameStateService ─────────────────────────────────────────

    private void OnDeathrollGameCompleted(DeathrollGame game)
    {
        if (ActiveTournament == null) return;

        var match = AllMatches(ActiveTournament).FirstOrDefault(m =>
            m.Status == MatchStatus.InProgress &&
            m.HasPlayer(game.Player1Name) &&
            m.HasPlayer(game.Player2Name));

        if (match == null || game.WinnerName == null) return;

        // Record the winner under the bracket's spelling, not the game's — a game
        // started from the Game tab may hold full chat names while the bracket has
        // first names, and the advancing slot/highlighting must stay consistent.
        var winner = PlayerNames.Match(game.WinnerName, match.Player1)
            ? match.Player1!
            : match.Player2!;

        match.GameId = game.Id;
        ActiveTournament.RecordWinner(match, winner);

        log.Information($"[DeathrollManager] Tournament match complete: {winner} won");
        if (ActiveTournament.IsComplete)
            log.Information($"[DeathrollManager] Champion: {ActiveTournament.Champion}");

        TournamentStateChanged?.Invoke();
    }
}

// ── Roll feed entry ────────────────────────────────────────────────────────

public sealed class RollFeedEntry
{
    public TournamentMatch Match { get; }
    public DeathrollGame   Game  { get; }
    public GameRoll        Roll  { get; }
    public bool            Live  { get; }   // true = from the current ongoing game

    public string MatchLabel => Match.Side switch
    {
        BracketSide.Losers      => $"LB·R{Match.RoundIndex + 1}·M{Match.MatchIndex + 1}",
        BracketSide.GrandFinals => Match.MatchIndex == 1 ? "GF Reset" : "Grand Finals",
        _                       => $"R{Match.RoundIndex + 1}·M{Match.MatchIndex + 1}",
    };

    public RollFeedEntry(TournamentMatch match, DeathrollGame game, GameRoll roll, bool live)
    {
        Match = match; Game = game; Roll = roll; Live = live;
    }
}
