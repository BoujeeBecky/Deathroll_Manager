using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DeathrollManager.Helpers;
using DeathrollManager.Models;
using DeathrollManager.Services;
using Dalamud.Bindings.ImGui;

namespace DeathrollManager.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private GameStateService   GameState      => plugin.GameState;
    private TournamentService  TournamentSvc  => plugin.TournamentService;
    private Configuration      Config         => plugin.Configuration;

    // ── New-game form state ───────────────────────────────────────────────
    private string newPlayer1 = string.Empty;
    private string newPlayer2 = string.Empty;
    private int    newStarting;
    private string newBetStr  = "0";
    private string newVenue   = string.Empty;

    // ── Leaderboard state ─────────────────────────────────────────────────
    private int lbSelectedVenueIndex = 0;

    // ── Help tab state ────────────────────────────────────────────────────
    private int _helpSection = 0;

    // ── Single-match roll-off state ───────────────────────────────────────
    private bool    _soloRollOffActive;
    private int?    _soloRollOffP1Roll;
    private int?    _soloRollOffP2Roll;
    private bool    _soloRollOffTied;
    private string? _soloRollOffFirstRoller;

    // ── Game-over flash animation ─────────────────────────────────────────
    private double gameOverFlashUntil = 0;
    private DeathrollGame? lastCompletedGame;

    // ── Unmatched roll quick-start hint ───────────────────────────────────
    private string? hintPlayer;
    private int     hintMax;

    public MainWindow(Plugin plugin) : base("Deathroll Manager###DRMain")
    {
        this.plugin = plugin;
        newStarting = plugin.Configuration.DefaultStartingNumber;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(480, 540),
            MaximumSize = new(720, 920),
        };

        GameState.GameCompleted += OnGameCompleted;
    }

    public override void OnClose()
    {
        GameState.GameCompleted -= OnGameCompleted;
    }

    public void OnUnmatchedRoll(string playerName, int rolled, int outOf)
    {
        // Intercept /random 10 rolls for an active single-match roll-off
        if (_soloRollOffActive && outOf == 10)
        {
            bool isP1 = string.Equals(playerName, newPlayer1.Trim(), StringComparison.OrdinalIgnoreCase);
            bool isP2 = string.Equals(playerName, newPlayer2.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isP1 || isP2)
            {
                if (isP1) _soloRollOffP1Roll = rolled;
                if (isP2) _soloRollOffP2Roll = rolled;

                if (_soloRollOffP1Roll.HasValue && _soloRollOffP2Roll.HasValue)
                {
                    if (_soloRollOffP1Roll.Value == _soloRollOffP2Roll.Value)
                    {
                        _soloRollOffTied   = true;
                        _soloRollOffP1Roll = null;
                        _soloRollOffP2Roll = null;
                    }
                    else
                    {
                        _soloRollOffTied        = false;
                        _soloRollOffFirstRoller = _soloRollOffP1Roll.Value > _soloRollOffP2Roll.Value
                            ? newPlayer1.Trim() : newPlayer2.Trim();
                    }
                }
                IsOpen = true;
                return;
            }
        }

        hintPlayer = playerName;
        hintMax    = outOf;
        IsOpen     = true;
    }

    private void ClearSoloRollOff()
    {
        _soloRollOffActive      = false;
        _soloRollOffP1Roll      = null;
        _soloRollOffP2Roll      = null;
        _soloRollOffTied        = false;
        _soloRollOffFirstRoller = null;
    }

    private void OnGameCompleted(DeathrollGame game)
    {
        lastCompletedGame  = game;
        gameOverFlashUntil = ImGui.GetTime() + 3.0;
    }

    // ── Main draw ─────────────────────────────────────────────────────────

    public override void Draw()
    {
        DrawHeader();

        if (ImGui.BeginTabBar("##DRTabs"))
        {
            if (ImGui.BeginTabItem("Game"))        { DrawGameTab();                        ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Battle"))      { plugin.BattleRenderer.Draw();         ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Tournament"))  { DrawTournamentTab();                  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("History"))     { DrawHistoryTab();                     ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Leaderboard")) { DrawLeaderboardTab();                 ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Help"))        { DrawHowToUseTab();                    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("About"))       { DrawAboutTab();                       ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ── Header ────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.5) * 0.4 + 0.6);
        var w = ImGui.GetContentRegionAvail().X;
        const string title = "🎲 DEATHROLL MANAGER";
        ImGui.SetCursorPosX((w - ImGui.CalcTextSize(title).X) * 0.5f);
        ImGui.TextColored(Theme.Gold * new Vector4(1, 1, 1, pulse), title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ── Game Tab ──────────────────────────────────────────────────────────

    private void DrawGameTab()
    {
        if (Config.FlashOnGameOver && ImGui.GetTime() < gameOverFlashUntil && lastCompletedGame != null)
        {
            DrawGameOverBanner(lastCompletedGame);
            return;
        }

        var game = GameState.ActiveGame;
        if (game != null) DrawActiveGame(game);
        else              DrawStartGamePanel();
    }

    private void DrawGameOverBanner(DeathrollGame game)
    {
        float t          = (float)((gameOverFlashUntil - ImGui.GetTime()) / 3.0);
        float flashAlpha = Math.Clamp(t * 2f, 0f, 1f);

        var dl  = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos() + new Vector2(0, ImGui.GetCursorPosY());
        dl.AddRectFilled(min, min + ImGui.GetContentRegionAvail(),
            Theme.ToU32(Theme.LosRed with { W = 0.25f * flashAlpha }));

        ImGui.Spacing();
        CenteredText("GAME OVER", Theme.Danger * new Vector4(1, 1, 1, flashAlpha));
        ImGui.Spacing();
        CenteredText($"💀 {game.LoserName} rolled a 1!", Theme.White);
        ImGui.Spacing();

        if (game.BetAmount > 0 && Config.ShowBetInWindow)
        {
            CenteredText($"Owes {game.WinnerName}", Theme.Muted);
            CenteredText($"{game.BetAmount:N0} gil", Theme.Gold);
            ImGui.Spacing();
        }

        CenteredText($"🏆 {game.WinnerName} wins!", Theme.WinGreen * new Vector4(1, 1, 1, flashAlpha));
        ImGui.Spacing();

        const float btnW = 160f;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - btnW) * 0.5f);
        if (ImGui.Button("View Summary", new Vector2(btnW, 0)))
            gameOverFlashUntil = 0;
    }

    private void DrawActiveGame(DeathrollGame game)
    {
        var avail = ImGui.GetContentRegionAvail().X;

        // Player cards
        DrawPlayerCard(game.Player1Name, Theme.Player1, game.CurrentPlayerTurn == game.Player1Name, avail);
        ImGui.SameLine();
        var vs = "VS";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail * 0.5f - ImGui.GetCursorPosX() - ImGui.CalcTextSize(vs).X * 0.5f));
        ImGui.TextColored(Theme.Muted, vs);
        ImGui.SameLine();
        DrawPlayerCard(game.Player2Name, Theme.Player2, game.CurrentPlayerTurn == game.Player2Name, avail);

        ImGui.Spacing();
        if (Config.ShowBetInWindow && game.BetAmount > 0)
            CenteredText($"Bet: {game.BetAmount:N0} gil", Theme.Gold);

        // Big current-max number
        ImGui.Spacing();
        var dangerColor = Theme.DangerGradient(game.DangerLevel);
        CenteredText("CURRENT MAX", Theme.Muted);
        var maxStr = game.CurrentMax.ToString("N0");
        ImGui.SetWindowFontScale(Math.Clamp(2.5f - maxStr.Length * 0.15f, 1.2f, 2.5f));
        CenteredText(maxStr, dangerColor);
        ImGui.SetWindowFontScale(1f);

        // Danger bar
        ImGui.Spacing();
        DrawDangerBar(game.SafeFraction, ImGui.GetContentRegionAvail().X, 18f, dangerColor);
        CenteredText($"{(int)(game.SafeFraction * 100f)}% of {game.StartingNumber:N0} remaining", Theme.Muted);

        // Turn indicator + Roll button
        ImGui.Spacing();
        ImGui.Separator();
        DrawTurnIndicator(game);
        ImGui.Separator();
        ImGui.Spacing();

        // Roll history
        ImGui.TextColored(Theme.Gold, "Roll History");
        float histH = ImGui.GetContentRegionAvail().Y - 32f;
        if (ImGui.BeginChild("##RollHistory", new Vector2(0, histH), true))
        {
            if (game.Rolls.Count == 0)
            {
                ImGui.TextColored(Theme.Muted, "Waiting for first roll...");
            }
            else
            {
                for (int i = game.Rolls.Count - 1; i >= 0; i--)
                {
                    var roll   = game.Rolls[i];
                    bool isP1  = string.Equals(roll.PlayerName, game.Player1Name, StringComparison.OrdinalIgnoreCase);
                    ImGui.TextColored(isP1 ? Theme.Player1 : Theme.Player2, roll.PlayerName);
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Muted, "rolled");
                    ImGui.SameLine();
                    var rc = roll.IsGameOver
                        ? Theme.Danger
                        : Theme.DangerGradient(1f - (float)roll.RolledValue / game.StartingNumber);
                    ImGui.TextColored(rc, roll.RolledValue.ToString("N0"));
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Muted, $"(out of {roll.MaxValue:N0})");
                    if (Config.ShowRollTimestamps) { ImGui.SameLine(); ImGui.TextColored(Theme.Muted, $"  {roll.Timestamp:HH:mm:ss}"); }
                    if (roll.IsGameOver) { ImGui.SameLine(); ImGui.TextColored(Theme.Danger, " 💀 DEAD"); }
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Abandon Game")) GameState.AbandonGame();
    }

    // ── Player card ───────────────────────────────────────────────────────

    private static void DrawPlayerCard(string name, Vector4 color, bool isActive, float totalWidth)
    {
        var cardW = totalWidth * 0.40f;
        var cardH = 44f;
        var dl    = ImGui.GetWindowDrawList();
        var pos   = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(pos, pos + new Vector2(cardW, cardH),
            Theme.ToU32(isActive ? color with { W = 0.25f } : Theme.CardBg), 6f);
        dl.AddRect(pos, pos + new Vector2(cardW, cardH),
            Theme.ToU32(isActive ? color : Theme.CardBorder), 6f, ImDrawFlags.None, isActive ? 2f : 1f);

        if (isActive)
        {
            float pulse = (float)(Math.Sin(ImGui.GetTime() * 3.0) * 0.5 + 0.5);
            dl.AddRect(pos, pos + new Vector2(cardW, cardH),
                Theme.ToU32(color with { W = pulse * 0.6f }), 6f, ImDrawFlags.None, 3f);
        }

        var ts   = ImGui.CalcTextSize(name);
        var tpos = pos + new Vector2((cardW - ts.X) * 0.5f, (cardH - ts.Y) * 0.5f);
        dl.AddText(tpos, Theme.ToU32(isActive ? color : Theme.Muted), name);

        ImGui.Dummy(new Vector2(cardW, cardH));
    }

    // ── Turn indicator + Roll button ──────────────────────────────────────

    private void DrawTurnIndicator(DeathrollGame game)
    {
        ImGui.Spacing();

        float pulse  = (float)(Math.Sin(ImGui.GetTime() * 4.0) * 0.5 + 0.5);
        bool  isP1   = game.CurrentPlayerTurn == game.Player1Name;
        var   color  = (isP1 ? Theme.Player1 : Theme.Player2) * new Vector4(1, 1, 1, 0.6f + pulse * 0.4f);
        CenteredText($"▶  {game.CurrentPlayerTurn}'s turn  ◀", color);

        ImGui.Spacing();

        // Determine if the local player is the one who needs to roll
        var localName = Plugin.PlayerState.CharacterName ?? string.Empty;
        bool isMyTurn = localName.Length > 0 &&
                        string.Equals(localName, game.CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase);

        float btnW  = 260f;
        float btnH  = 38f;
        var   avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX((avail - btnW) * 0.5f);

        if (isMyTurn)
        {
            // Pulsing green roll button
            var btnColor    = Vector4.Lerp(Theme.Safe,    Theme.WinGreen, pulse);
            var btnHovered  = Vector4.Lerp(Theme.WinGreen, Theme.Safe,    0.3f);
            var btnActive   = Theme.WinGreen with { W = 0.9f };

            ImGui.PushStyleColor(ImGuiCol.Button,        btnColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  btnActive);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);

            if (ImGui.Button($"🎲  Roll!  /random {game.CurrentMax}", new Vector2(btnW, btnH)))
                ChatSender.SendRandom(game.CurrentMax);

            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();
        }
        else
        {
            // Greyed-out waiting state
            ImGui.BeginDisabled();
            ImGui.Button($"🎲  /random {game.CurrentMax}", new Vector2(btnW, btnH));
            ImGui.EndDisabled();
            ImGui.SetCursorPosX((avail - ImGui.CalcTextSize($"Waiting for {game.CurrentPlayerTurn}…").X) * 0.5f);
            ImGui.TextColored(Theme.Muted, $"Waiting for {game.CurrentPlayerTurn}…");
        }

        ImGui.Spacing();
    }

    // ── Danger bar ────────────────────────────────────────────────────────

    private static void DrawDangerBar(float safeFraction, float width, float height, Vector4 fillColor)
    {
        var dl  = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(pos, pos + new Vector2(width, height),
            Theme.ToU32(new Vector4(0.10f, 0.10f, 0.18f, 1f)), 4f);

        float fillW = width * safeFraction;
        if (fillW > 2f)
        {
            dl.AddRectFilledMultiColor(pos, pos + new Vector2(fillW, height),
                Theme.ToU32(Theme.Safe),    // left edge
                Theme.ToU32(fillColor),     // right edge (danger-colored)
                Theme.ToU32(fillColor),
                Theme.ToU32(Theme.Safe));
        }

        dl.AddRect(pos, pos + new Vector2(width, height),
            Theme.ToU32(Theme.CardBorder), 4f, ImDrawFlags.None, 1f);

        ImGui.Dummy(new Vector2(width, height));
    }

    // ── Start New Game Panel ──────────────────────────────────────────────

    private void DrawStartGamePanel()
    {
        ImGui.Spacing();
        CenteredText("No Active Game", Theme.Muted);
        ImGui.Spacing();

        if (hintPlayer != null)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.ToU32(Theme.CardBg));
            if (ImGui.BeginChild("##Hint", new Vector2(0, 62), true))
            {
                ImGui.TextColored(Theme.Warning, "⚡ Roll detected!");
                ImGui.TextColored(Theme.White, $"{hintPlayer} rolled with a max of {hintMax:N0}.");
                ImGui.TextColored(Theme.Muted, "Fill in the form below and click Start Game.");
                ImGui.EndChild();
            }
            ImGui.PopStyleColor();
            ImGui.Spacing();

            if (newPlayer1.Length == 0) newPlayer1 = hintPlayer;
            if (newStarting == Config.DefaultStartingNumber && hintMax != Config.DefaultStartingNumber)
                newStarting = hintMax;
        }

        ImGui.TextColored(Theme.Gold, "Start New Game");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(180); ImGui.InputText("Player 1",        ref newPlayer1, 64);
        ImGui.SetNextItemWidth(180); ImGui.InputText("Player 2",        ref newPlayer2, 64);
        ImGui.SetNextItemWidth(120); ImGui.InputInt("Starting number",  ref newStarting);
        if (newStarting < 2) newStarting = 2;
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Bet (gil)", ref newBetStr, 20, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SetNextItemWidth(220); ImGui.InputText("Venue (optional)", ref newVenue, 80);

        ImGui.Spacing();

        bool canStart = newPlayer1.Trim().Length > 0
                     && newPlayer2.Trim().Length > 0
                     && newPlayer1.Trim() != newPlayer2.Trim()
                     && newStarting >= 2;

        if (!canStart && newPlayer1.Trim() == newPlayer2.Trim() && newPlayer1.Length > 0)
            ImGui.TextColored(Theme.Danger, "Players must be different!");

        // Roll-off panel
        if (_soloRollOffActive)
        {
            ImGui.Spacing();
            DrawSoloRollOffStatus();
            ImGui.Spacing();
        }
        else if (canStart)
        {
            ImGui.Spacing();
            if (ImGui.SmallButton("Roll Off"))
                _soloRollOffActive = true;
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, "/random 10 to decide who rolls first");
            ImGui.Spacing();
        }

        // Honour roll-off result by swapping player order if needed
        string p1 = newPlayer1.Trim();
        string p2 = newPlayer2.Trim();
        if (_soloRollOffFirstRoller != null &&
            string.Equals(_soloRollOffFirstRoller, p2, StringComparison.OrdinalIgnoreCase))
            (p1, p2) = (p2, p1);

        if (!canStart) ImGui.BeginDisabled();
        if (ImGui.Button("Start Game", new Vector2(140, 0)))
        {
            long bet = long.TryParse(newBetStr, out var b) ? Math.Max(b, 0) : 0;
            GameState.StartGame(p1, p2, newStarting, bet, newVenue.Trim());
            ClearSoloRollOff();
            hintPlayer  = null;
            newPlayer1  = newPlayer2 = string.Empty;
            newStarting = Config.DefaultStartingNumber;
            newBetStr   = "0";
            // newVenue intentionally kept — likely still at same venue
        }
        if (!canStart) ImGui.EndDisabled();

        if (_soloRollOffFirstRoller != null && canStart)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.WinGreen, $"{_soloRollOffFirstRoller} rolls first");
        }
    }

    // ── Tournament Tab ────────────────────────────────────────────────────

    private void DrawTournamentTab()
    {
        ImGui.Spacing();
        var t = TournamentSvc.ActiveTournament;

        if (t == null)
        {
            CenteredText("No active tournament.", Theme.Muted);
            ImGui.Spacing();
            CenteredText("Open the bracket window to create one.", Theme.Muted);
        }
        else
        {
            ImGui.TextColored(Theme.Gold, t.Name);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"  ·  start: {t.StartingNumber:N0}");
            if (t.BetAmount > 0) { ImGui.SameLine(); ImGui.TextColored(Theme.Gold, $"  {t.BetAmount:N0} gil"); }

            ImGui.Spacing();

            if (t.IsComplete)
            {
                float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.4 + 0.6);
                CenteredText($"🏆  Champion:  {t.Champion}", Theme.Gold * new Vector4(1, 1, 1, pulse));
            }
            else
            {
                var match = t.CurrentMatch;
                if (match != null)
                {
                    if (match.Status == MatchStatus.InProgress)
                    {
                        ImGui.TextColored(Theme.Warning, "▶ Current match:");
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.Player1, match.Player1 ?? "?");
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.Muted, " vs ");
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.Player2, match.Player2 ?? "?");
                    }
                    else if (match.BothPlayersReady)
                    {
                        ImGui.TextColored(Theme.Muted, "Next up:");
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.White, $"{match.Player1} vs {match.Player2}");
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Start"))
                            TournamentSvc.StartCurrentMatch();
                    }
                    else
                    {
                        ImGui.TextColored(Theme.Muted, "Waiting for matches to complete...");
                    }
                }

                // Progress summary
                ImGui.Spacing();
                int totalMatches    = 0, completedMatches = 0;
                foreach (var r in t.Rounds)
                    foreach (var m in r)
                    {
                        if (!m.IsBye) totalMatches++;
                        if (m.IsCompleted) completedMatches++;
                    }
                ImGui.TextColored(Theme.Muted, $"Progress: {completedMatches}/{totalMatches} matches complete");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float btnW = 220f;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - btnW) * 0.5f);
        if (ImGui.Button("Open Bracket Window", new Vector2(btnW, 30)))
            plugin.TournamentWindow.IsOpen = true;
    }

    // ── History Tab ───────────────────────────────────────────────────────

    private void DrawHistoryTab()
    {
        var history = GameState.History;
        if (history.Count == 0)
        {
            ImGui.Spacing();
            CenteredText("No games recorded yet.", Theme.Muted);
            return;
        }

        ImGui.TextColored(Theme.Muted, $"{history.Count} game(s) on record");
        ImGui.Spacing();

        if (!ImGui.BeginChild("##History", ImGui.GetContentRegionAvail(), false)) return;

        foreach (var game in history)
        {
            var label = $"{game.Player1Name} vs {game.Player2Name}  ·  {game.StartedAt:MM/dd HH:mm}";
            if (!ImGui.CollapsingHeader(label)) continue;

            ImGui.Indent(12f);
            if (game.Status == GameStatus.Completed)
            {
                ImGui.TextColored(Theme.WinGreen, $"🏆 Winner: {game.WinnerName}");
                ImGui.TextColored(Theme.Danger,   $"💀 Loser:  {game.LoserName}");
            }
            else
            {
                ImGui.TextColored(Theme.Muted, $"Status: {game.Status}");
            }

            if (game.BetAmount > 0) ImGui.TextColored(Theme.Gold, $"Bet: {game.BetAmount:N0} gil");
            ImGui.TextColored(Theme.Muted,
                $"Starting: {game.StartingNumber:N0}  ·  Rolls: {game.Rolls.Count}  ·  Duration: {game.Duration:mm\\:ss}");
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Rolls:");
            foreach (var roll in game.Rolls)
            {
                var rc = roll.IsGameOver ? Theme.Danger : Theme.White;
                ImGui.TextColored(rc, $"  {roll.PlayerName}: {roll.RolledValue:N0} / {roll.MaxValue:N0}{(roll.IsGameOver ? "  ← 💀" : "")}");
            }
            ImGui.Unindent(12f);
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    // ── Leaderboard Tab ───────────────────────────────────────────────────

    private void DrawLeaderboardTab()
    {
        var completed = GameState.History.Where(g => g.Status == GameStatus.Completed).ToList();

        ImGui.TextColored(Theme.Muted, $"{completed.Count} completed game(s) on record");
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##LBSubTabs"))
        {
            if (ImGui.BeginTabItem("Overall"))  { DrawOverallLB(completed);  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("By Venue")) { DrawVenueLB(completed);    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("My Stats")) { DrawMyStats(completed);    ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawOverallLB(List<DeathrollGame> completed)
    {
        if (completed.Count == 0)
        {
            ImGui.Spacing();
            CenteredText("No completed games yet.", Theme.Muted);
            return;
        }

        var stats  = ComputePlayerStats(completed);
        var ranked = stats.OrderByDescending(kv => kv.Value.wins)
                          .ThenByDescending(kv => kv.Value.winRate)
                          .ToList();

        ImGui.Spacing();
        DrawPlayerStatsTable(ranked);
    }

    private void DrawVenueLB(List<DeathrollGame> completed)
    {
        var venues = completed
            .Where(g => !string.IsNullOrWhiteSpace(g.VenueName))
            .Select(g => g.VenueName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

        if (venues.Count == 0)
        {
            ImGui.Spacing();
            CenteredText("No venue-tagged games yet.", Theme.Muted);
            ImGui.Spacing();
            CenteredText("Add a venue name when starting a game or tournament.", Theme.Muted);
            return;
        }

        ImGui.Spacing();
        if (lbSelectedVenueIndex >= venues.Count) lbSelectedVenueIndex = 0;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##Venue", venues[lbSelectedVenueIndex]))
        {
            for (int i = 0; i < venues.Count; i++)
            {
                bool sel = i == lbSelectedVenueIndex;
                if (ImGui.Selectable(venues[i], sel)) lbSelectedVenueIndex = i;
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var venueGames = completed
            .Where(g => string.Equals(g.VenueName, venues[lbSelectedVenueIndex], StringComparison.OrdinalIgnoreCase))
            .ToList();

        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, $"  {venueGames.Count} game(s)");
        ImGui.Spacing();

        var stats  = ComputePlayerStats(venueGames);
        var ranked = stats.OrderByDescending(kv => kv.Value.wins)
                          .ThenByDescending(kv => kv.Value.winRate)
                          .ToList();

        DrawPlayerStatsTable(ranked);
    }

    private void DrawMyStats(List<DeathrollGame> completed)
    {
        if (!Plugin.PlayerState.IsLoaded || string.IsNullOrEmpty(Plugin.PlayerState.CharacterName))
        {
            ImGui.Spacing();
            CenteredText("Not logged in — log in to see your personal stats.", Theme.Muted);
            return;
        }
        var localName = Plugin.PlayerState.CharacterName!;

        var myGames = completed
            .Where(g => string.Equals(g.Player1Name, localName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(g.Player2Name, localName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int   wins    = myGames.Count(g => string.Equals(g.WinnerName, localName, StringComparison.OrdinalIgnoreCase));
        int   losses  = myGames.Count - wins;
        int   total   = myGames.Count;
        float winRate = total > 0 ? (float)wins / total : 0f;
        long  gilWon  = myGames.Where(g => string.Equals(g.WinnerName, localName, StringComparison.OrdinalIgnoreCase))
                               .Sum(g => g.BetAmount);
        long  gilLost = myGames.Where(g => string.Equals(g.LoserName, localName, StringComparison.OrdinalIgnoreCase))
                               .Sum(g => g.BetAmount);
        long  netGil  = gilWon - gilLost;

        ImGui.Spacing();
        CenteredText(localName, Theme.Gold);
        ImGui.Spacing();

        // W / L / Win% stat boxes
        float avail = ImGui.GetContentRegionAvail().X;
        float boxW  = avail / 3f - 4f;
        DrawStatBox("Wins",     wins.ToString(),         Theme.WinGreen,                          boxW);
        ImGui.SameLine(avail / 3f + 2f);
        DrawStatBox("Losses",   losses.ToString(),       Theme.Danger,                            boxW);
        ImGui.SameLine(avail * 2f / 3f + 4f);
        DrawStatBox("Win Rate", $"{winRate * 100f:F1}%", Theme.DangerGradient(1f - winRate),      boxW);

        ImGui.Spacing();
        if (netGil != 0 || (gilWon == 0 && gilLost == 0 && total > 0))
        {
            string gilStr  = netGil >= 0 ? $"+{netGil:N0}" : $"{netGil:N0}";
            var    gilCol  = netGil >= 0 ? Theme.WinGreen : Theme.Danger;
            CenteredText($"Net Gil:  {gilStr}", gilCol);
        }

        if (total == 0)
        {
            ImGui.Spacing();
            CenteredText("No completed games on record for you yet.", Theme.Muted);
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Venues attended
        var venues = myGames
            .Where(g => !string.IsNullOrWhiteSpace(g.VenueName))
            .Select(g => g.VenueName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

        if (venues.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, "Venues Attended");
            foreach (var v in venues)
            {
                int vW = myGames.Count(g => string.Equals(g.VenueName, v, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(g.WinnerName, localName, StringComparison.OrdinalIgnoreCase));
                int vT = myGames.Count(g => string.Equals(g.VenueName, v, StringComparison.OrdinalIgnoreCase));
                ImGui.TextColored(Theme.White, $"  {v}");
                ImGui.SameLine();
                ImGui.TextColored(Theme.WinGreen, $"  {vW}W");
                ImGui.SameLine();
                ImGui.TextColored(Theme.Danger,   $"/ {vT - vW}L");
            }
            ImGui.Spacing();
            ImGui.Separator();
        }

        // Recent games
        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, "Recent Games");
        ImGui.Spacing();

        if (!ImGui.BeginChild("##MyGames", ImGui.GetContentRegionAvail(), false)) return;

        foreach (var game in myGames.Take(20))
        {
            bool won      = string.Equals(game.WinnerName, localName, StringComparison.OrdinalIgnoreCase);
            var  resCol   = won ? Theme.WinGreen : Theme.Danger;
            string opponent = string.Equals(game.Player1Name, localName, StringComparison.OrdinalIgnoreCase)
                ? game.Player2Name : game.Player1Name;

            ImGui.TextColored(resCol, won ? "WIN " : "LOSS");
            ImGui.SameLine();
            ImGui.TextColored(Theme.White, $"vs {opponent}");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"  start:{game.StartingNumber:N0}");

            if (game.BetAmount > 0)
            {
                ImGui.SameLine();
                string prefix = won ? "+" : "-";
                ImGui.TextColored(resCol, $"  {prefix}{game.BetAmount:N0}g");
            }

            if (!string.IsNullOrWhiteSpace(game.VenueName))
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Muted, $"  @ {game.VenueName}");
            }

            ImGui.TextColored(Theme.Muted,
                $"    {game.StartedAt:MM/dd HH:mm}  ·  {game.Rolls.Count} rolls  ·  {game.Duration:mm\\:ss}");
        }

        ImGui.EndChild();
    }

    private static void DrawPlayerStatsTable(
        List<KeyValuePair<string, (int wins, int losses, float winRate, long netGil)>> ranked)
    {
        if (!ImGui.BeginTable("##LBTable", 6,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                ImGui.GetContentRegionAvail()))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#",       ImGuiTableColumnFlags.WidthFixed,   28f);
        ImGui.TableSetupColumn("Player",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("W",       ImGuiTableColumnFlags.WidthFixed,   36f);
        ImGui.TableSetupColumn("L",       ImGuiTableColumnFlags.WidthFixed,   36f);
        ImGui.TableSetupColumn("Win%",    ImGuiTableColumnFlags.WidthFixed,   56f);
        ImGui.TableSetupColumn("Net Gil", ImGuiTableColumnFlags.WidthFixed,   90f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < ranked.Count; i++)
        {
            var name = ranked[i].Key;
            var (wins, losses, winRate, netGil) = ranked[i].Value;

            // Gold / silver / bronze tint for top 3
            var nameColor = i switch
            {
                0 => Theme.Gold,
                1 => new Vector4(0.80f, 0.80f, 0.85f, 1f),
                2 => new Vector4(0.72f, 0.50f, 0.30f, 1f),
                _ => Theme.White,
            };

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextColored(Theme.Muted,  $"{i + 1}");
            ImGui.TableSetColumnIndex(1); ImGui.TextColored(nameColor,    name);
            ImGui.TableSetColumnIndex(2); ImGui.TextColored(Theme.WinGreen, wins.ToString());
            ImGui.TableSetColumnIndex(3); ImGui.TextColored(Theme.Danger,   losses.ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextColored(Theme.DangerGradient(1f - winRate), $"{winRate * 100f:F1}%");
            ImGui.TableSetColumnIndex(5);
            string gilStr = netGil >= 0 ? $"+{netGil:N0}" : $"{netGil:N0}";
            ImGui.TextColored(netGil >= 0 ? Theme.WinGreen : Theme.Danger, gilStr);
        }

        ImGui.EndTable();
    }

    private static void DrawStatBox(string label, string value, Vector4 valueColor, float width)
    {
        var   dl     = ImGui.GetWindowDrawList();
        var   pos    = ImGui.GetCursorScreenPos();
        float height = 54f;

        dl.AddRectFilled(pos, pos + new Vector2(width, height), Theme.ToU32(Theme.CardBg), 5f);
        dl.AddRect(pos, pos + new Vector2(width, height), Theme.ToU32(Theme.CardBorder), 5f);

        var labelSz = ImGui.CalcTextSize(label);
        dl.AddText(pos + new Vector2((width - labelSz.X) * 0.5f, 6f), Theme.ToU32(Theme.Muted), label);

        var valSz = ImGui.CalcTextSize(value);
        dl.AddText(pos + new Vector2((width - valSz.X) * 0.5f, 30f), Theme.ToU32(valueColor), value);

        ImGui.Dummy(new Vector2(width, height));
    }

    private static Dictionary<string, (int wins, int losses, float winRate, long netGil)>
        ComputePlayerStats(IEnumerable<DeathrollGame> games)
    {
        var raw = new Dictionary<string, (int wins, int losses, long gilWon, long gilLost)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var game in games.Where(g => g.WinnerName != null && g.LoserName != null))
        {
            var winner = game.WinnerName!;
            var loser  = game.LoserName!;

            if (!raw.ContainsKey(winner)) raw[winner] = (0, 0, 0, 0);
            if (!raw.ContainsKey(loser))  raw[loser]  = (0, 0, 0, 0);

            var w = raw[winner]; raw[winner] = (w.wins + 1, w.losses,     w.gilWon + game.BetAmount, w.gilLost);
            var l = raw[loser];  raw[loser]  = (l.wins,     l.losses + 1, l.gilWon,                  l.gilLost + game.BetAmount);
        }

        return raw.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                int   total   = kv.Value.wins + kv.Value.losses;
                float winRate = total > 0 ? (float)kv.Value.wins / total : 0f;
                long  netGil  = kv.Value.gilWon - kv.Value.gilLost;
                return (kv.Value.wins, kv.Value.losses, winRate, netGil);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Solo roll-off status panel ────────────────────────────────────────

    private void DrawSoloRollOffStatus()
    {
        string p1 = newPlayer1.Trim();
        string p2 = newPlayer2.Trim();

        ImGui.TextColored(Theme.Gold, "Roll-off  ·  /random 10");
        ImGui.Separator();
        ImGui.Spacing();

        // P1 row
        ImGui.TextColored(Theme.Player1, p1);
        ImGui.SameLine();
        if (_soloRollOffP1Roll.HasValue)
            ImGui.TextColored(Theme.WinGreen, $"rolled {_soloRollOffP1Roll.Value}  ✓");
        else
            ImGui.TextColored(Theme.Muted, "waiting...");

        // P2 row
        ImGui.TextColored(Theme.Player2, p2);
        ImGui.SameLine();
        if (_soloRollOffP2Roll.HasValue)
            ImGui.TextColored(Theme.WinGreen, $"rolled {_soloRollOffP2Roll.Value}  ✓");
        else
            ImGui.TextColored(Theme.Muted, "waiting...");

        ImGui.Spacing();

        if (_soloRollOffTied)
            ImGui.TextColored(Theme.Warning, "⚡ Tie! Both players roll again.");
        else if (_soloRollOffFirstRoller != null)
            ImGui.TextColored(Theme.WinGreen, $"✓  {_soloRollOffFirstRoller} goes first!");
        else
            ImGui.TextColored(Theme.Muted, "Waiting for both players to roll...");

        ImGui.Spacing();
        if (ImGui.SmallButton("Cancel Roll-off"))
            ClearSoloRollOff();
    }

    // ── About Tab ─────────────────────────────────────────────────────────

    private void DrawAboutTab()
    {
        if (!ImGui.BeginChild("##AboutScroll", ImGui.GetContentRegionAvail(), false)) return;

        var avail = ImGui.GetContentRegionAvail().X;

        ImGui.Spacing();
        ImGui.Spacing();

        // Title
        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.3 + 0.7);
        CenteredText("🎲 Deathroll Manager", Theme.Gold * new Vector4(1, 1, 1, pulse));
        ImGui.Spacing();
        CenteredText("A free plugin for FFXIV deathroll venues", Theme.Muted);

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // About blurb
        ImGui.TextColored(Theme.Gold, "About");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Deathroll Manager was built to make running deathroll events at FFXIV venues smoother — " +
            "from tracking live games and animated battle scenes to full tournament brackets with relay " +
            "broadcasting and a live web viewer.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "The plugin is free and always will be. No paywalls, no locked features. " +
            "If you'd like to support ongoing development — server costs, new features, and keeping " +
            "the web toolkit running — a Ko-Fi donation means the world.");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // Ko-Fi button
        ImGui.TextColored(Theme.Gold, "Support the Project");
        ImGui.Spacing();
        ImGui.TextWrapped("Donations help cover hosting costs and fund future features like persistent leaderboards, venue profiles, and server-side tournament history.");
        ImGui.Spacing();

        const float tipBtnW = 190f;
        const float tipGap  = 10f;
        ImGui.SetCursorPosX((avail - tipBtnW * 2 - tipGap) * 0.5f);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.16f, 0.67f, 0.88f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.76f, 0.98f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.12f, 0.55f, 0.76f, 1.00f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        if (ImGui.Button("☕  Support on Ko-Fi", new Vector2(tipBtnW, 32)))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "https://ko-fi.com/boujeebecky", UseShellExecute = true });
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        ImGui.SameLine(0, tipGap);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.00f, 0.84f, 0.20f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.00f, 0.72f, 0.17f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.00f, 0.60f, 0.14f, 1.00f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        if (ImGui.Button("$  Tip on Cash App", new Vector2(tipBtnW, 32)))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "https://cash.app/$StormyRoxTips", UseShellExecute = true });
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // Credits
        ImGui.TextColored(Theme.Gold, "Credits");
        ImGui.Spacing();
        ImGui.TextColored(Theme.White,    "  Created by");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Gold,     "Boujee Becky");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted,    "— DC Primal");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "  Community organiser, venue host, and the reason this plugin exists. " +
            "If you see Becky at a venue, say hi — she's the social one.");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // Contact
        ImGui.TextColored(Theme.Gold, "Feature Requests & Contact");
        ImGui.Spacing();
        ImGui.TextWrapped("Have an idea, found a bug, or want to get in touch? Use the contact form on the toolkit site:");
        ImGui.Spacing();

        const float linkW = 220f;
        ImGui.SetCursorPosX((avail - linkW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.CardBg));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(new Vector4(0.2f, 0.2f, 0.3f, 1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.CardBorder));
        if (ImGui.Button("tometools.com/contact", new Vector2(linkW, 0)))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://tometools.com/contact",
                UseShellExecute = true,
            });
        }
        ImGui.PopStyleColor(3);

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "  Please don't send support requests to other players in-game.");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        CenteredText("Thank you for using Deathroll Manager  🎲", Theme.Muted);
        ImGui.Spacing();

        ImGui.EndChild();
    }

    // ── How To Use Tab ────────────────────────────────────────────────────

    private static readonly string[] HelpSections =
    {
        "Getting Started", "Battle Tab", "Single Matches", "Tournaments",
        "Brackets", "History", "Leaderboards", "Game Tracking",
        "Relay System", "Settings",
    };

    private void DrawHowToUseTab()
    {
        var avail = ImGui.GetContentRegionAvail();
        const float navW = 130f;

        // Left nav panel
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.ToU32(Theme.CardBg));
        if (ImGui.BeginChild("##HelpNav", new Vector2(navW, avail.Y), true))
        {
            ImGui.Spacing();
            for (int i = 0; i < HelpSections.Length; i++)
            {
                bool sel = _helpSection == i;
                if (sel) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ToU32(Theme.Gold));
                if (ImGui.Selectable($"  {HelpSections[i]}##hn{i}", sel))
                    _helpSection = i;
                if (sel) ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // Right content panel
        if (!ImGui.BeginChild("##HelpContent", ImGui.GetContentRegionAvail(), false)) return;

        void SectionHeader(string text)
        {
            ImGui.TextColored(Theme.Gold, text);
            ImGui.Separator();
            ImGui.Spacing();
        }

        void Body(string text)   => ImGui.TextWrapped(text);
        void Bullet(string text) => ImGui.TextWrapped("  · " + text);
        void Gap()               { ImGui.Spacing(); ImGui.Spacing(); }

        switch (_helpSection)
        {
            case 0: // Getting Started
                SectionHeader("Getting Started");
                Body("Deathroll Manager tracks /random deathroll games and tournament brackets for FFXIV venue events.");
                Body("Deathroll: two players agree on a starting number and bet. They alternate /random [max] using the previous result as the new max. Whoever rolls 1 loses.");
                Gap();
                SectionHeader("Commands");
                Bullet("/dr or /deathroll — open the main window");
                Bullet("/dr tournament — open the tournament bracket window");
                Bullet("/dr settings — open settings");
                Gap();
                SectionHeader("First Time Setup");
                Body("Open Settings and verify the Chat Channels match where your venue does deathrolls. Say (/random always outputs there) is always monitored. Enable Party, FC, Linkshell, or Yell as needed.");
                Gap();
                Body("Auto-detect is on by default — the plugin listens to chat and logs rolls automatically. Start a game with both player names before they begin rolling and the rest happens on its own.");
                break;

            case 1: // Battle Tab
                SectionHeader("Battle Tab");
                Body("The Battle tab shows an animated stick-figure duel that reacts to the live game in real time.");
                Gap();
                Bullet("Each /random roll triggers a sword-swing animation on the rolling player's side.");
                Bullet("HP bars are danger-tinted and shrink as the current max drops toward 1.");
                Bullet("When someone rolls 1, the loser's figure fades and shatters — the death animation plays out over ~1.3 seconds.");
                Bullet("After the animation the winner stands alone until a new game starts.");
                Gap();
                SectionHeader("Pop-out Window");
                Body("Enable Pop-out Battle Panel in Settings. When a new game starts, the battle scene opens as a separate floating window you can pin anywhere on screen — handy for streaming or dual monitors.");
                Body("Both the Battle tab and the pop-out window share the same animation state, so they stay perfectly in sync.");
                break;

            case 2: // Single Matches
                SectionHeader("Single Matches");
                Body("Use the Game tab to manually track a deathroll between two players.");
                Gap();
                SectionHeader("Starting a Game");
                Bullet("Player 1 / Player 2 — names exactly as they appear in FFXIV chat.");
                Bullet("Starting Number — the agreed-upon /random max (default from Settings).");
                Bullet("Bet — gil wagered. Shows in window and feeds the leaderboard net-gil stat.");
                Bullet("Venue — optional label used by the By Venue leaderboard.");
                Gap();
                SectionHeader("During a Game");
                Body("The danger bar and current max update with each detected roll. When it is your turn a pulsing green Roll button appears — click it to send /random [current max] automatically without typing.");
                Gap();
                Body("Roll history scrolls newest-first. Enable Show Timestamps in Settings to add HH:mm:ss to each entry.");
                Gap();
                SectionHeader("Game Over");
                Body("When someone rolls 1 the game-over flash fires (if enabled), then the Game tab shows the summary. The result is saved to History automatically and immediately reflected in the Leaderboard.");
                break;

            case 3: // Tournaments
                SectionHeader("Tournaments");
                Body("Open the bracket window with /dr tournament or the button in the Tournament tab.");
                Gap();
                SectionHeader("Setup");
                Bullet("Enter a name, starting number, bet, and venue.");
                Bullet("Add players one by one (press Enter to add quickly) or paste a comma-separated list using the CSV import button.");
                Bullet("The bracket rounds up to the next power of 2. BYEs fill the extra slots automatically and are skipped during play.");
                Gap();
                SectionHeader("Running Matches");
                Body("Click Start Match for the next pending match. This begins a live Deathroll game and the plugin tracks all rolls automatically. When someone rolls 1 the bracket advances — no manual input needed.");
                Gap();
                Body("Left-click any completed match to view the full roll-by-roll history. Right-click any match to force a winner (forfeit or no-show).");
                Gap();
                SectionHeader("MC Controls");
                Body("The MC Controls panel (in the bracket window) has one-click announcement buttons:");
                Bullet("Call Up — announce the next two players.");
                Bullet("Roll Off — prompt both players to /random 10 to decide who rolls first. The plugin reads results from chat automatically.");
                Bullet("Start Match — announce the starting number and who rolls first.");
                Bullet("Announce Winner — declare the round winner.");
                Body("Select the announcement channel (Say / Yell / Shout / Party / FC) at the top of MC Controls or in Settings.");
                break;

            case 4: // Brackets
                SectionHeader("Single Elimination");
                Body("One loss eliminates the player. Simple and fast — best for smaller events or when time is limited.");
                Gap();
                SectionHeader("Double Elimination");
                Body("Players survive their first loss by dropping into the Losers Bracket. They can fight back and still win the whole tournament.");
                Bullet("Winners Bracket (WB) — players who have not lost yet.");
                Bullet("Losers Bracket (LB) — players with exactly one loss. A second loss eliminates them.");
                Bullet("Grand Finals — WB champion vs LB champion.");
                Bullet("Grand Finals Reset — if the LB champion wins Grand Finals, a second match is played. The WB champion entered GF undefeated, so the reset gives a fair final.");
                Gap();
                SectionHeader("Layouts");
                Body("Each format supports two visual layouts:");
                Bullet("V-Bracket (Centre Finals) — left and right sub-brackets mirror inward; Grand Finals sits at the bottom-centre. Great for venues where the final match is the centrepiece event.");
                Bullet("Left to Right — traditional tree; rounds flow left to right. Easier for participants who are not familiar with the V format.");
                Gap();
                Body("Set your preferred default in Settings. You can also choose a layout per-tournament during setup.");
                break;

            case 5: // History
                SectionHeader("History");
                Body("Every completed game is saved automatically to history.json in your Dalamud plugin data folder. Nothing is lost when you close the plugin or restart the game.");
                Gap();
                SectionHeader("History Tab");
                Body("Games appear as collapsible rows, newest first. Expand any row to see:");
                Bullet("Winner and loser");
                Bullet("Bet amount (if any)");
                Bullet("Starting number, roll count, and match duration");
                Bullet("Full roll-by-roll breakdown with each player's value and the final 💀 roll");
                Gap();
                SectionHeader("What History Drives");
                Body("History is the single source of truth for Leaderboards, My Stats, and the tournament roll feed. Play a game, finish it — everything updates instantly.");
                break;

            case 6: // Leaderboards
                SectionHeader("Leaderboards");
                Body("All stats are computed live from History and update the moment a game completes. There is nothing to configure or sync.");
                Gap();
                SectionHeader("Overall");
                Body("All players ranked by wins, then by win rate. Top 3 are highlighted gold, silver, and bronze. Columns: Rank, Player, Wins, Losses, Win%, Net Gil.");
                Gap();
                SectionHeader("By Venue");
                Body("Same stats filtered to a single venue. Use the dropdown to switch venues. Only venues where games were recorded with a venue name will appear here.");
                Gap();
                SectionHeader("My Stats");
                Body("Your personal wins, losses, win rate, net gil, venues attended, and a recent-game list. Only visible when you are logged in — the plugin uses your character name to match records.");
                Gap();
                Body("Tip: always fill in the Venue field when starting a game or tournament. It is the only way to build a per-venue leaderboard.");
                break;

            case 7: // Game Tracking
                SectionHeader("Game Tracking");
                Body("The plugin listens to chat for the FFXIV dice roll message:");
                ImGui.Spacing();
                ImGui.TextColored(Theme.Warning, "  Random! [Name] rolls a [N] (out of [M]).");
                ImGui.Spacing();
                Gap();
                SectionHeader("Channels Monitored");
                Body("/random always outputs to the dedicated dice channel (Say / type 2122), which is always monitored. Enable Party, FC, Linkshell, or Yell in Settings if your venue uses those channels.");
                Gap();
                SectionHeader("Auto-detect");
                Body("When a roll comes in, the plugin checks if it matches the active game's expected player and max value. If it does, the roll is added automatically.");
                Gap();
                Body("If a roll does not match any active game, the Game tab shows an Unmatched Roll hint with the player name and max pre-filled so you can start tracking in one click.");
                Gap();
                SectionHeader("Tournament Rolls");
                Body("During a tournament match, the plugin listens the same way. When the active game ends with a roll of 1 the bracket advances automatically — no button press needed.");
                break;

            case 8: // Relay System
                SectionHeader("Relay System");
                Body("The relay keeps your live bracket in sync with spectators in two ways: encoded /say messages for in-game followers, and a live web viewer at tometools.com for anyone online.");
                Gap();
                SectionHeader("Hosting");
                Bullet("Click Start Relay in the Tournament window. A 6-character code is generated.");
                Bullet("Share the code with in-game spectators, or share the web link: tometools.com/bracket?code=XXXXXX");
                Bullet("⟳ Resync — re-sends the full bracket for late joiners. Sends 3–4 compressed /say messages regardless of bracket size or how far the tournament has progressed.");
                Bullet("Stop Relay — drops the send queue and sends an END signal immediately.");
                Gap();
                SectionHeader("Broadcast via /say (checkbox next to ⟳ Resync)");
                Body("When checked, bracket updates are broadcast via encoded /say messages — this is what allows in-game spectators to join with a code. The encoded text in chat is the relay system working as intended.");
                Body("Uncheck to disable /say broadcasting entirely. No relay messages appear in chat. Spectators use the tometools.com web link instead.");
                Gap();
                SectionHeader("Rate limiting");
                Body("FFXIV caps /say send speed. The relay queues one message per second so nothing is lost. A full resync for a 32-player bracket takes around 5 seconds to finish.");
                Gap();
                SectionHeader("Spectating In-Game");
                Bullet("Enter the 6-character code in the Tournament window relay section.");
                Bullet("The bracket appears read-only and updates in real time as each match completes.");
                Bullet("Click Leave to stop following. Ask the host to press ⟳ Resync if the tournament is already in progress when you join.");
                Gap();
                SectionHeader("Web Viewer");
                Body("tometools.com/bracket?code=XXXXXX shows the full bracket in a browser — no plugin required. Updates every 5 seconds. Works on mobile and is ideal for streaming.");
                Gap();
                SectionHeader("Notes");
                Body("MC announcement buttons (call-up, roll-off, winner) are separate from the relay and use the channel set in MC Controls (Say, Yell, Shout, Party, FC). The relay always uses /say.");
                Body("Relay messages are tied to the first sender detected. Others sending the same code are silently ignored — this prevents bracket spoofing.");
                break;

            case 9: // Settings
                SectionHeader("Chat Channels");
                Body("Toggle which channels to monitor for /random rolls. Say is the native /random channel and is always on. Enable others to match where your venue does deathrolls.");
                Gap();
                SectionHeader("Gameplay");
                Bullet("Auto-detect games — automatically match chat rolls to the active game.");
                Bullet("Show bet in window — display the wager amount during active games.");
                Bullet("Show timestamps — add HH:mm:ss to each roll in the roll history.");
                Bullet("Flash on game over — 3-second red overlay when someone rolls 1.");
                Bullet("Pop-out battle panel — auto-opens the animated Battle scene as a floating window when a game starts.");
                Gap();
                SectionHeader("Defaults");
                Bullet("Default starting number — pre-fills the starting number for new games and tournaments.");
                Bullet("Default bracket layout — V-Bracket or Left-to-Right; applies to new tournaments.");
                break;
        }

        ImGui.EndChild();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void CenteredText(string text, Vector4 color)
    {
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (w - ImGui.CalcTextSize(text).X) * 0.5f);
        ImGui.TextColored(color, text);
    }
}
