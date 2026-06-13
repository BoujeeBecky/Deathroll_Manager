using System;
using System.Collections.Generic;
using System.Linq;

namespace DeathrollManager.Models;

public enum BracketLayout { VBracket, LeftToRight }
public enum BracketFormat { SingleElim, DoubleElim }

public class Tournament
{
    public Guid   Id             { get; set; } = Guid.NewGuid();
    public string Name           { get; set; } = "Deathroll Tournament";
    public string VenueName      { get; set; } = string.Empty;
    public int    StartingNumber { get; set; } = 1000;
    public long   BetAmount      { get; set; }
    public bool   IsComplete     { get; set; }
    public string? Champion      { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.Now;
    public BracketLayout Layout  { get; set; } = BracketLayout.VBracket;
    public BracketFormat Format  { get; set; } = BracketFormat.SingleElim;

    // ── Single-elim ───────────────────────────────────────────────────────
    /// <summary>Rounds[r][m] — round 0 is leftmost (first round). Used only for SingleElim.</summary>
    public List<List<TournamentMatch>> Rounds { get; set; } = new();

    // ── Double-elim ───────────────────────────────────────────────────────
    public List<List<TournamentMatch>> WBRounds        { get; set; } = new();
    public List<List<TournamentMatch>> LBRounds        { get; set; } = new();
    public TournamentMatch?            GrandFinalsMatch { get; set; }
    public TournamentMatch?            GrandFinalsReset { get; set; }
    /// <summary>True after LB champion wins GF game 1 — host decides whether to play reset.</summary>
    public bool GrandFinalsNeedsReset { get; set; }

    // ── Computed ──────────────────────────────────────────────────────────
    public int TotalSlots => Format == BracketFormat.DoubleElim
        ? (WBRounds.Count > 0 ? WBRounds[0].Count * 2 : 0)
        : (Rounds.Count > 0   ? Rounds[0].Count   * 2 : 0);

    public int NumRounds => Format == BracketFormat.DoubleElim
        ? WBRounds.Count
        : Rounds.Count;

    public TournamentMatch? CurrentMatch => Format == BracketFormat.DoubleElim
        ? CurrentMatchDE()
        : Rounds.SelectMany(r => r).FirstOrDefault(m => m.Status is MatchStatus.Pending or MatchStatus.InProgress);

    private TournamentMatch? CurrentMatchDE()
    {
        foreach (var round in WBRounds)
            foreach (var m in round)
                if (m.Status is MatchStatus.Pending or MatchStatus.InProgress) return m;
        foreach (var round in LBRounds)
            foreach (var m in round)
                if (m.Status is MatchStatus.Pending or MatchStatus.InProgress) return m;
        if (GrandFinalsMatch?.Status is MatchStatus.Pending or MatchStatus.InProgress) return GrandFinalsMatch;
        if (GrandFinalsReset?.Status  is MatchStatus.Pending or MatchStatus.InProgress) return GrandFinalsReset;
        return null;
    }

    // ── Single-elim factory ───────────────────────────────────────────────

    public static Tournament Create(string name, IList<string> players, int startingNumber, long bet,
        string venue = "", bool shuffle = true, BracketLayout layout = BracketLayout.VBracket)
    {
        var t = new Tournament
        {
            Name = name, VenueName = venue, StartingNumber = startingNumber,
            BetAmount = bet, Layout = layout, Format = BracketFormat.SingleElim,
        };

        int slots = 1;
        while (slots < players.Count) slots <<= 1;
        int numRounds = (int)Math.Log2(slots);

        var seeded = shuffle ? players.OrderBy(_ => Guid.NewGuid()).ToList() : players.ToList();
        while (seeded.Count < slots) seeded.Add("BYE");

        var round0 = new List<TournamentMatch>();
        for (int i = 0; i < slots / 2; i++)
        {
            bool isBye = seeded[i * 2 + 1] == "BYE";
            round0.Add(new TournamentMatch
            {
                Player1    = seeded[i * 2],
                Player2    = isBye ? null : seeded[i * 2 + 1],
                Status     = isBye ? MatchStatus.Bye : MatchStatus.Pending,
                Winner     = isBye ? seeded[i * 2] : null,
                RoundIndex = 0, MatchIndex = i,
            });
        }
        t.Rounds.Add(round0);

        for (int r = 1; r < numRounds; r++)
        {
            int count = slots / (1 << (r + 1));
            var round = new List<TournamentMatch>();
            for (int i = 0; i < count; i++)
                round.Add(new TournamentMatch { RoundIndex = r, MatchIndex = i });
            t.Rounds.Add(round);
        }

        t.PropagateByes();
        return t;
    }

    // ── Double-elim factory ───────────────────────────────────────────────

    public static Tournament CreateDoubleElim(string name, IList<string> players, int startingNumber, long bet,
        string venue = "", bool shuffle = true, BracketLayout layout = BracketLayout.VBracket)
    {
        var t = new Tournament
        {
            Name = name, VenueName = venue, StartingNumber = startingNumber,
            BetAmount = bet, Layout = layout, Format = BracketFormat.DoubleElim,
        };

        // Cap at 16 and pad to next power of 2
        int count = Math.Min(players.Count, 16);
        int slots  = 1;
        while (slots < count) slots <<= 1;
        int wbRounds = (int)Math.Log2(slots);

        var seeded = shuffle ? players.Take(count).OrderBy(_ => Guid.NewGuid()).ToList()
                             : players.Take(count).ToList();
        while (seeded.Count < slots) seeded.Add("BYE");

        // ── WB ────────────────────────────────────────────────────────────
        var wbR0 = new List<TournamentMatch>();
        for (int i = 0; i < slots / 2; i++)
        {
            bool isBye = seeded[i * 2 + 1] == "BYE";
            wbR0.Add(new TournamentMatch
            {
                Player1    = seeded[i * 2],
                Player2    = isBye ? null : seeded[i * 2 + 1],
                Status     = isBye ? MatchStatus.Bye : MatchStatus.Pending,
                Winner     = isBye ? seeded[i * 2] : null,
                Side = BracketSide.Winners, RoundIndex = 0, MatchIndex = i,
            });
        }
        t.WBRounds.Add(wbR0);

        for (int r = 1; r < wbRounds; r++)
        {
            int c = slots / (1 << (r + 1));
            var round = new List<TournamentMatch>();
            for (int i = 0; i < c; i++)
                round.Add(new TournamentMatch { Side = BracketSide.Winners, RoundIndex = r, MatchIndex = i });
            t.WBRounds.Add(round);
        }

        // ── LB ────────────────────────────────────────────────────────────
        // 2*(wbRounds-1) LB rounds. Sizes alternate: feed then cull.
        // LBR0: slots/4  (WBR0 losers fill two slots per match)
        // LBR1: same as LBR0 (carry + WBR1 drop-in)
        // LBR2: LBR1/2  (cull)
        // LBR3: same as LBR2 (carry + WBR2 drop-in)
        // ...
        int lbRoundCount = 2 * (wbRounds - 1);
        int prevCount = slots / 4;  // LBR0 match count
        for (int r = 0; r < lbRoundCount; r++)
        {
            int c = r == 0 ? prevCount
                  : (r % 2 == 0 ? t.LBRounds[r - 1].Count / 2  // cull
                                 : t.LBRounds[r - 1].Count);    // carry
            var round = new List<TournamentMatch>();
            for (int i = 0; i < c; i++)
                round.Add(new TournamentMatch { Side = BracketSide.Losers, RoundIndex = r, MatchIndex = i });
            t.LBRounds.Add(round);
        }

        // ── Grand Finals ──────────────────────────────────────────────────
        t.GrandFinalsMatch = new TournamentMatch
        {
            Side = BracketSide.GrandFinals, RoundIndex = 0, MatchIndex = 0,
        };

        // Propagate WB R0 byes into WB R1 and LB R0
        t.PropagateByesDE();

        return t;
    }

    // ── Bracket advancement (single-elim) ─────────────────────────────────

    public void RecordWinner(TournamentMatch match, string winner)
    {
        match.Winner = winner;
        match.Status = MatchStatus.Completed;

        if (Format == BracketFormat.DoubleElim)
            AdvanceDoubleElim(match);
        else
        {
            AdvanceWinner(match);
            CheckCompleteSE();
        }
    }

    private void AdvanceWinner(TournamentMatch match)
    {
        int nextRound = match.RoundIndex + 1;
        if (nextRound >= Rounds.Count) return;

        var nextMatch = Rounds[nextRound][match.MatchIndex / 2];
        if (match.MatchIndex % 2 == 0) nextMatch.Player1 = match.Winner;
        else                           nextMatch.Player2 = match.Winner;

        if (nextMatch.Player1 == null || nextMatch.Player2 == null) return;

        if (nextMatch.Player1 == "BYE" || nextMatch.Player2 == "BYE")
        {
            nextMatch.Winner = nextMatch.Player1 == "BYE" ? nextMatch.Player2 : nextMatch.Player1;
            nextMatch.Status = MatchStatus.Bye;
            AdvanceWinner(nextMatch);
        }
    }

    private void PropagateByes()
    {
        foreach (var round in Rounds)
            foreach (var m in round.Where(m => m.Status == MatchStatus.Bye && m.Winner != null))
                AdvanceWinner(m);
    }

    private void CheckCompleteSE()
    {
        var last = Rounds[^1];
        if (last.Count == 1 && last[0].IsCompleted)
        {
            Champion   = last[0].Winner;
            IsComplete = true;
        }
    }

    // ── Bracket advancement (double-elim) ─────────────────────────────────

    private void AdvanceDoubleElim(TournamentMatch match)
    {
        switch (match.Side)
        {
            case BracketSide.Winners:    AdvanceWBMatch(match);  break;
            case BracketSide.Losers:     AdvanceLBMatch(match);  break;
            case BracketSide.GrandFinals: HandleGFResult(match); break;
        }
    }

    private void AdvanceWBMatch(TournamentMatch match)
    {
        int r = match.RoundIndex;
        int m = match.MatchIndex;

        // Winner advances in WB
        if (r + 1 < WBRounds.Count)
        {
            var next = WBRounds[r + 1][m / 2];
            if (m % 2 == 0) next.Player1 = match.Winner;
            else             next.Player2 = match.Winner;
        }
        else
        {
            // WB Finals winner → GF Player1 (WB champion seat)
            GrandFinalsMatch!.Player1 = match.Winner;
        }

        // Loser drops to LB (skip if opponent was a BYE — no real loser)
        string? loser = match.Player1 == match.Winner ? match.Player2 : match.Player1;
        if (!string.IsNullOrEmpty(loser) && loser != "BYE")
            RouteLoserToLB(r, m, loser);
    }

    private void RouteLoserToLB(int wbR, int wbM, string loser)
    {
        TournamentMatch lbMatch;
        bool asP1;

        if (wbR == 0)
        {
            // WBR0 match M → LBR0 match M/2; P1 if M is even, P2 if odd
            lbMatch = LBRounds[0][wbM / 2];
            asP1    = wbM % 2 == 0;
        }
        else
        {
            // WBRk (k≥1) → LBR(2k-1) match M; always fills P2 (LB winner is P1)
            lbMatch = LBRounds[2 * wbR - 1][wbM];
            asP1    = false;
        }

        if (asP1) lbMatch.Player1 = loser;
        else      lbMatch.Player2 = loser;

        TryAutoCompleteLB(lbMatch);
    }

    private void AdvanceLBMatch(TournamentMatch match)
    {
        int lbR = match.RoundIndex;
        int lbM = match.MatchIndex;

        if (lbR == LBRounds.Count - 1)
        {
            // LB Finals winner → GF Player2 (LB champion seat)
            GrandFinalsMatch!.Player2 = match.Winner;
            return;
        }

        // Route winner to next LB round
        TournamentMatch next;
        bool asP1;

        // Odd LBR (1,3,5…) are carry+feed → next round is cull (even); use pairing (m/2)
        // Even LBR (0,2,4…) are feed/cull → next round is carry (odd); same index, always P1
        if (lbR % 2 == 1)
        {
            next = LBRounds[lbR + 1][lbM / 2];
            asP1 = lbM % 2 == 0;
        }
        else
        {
            next = LBRounds[lbR + 1][lbM];
            asP1 = true;
        }

        if (asP1) next.Player1 = match.Winner;
        else      next.Player2 = match.Winner;

        TryAutoCompleteLB(next);
    }

    private void TryAutoCompleteLB(TournamentMatch m)
    {
        if (m.Player1 == null || m.Player2 == null) return;

        if (m.Player1 == "BYE" || m.Player2 == "BYE")
        {
            m.Winner = m.Player1 == "BYE" ? m.Player2 : m.Player1;
            m.Status = MatchStatus.Bye;
            AdvanceLBMatch(m);
        }
        // else: both real players — match stays Pending, ready to play
    }

    private void HandleGFResult(TournamentMatch match)
    {
        // Reset game?
        if (match == GrandFinalsReset)
        {
            Champion   = match.Winner;
            IsComplete = true;
            return;
        }

        // Regular GF: P1 is always WB champion
        if (match.Winner == match.Player1)
        {
            Champion   = match.Player1;
            IsComplete = true;
        }
        else
        {
            // LB champion won — bracket reset available; host decides
            GrandFinalsNeedsReset = true;
        }
    }

    /// <summary>Host triggers optional second GF game after LB champion wins game 1.</summary>
    public void TriggerBracketReset()
    {
        if (!GrandFinalsNeedsReset || GrandFinalsMatch == null) return;
        GrandFinalsNeedsReset = false;
        GrandFinalsReset = new TournamentMatch
        {
            Player1 = GrandFinalsMatch.Player1,
            Player2 = GrandFinalsMatch.Player2,
            Side    = BracketSide.GrandFinals,
            RoundIndex = 0, MatchIndex = 1,
        };
    }

    /// <summary>Host skips the bracket reset and declares LB champion the winner.</summary>
    public void DeclareChampionWithoutReset()
    {
        if (!GrandFinalsNeedsReset || GrandFinalsMatch == null) return;
        Champion              = GrandFinalsMatch.Winner;
        IsComplete            = true;
        GrandFinalsNeedsReset = false;
    }

    // ── Bracket repair ────────────────────────────────────────────────────

    /// <summary>All matches in dependency order: WB rounds, LB rounds, GF, GF reset.</summary>
    public IEnumerable<TournamentMatch> EnumerateMatches()
    {
        if (Format == BracketFormat.DoubleElim)
        {
            foreach (var r in WBRounds) foreach (var m in r) yield return m;
            foreach (var r in LBRounds) foreach (var m in r) yield return m;
            if (GrandFinalsMatch != null) yield return GrandFinalsMatch;
            if (GrandFinalsReset != null) yield return GrandFinalsReset;
        }
        else
        {
            foreach (var r in Rounds) foreach (var m in r) yield return m;
        }
    }

    /// <summary>
    /// Clears a completed result by resetting the bracket to creation state and
    /// replaying every OTHER recorded result in dependency order. Results that
    /// no longer validate (their winner doesn't reach that match anymore) are
    /// dropped — i.e., everything downstream of the cleared result reverts to
    /// pending. Returns the number of downstream results dropped, or -1 if the
    /// target wasn't a clearable result.
    /// </summary>
    public int ClearResult(TournamentMatch target)
    {
        if (!target.IsCompleted || target.IsBye || target.Winner == null) return -1;

        // 1) Snapshot results + live markers (replay order = EnumerateMatches order)
        var completed  = new List<(BracketSide side, int r, int m, string winner, Guid? gameId)>();
        var inProgress = new List<(BracketSide side, int r, int m)>();
        bool hadReset  = GrandFinalsReset != null;

        foreach (var match in EnumerateMatches())
        {
            if (ReferenceEquals(match, target)) continue;
            if (match.IsCompleted && !match.IsBye && match.Winner != null)
                completed.Add((match.Side, match.RoundIndex, match.MatchIndex, match.Winner, match.GameId));
            else if (match.Status == MatchStatus.InProgress)
                inProgress.Add((match.Side, match.RoundIndex, match.MatchIndex));
        }

        // 2) Reset to creation state (round-0 seats are fixed at creation)
        ResetDerivedState();

        // 3) Replay
        int applied = 0;
        foreach (var e in completed)
        {
            var match = FindMatch(e.side, e.r, e.m);
            if (match == null && e.side == BracketSide.GrandFinals && e.m == 1 &&
                GrandFinalsNeedsReset && hadReset)
            {
                TriggerBracketReset(); // GF reset match only exists after the host triggers it
                match = GrandFinalsReset;
            }
            if (match == null) continue;
            if (match.Status != MatchStatus.Pending) continue;                          // bye'd away
            if (match.Player1 == null || match.Player2 == null) continue;               // seat never filled
            if (e.winner != match.Player1 && e.winner != match.Player2) continue;       // winner no longer here
            RecordWinner(match, e.winner);
            match.GameId = e.gameId;
            applied++;
        }

        // Host had triggered the GF reset but it wasn't played yet — restore the match
        if (GrandFinalsNeedsReset && hadReset && GrandFinalsReset == null)
            TriggerBracketReset();

        // 4) Restore live-match markers where the pairing survived
        foreach (var e in inProgress)
        {
            var match = FindMatch(e.side, e.r, e.m);
            if (match is { Status: MatchStatus.Pending, BothPlayersReady: true })
                match.Status = MatchStatus.InProgress;
        }

        return completed.Count - applied;
    }

    /// <summary>Renames a player everywhere in the bracket (seats, winners, champion).</summary>
    public void RenamePlayer(string oldName, string newName)
    {
        foreach (var m in EnumerateMatches())
        {
            if (string.Equals(m.Player1, oldName, StringComparison.OrdinalIgnoreCase)) m.Player1 = newName;
            if (string.Equals(m.Player2, oldName, StringComparison.OrdinalIgnoreCase)) m.Player2 = newName;
            if (string.Equals(m.Winner,  oldName, StringComparison.OrdinalIgnoreCase)) m.Winner  = newName;
        }
        if (string.Equals(Champion, oldName, StringComparison.OrdinalIgnoreCase))
            Champion = newName;
    }

    private TournamentMatch? FindMatch(BracketSide side, int r, int m) => side switch
    {
        BracketSide.GrandFinals => m == 0 ? GrandFinalsMatch : GrandFinalsReset,
        BracketSide.Losers      => r < LBRounds.Count && m < LBRounds[r].Count ? LBRounds[r][m] : null,
        _ => Format == BracketFormat.DoubleElim
            ? (r < WBRounds.Count && m < WBRounds[r].Count ? WBRounds[r][m] : null)
            : (r < Rounds.Count   && m < Rounds[r].Count   ? Rounds[r][m]   : null),
    };

    /// <summary>Clears all derived state back to how the factory left it; re-runs bye propagation.</summary>
    private void ResetDerivedState()
    {
        IsComplete = false;
        Champion   = null;

        if (Format == BracketFormat.SingleElim)
        {
            for (int r = 0; r < Rounds.Count; r++)
            foreach (var m in Rounds[r])
            {
                if (r == 0)
                {
                    if (m.Status == MatchStatus.Bye) continue; // creation-time bye — keep as-is
                    m.Winner = null; m.Status = MatchStatus.Pending; m.GameId = null;
                }
                else
                {
                    m.Player1 = null; m.Player2 = null; m.Winner = null;
                    m.Status  = MatchStatus.Pending; m.GameId = null;
                }
            }
            PropagateByes();
            return;
        }

        GrandFinalsNeedsReset = false;
        GrandFinalsReset      = null;
        for (int r = 0; r < WBRounds.Count; r++)
        foreach (var m in WBRounds[r])
        {
            if (r == 0)
            {
                if (m.Status == MatchStatus.Bye) continue;
                m.Winner = null; m.Status = MatchStatus.Pending; m.GameId = null;
            }
            else
            {
                m.Player1 = null; m.Player2 = null; m.Winner = null;
                m.Status  = MatchStatus.Pending; m.GameId = null;
            }
        }
        foreach (var round in LBRounds)
        foreach (var m in round)
        {
            m.Player1 = null; m.Player2 = null; m.Winner = null;
            m.Status  = MatchStatus.Pending; m.GameId = null;
        }
        if (GrandFinalsMatch != null)
        {
            GrandFinalsMatch.Player1 = null; GrandFinalsMatch.Player2 = null;
            GrandFinalsMatch.Winner  = null; GrandFinalsMatch.Status  = MatchStatus.Pending;
            GrandFinalsMatch.GameId  = null;
        }
        PropagateByesDE();
    }

    private void PropagateByesDE()
    {
        foreach (var match in WBRounds[0])
        {
            if (match.Status != MatchStatus.Bye || match.Winner == null) continue;

            // Advance WB bye winner to WBR1
            if (WBRounds.Count > 1)
            {
                var nextWB = WBRounds[1][match.MatchIndex / 2];
                if (match.MatchIndex % 2 == 0) nextWB.Player1 = match.Winner;
                else                            nextWB.Player2 = match.Winner;
            }
            else
            {
                GrandFinalsMatch!.Player1 = match.Winner;
            }

            // Route the "loser" side (BYE) into LBR0 so that slot is marked and can auto-advance
            if (LBRounds.Count > 0)
            {
                var lbMatch = LBRounds[0][match.MatchIndex / 2];
                if (match.MatchIndex % 2 == 0) lbMatch.Player1 = "BYE";
                else                            lbMatch.Player2 = "BYE";
                TryAutoCompleteLB(lbMatch);
            }
        }
    }
}
