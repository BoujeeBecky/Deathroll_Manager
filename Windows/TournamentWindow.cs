using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Interface.Windowing;
using DeathrollManager.Helpers;
using DeathrollManager.Models;
using DeathrollManager.Services;
using Dalamud.Bindings.ImGui;

namespace DeathrollManager.Windows;

public class TournamentWindow : Window
{
    private readonly Plugin plugin;
    private TournamentService TournamentSvc => plugin.TournamentService;

    // ── Setup form ────────────────────────────────────────────────────────
    private string       newTournamentName = "Deathroll Tournament";
    private string       newVenue          = string.Empty;
    private readonly List<string> playerList = new();
    private string       newPlayerInput = string.Empty;
    private int          newStarting;
    private string       newBetStr = "0";
    private string?      duplicateWarning;
    private string       importPath   = string.Empty;
    private string?      importResult;

    // ── Test mode ────────────────────────────────────────────────────────
    private bool _testMode = false;

    private static readonly string[] TestPlayerPool =
    [
        "Dice Goblin",      "Lucky Sevens",     "Nat One",
        "Crit Fail",        "All In Alice",      "Bankrupt Bob",
        "Clutch Charlie",   "Double Down Dan",   "Even Odds Eve",
        "Full Send Fred",   "Gamble Gremlin",    "High Roller Hana",
        "Inside Job Ian",   "Jackpot Jade",      "Kingpin Kyle",
        "Longshot Lila",    "Max Bet Max",       "One More Roll",
        "Poker Face Petra", "Quickdraw Quinn",   "Risk Taker Rex",
        "Safe Bet Sara",    "Tilt Mode Tim",     "Underdog Uma",
        "Variance Vince",   "Wild Card Wren",    "Yolo Yuki",
        "Zero Sum Zara",    "Skill Issue",       "Loaded Dice",
        "Bluff Master",     "Fold Enjoyer",      "Free Company",
        "Grand Company",    "Moogle Fan",        "Hildibrand",
        "Lucky Crit",       "Perpetual Miss",    "Ante Up",
        "House Always Wins","Broke Again",       "Final Answer",
    ];

    // ── Setup form extras ─────────────────────────────────────────────────
    private int   _pendingLayout = 0; // 0=VBracket 1=LeftToRight
    private int   _pendingFormat = 0; // 0=SingleElim 1=DoubleElim

    // ── Bracket scroll state ──────────────────────────────────────────────
    private Guid? _lastBracketId; // detects new tournament → re-center scroll

    // ── Relay UI state ────────────────────────────────────────────────────
    private string _watchCodeInput = string.Empty;

    // ── Match detail popup ────────────────────────────────────────────────
    private TournamentMatch? pendingDetailMatch;

    // ── Full report popup ─────────────────────────────────────────────────
    private string _reportText      = string.Empty;
    private bool   _openReportPopup;

    // ── Bracket search ────────────────────────────────────────────────────
    private string _searchFilter = string.Empty;

    // ── Bracket repair UI state ───────────────────────────────────────────
    private string? _renameOld;
    private string  _renameInput = string.Empty;
    private bool    _openRenamePopup;
    private string? _repairMsg;
    private double  _repairMsgUntil;

    // ── Default announcement macro templates ─────────────────────────────
    // Placeholders: {p1} {p2} {start} {first} {winner} {round} {match} {venue}
    private const string MacroCallUp     = "Next up: {p1} vs {p2}! Please make your way to the front.";
    private const string MacroRollOff    = "{p1} and {p2}, please both /random 10 to determine who rolls first!";
    private const string MacroStartMatch = "Deathroll begins at {start}! {first} rolls first — /random {start}";
    private const string MacroWinner     = "Round {round} winner: {winner}! Congratulations!";

    // ── Layout constants ──────────────────────────────────────────────────
    private const float SlotH    = 36f;
    private const float MatchW   = 200f;
    private const float RoundGap = 56f;
    private const float Padding  = 14f;
    private const float LabelH   = 26f;

    // Auto call-up dedup — (tournament, side, round, match) last announced
    private (Guid, BracketSide, int, int)? _lastAutoCallUp;

    public TournamentWindow(Plugin plugin) : base("Tournament Bracket###DRTournament")
    {
        this.plugin = plugin;
        plugin.TournamentService.TournamentStateChanged += OnAutoCallUp;
        newStarting    = plugin.Configuration.DefaultStartingNumber;
        _pendingLayout = plugin.Configuration.DefaultBracketLayout == BracketLayout.LeftToRight ? 1 : 0;
        _pendingFormat = plugin.Configuration.DefaultBracketFormat == BracketFormat.DoubleElim  ? 1 : 0;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(500, 400),
            MaximumSize = new(1600, 1000),
        };

    }

    public override void Draw()
    {
        var t     = TournamentSvc.ActiveTournament;
        var relay = plugin.RelayService;

        if (t == null && relay.IsSpectating)
        {
            DrawSpectatorView(relay);
            return;
        }

        if (t == null)
        {
            DrawSetupPanel();
            return;
        }

        DrawTournamentHeader(t);
        DrawRelayControls(t, relay);
        ImGui.Separator();

        if (_repairMsg != null && ImGui.GetTime() < _repairMsgUntil)
            ImGui.TextColored(Theme.Warning, $"🔧 {_repairMsg}");

        if (t.IsComplete)
            DrawChampionBanner(t);
        else
        {
            DrawCurrentMatchHint(t);
            DrawMCControls(t);
        }

        ImGui.Spacing();

        // Bracket search — pulses matching boxes gold ("where is so-and-so?")
        ImGui.SetNextItemWidth(170);
        ImGui.InputTextWithHint("##bracketSearch", "🔍 find player…", ref _searchFilter, 32);
        if (_searchFilter.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("✕##clrSearch")) _searchFilter = string.Empty;
            ImGui.SameLine();
            int hits = t.EnumerateMatches().Count(m => !m.IsBye && MatchesSearch(m));
            ImGui.TextColored(hits > 0 ? Theme.Warning : Theme.Muted,
                hits > 0 ? $"{hits} match(es) highlighted" : "no matches");
        }

        float availH   = ImGui.GetContentRegionAvail().Y;
        float feedH    = Math.Min(180f, availH * 0.30f);
        float bracketH = availH - feedH - 8f;

        if (ImGui.BeginChild("##BracketArea", new Vector2(0, bracketH), false))
        {
            DrawBracket(t);
            ImGui.EndChild();
        }

        ImGui.Spacing();
        DrawRollFeed(t);
        DrawMatchDetailPopup();
        DrawFullReportPopup();
        DrawRenamePopup();
    }

    // ── Header ────────────────────────────────────────────────────────────

    private void DrawTournamentHeader(Tournament t)
    {
        ImGui.TextColored(Theme.Gold, t.Name);
        ImGui.SameLine();

        if (_testMode)
        {
            ImGui.TextColored(Theme.Warning, " ◆ TEST");
            ImGui.SameLine();
        }

        int total = 0, done = 0;
        var allMatchRounds = t.Format == BracketFormat.DoubleElim
            ? t.WBRounds.Concat(t.LBRounds)
            : t.Rounds.AsEnumerable();
        foreach (var r in allMatchRounds)
            foreach (var m in r) { if (!m.IsBye) total++; if (m.IsCompleted) done++; }
        if (t.GrandFinalsMatch != null) { total++; if (t.GrandFinalsMatch.IsCompleted) done++; }
        if (t.GrandFinalsReset  != null) { total++; if (t.GrandFinalsReset.IsCompleted)  done++; }
        ImGui.TextColored(Theme.Muted,
            $"  ·  {TotalParticipants(t)} players  ·  {done}/{total} matches  ·  start: {t.StartingNumber:N0}");

        if (t.BetAmount > 0) { ImGui.SameLine(); ImGui.TextColored(Theme.Gold, $"  bet: {t.BetAmount:N0} gil"); }

        float btnAreaW = _testMode ? 360f : 260f;
        ImGui.SameLine(ImGui.GetWindowWidth() - btnAreaW);

        if (_testMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Warning with { W = 0.22f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Warning with { W = 0.38f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Warning with { W = 0.55f }));
            if (ImGui.SmallButton("Sim All →"))
                SimulateAll();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick random winners for all remaining matches");
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Copy Results"))
            ImGui.SetClipboardText(TournamentSvc.ExportBracketText());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy bracket to clipboard (paste in Discord/chat)");

        ImGui.SameLine();
        if (ImGui.SmallButton("📜 Full Report"))
        {
            _reportText      = TournamentSvc.ExportFullReport();
            _openReportPopup = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Review every match with its complete roll history\n— copy or export if the bracket needs fixing up");

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel Tournament"))
            ImGui.OpenPopup("##confirmCancelT");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Asks for confirmation first");

        if (ImGui.BeginPopup("##confirmCancelT"))
        {
            ImGui.TextColored(Theme.Warning, "Cancel this tournament?");
            ImGui.TextColored(Theme.Muted, "The entire bracket will be lost — this cannot be undone.");
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.ToU32(Theme.Danger with { W = 0.35f }));
            if (ImGui.Button("Yes, cancel it", new Vector2(120, 0)))
            {
                TournamentSvc.CancelTournament();
                _testMode = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Keep playing", new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawChampionBanner(Tournament t)
    {
        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.4 + 0.6);
        var w = ImGui.GetContentRegionAvail().X;
        float bannerH = 72f;

        ImGui.Dummy(new Vector2(w, bannerH));
        var bannerMin = ImGui.GetItemRectMin();
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(bannerMin, bannerMin + new Vector2(w, bannerH),
            Theme.ToU32(Theme.Gold with { W = 0.06f + pulse * 0.04f }), 8f);
        dl.AddRect(bannerMin, bannerMin + new Vector2(w, bannerH),
            Theme.ToU32(Theme.Gold with { W = 0.25f + pulse * 0.2f }), 8f, ImDrawFlags.None, 1.5f);

        string title = "🏆 TOURNAMENT CHAMPION 🏆";
        var titleSize = ImGui.CalcTextSize(title);
        dl.AddText(bannerMin + new Vector2((w - titleSize.X) * 0.5f, 8f),
            Theme.ToU32(Theme.Gold * new Vector4(1, 1, 1, pulse)), title);

        string nameText = t.Champion ?? "???";
        float nameFontSz = ImGui.GetFontSize() * 2.0f;
        var nameSize = ImGui.CalcTextSize(nameText) * 2.0f;
        dl.AddText(ImGui.GetFont(), nameFontSz,
            bannerMin + new Vector2((w - nameSize.X) * 0.5f, 26f),
            Theme.ToU32(Theme.WinGreen * new Vector4(1, 1, 1, 0.7f + pulse * 0.3f)), nameText);

        ImGui.Spacing();
    }

    private void DrawCurrentMatchHint(Tournament t)
    {
        var match = t.CurrentMatch;
        if (match == null) return;

        if (match.Status == MatchStatus.InProgress)
        {
            ImGui.TextColored(Theme.Warning, "▶  Match in progress:");
            ImGui.SameLine(); ImGui.TextColored(Theme.Player1, match.Player1 ?? "?");
            ImGui.SameLine(); ImGui.TextColored(Theme.Muted, " vs ");
            ImGui.SameLine(); ImGui.TextColored(Theme.Player2, match.Player2 ?? "?");
        }
        else if (match.BothPlayersReady)
        {
            ImGui.TextColored(Theme.Muted, "Next up:");
            ImGui.SameLine(); ImGui.TextColored(Theme.Player1, match.Player1 ?? "?");
            ImGui.SameLine(); ImGui.TextColored(Theme.Muted, " vs ");
            ImGui.SameLine(); ImGui.TextColored(Theme.Player2, match.Player2 ?? "?");
            ImGui.SameLine();

            if (_testMode)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Warning with { W = 0.22f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Warning with { W = 0.38f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Warning with { W = 0.55f }));
                if (ImGui.SmallButton("🎲 Random Win"))
                    SimulateMatch(match);
                ImGui.PopStyleColor(3);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Gold with { W = 0.25f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Gold with { W = 0.40f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Gold with { W = 0.55f }));
                if (ImGui.SmallButton("⚔ Start Match"))
                    TournamentSvc.StartCurrentMatch(TournamentSvc.RollOffFirstRoller);
                ImGui.PopStyleColor(3);
            }
        }
        else
        {
            ImGui.TextColored(Theme.Muted, "Waiting for previous matches to complete...");
        }
    }

    // Announces the next pairing the moment it becomes ready (match completes,
    // byes resolve, or the tournament starts). Skipped in test mode — Sim All
    // would flood the announcement channel.
    private void OnAutoCallUp()
    {
        if (!plugin.Configuration.AutoCallUp || _testMode) return;
        var t = TournamentSvc.ActiveTournament;
        if (t == null || t.IsComplete) return;

        var m = t.CurrentMatch;
        if (m == null || m.Status != MatchStatus.Pending || !m.BothPlayersReady) return;

        var key = (t.Id, m.Side, m.RoundIndex, m.MatchIndex);
        if (_lastAutoCallUp == key) return;
        _lastAutoCallUp = key;

        string cmd = Configuration.MCChannelCommand(plugin.Configuration.MCAnnouncementChannel);
        ChatSender.Send($"{cmd} {FormatMacro(MacroCallUp, m, t)}");
    }

    // ── MC Controls ───────────────────────────────────────────────────────

    private void DrawMCControls(Tournament t)
    {
        var svc       = TournamentSvc;
        var match     = t.CurrentMatch;
        bool hasMatch = match != null && !match.IsBye && match.BothPlayersReady;

        // Last completed non-bye match — for the Announce Winner button
        var lastDone  = AllMatchesOf(t).LastOrDefault(m => m.IsCompleted && !m.IsBye && m.Winner != null);

        ImGui.Spacing();
        if (!ImGui.TreeNodeEx("📣 MC Controls##MCCtrl", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // ── Channel selector + current match info (same line) ─────────────
        var chanIdx = (int)plugin.Configuration.MCAnnouncementChannel;
        ImGui.SetNextItemWidth(72);
        if (ImGui.Combo("##MCchan", ref chanIdx, "Say\0Yell\0Shout\0Party\0FC\0"))
        {
            plugin.Configuration.MCAnnouncementChannel = (MCChannel)chanIdx;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Channel for MC announcements\n(Relay protocol always uses /say regardless)");

        ImGui.SameLine();
        bool autoCU = plugin.Configuration.AutoCallUp;
        if (ImGui.Checkbox("Auto📢##autoCallUp", ref autoCU))
        {
            plugin.Configuration.AutoCallUp = autoCU;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically announce Call Up whenever the next match\nbecomes ready (saves a click per match). Off in test mode.");

        ImGui.SameLine();
        if (hasMatch)
        {
            ImGui.TextColored(Theme.Muted, "Match:");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Player1, match!.Player1 ?? "?");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, " vs ");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Player2, match.Player2 ?? "?");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"  ({GetMatchRoundLabel(t, match)})");
        }
        else
        {
            ImGui.TextColored(Theme.Muted, "No pending match.");
        }

        ImGui.Spacing();
        string cmd = Configuration.MCChannelCommand(plugin.Configuration.MCAnnouncementChannel);

        // ── Row 1: Call Up | Roll-off ─────────────────────────────────────
        bool canCallUp  = hasMatch;
        bool canRollOff = hasMatch && match!.Status == MatchStatus.Pending;

        MCButton("📢 Call Up##MCCallUp", canCallUp, Theme.WinGreen, () =>
        {
            ChatSender.Send($"{cmd} {FormatMacro(MacroCallUp, match, t)}");
        });
        ImGui.SameLine();
        if (ImGui.SmallButton("📋##CUcopy") && canCallUp)
            ImGui.SetClipboardText(FormatMacro(MacroCallUp, match, t));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy to clipboard");

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(10, 0)); ImGui.SameLine();

        MCButton("🎲 Roll-off##MCRollOff", canRollOff, Theme.Warning, () =>
        {
            ChatSender.Send($"{cmd} {FormatMacro(MacroRollOff, match, t)}");
            svc.StartRollOff(match!);
        });
        if (ImGui.IsItemHovered() && canRollOff)
            ImGui.SetTooltip("Sends the announcement and arms auto-detection\n(watches for both players to /random 10).\nYou can also just pick who rolls first below.");
        ImGui.SameLine();
        if (ImGui.SmallButton("📋##ROcopy") && canRollOff)
            ImGui.SetClipboardText(FormatMacro(MacroRollOff, match, t));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy to clipboard");

        // ── Roll-off status ───────────────────────────────────────────────
        if (svc.IsRollOffActive)
        {
            ImGui.Spacing();
            string p1s = svc.RollOffP1Roll.HasValue ? svc.RollOffP1Roll.Value.ToString() : "—";
            string p2s = svc.RollOffP2Roll.HasValue ? svc.RollOffP2Roll.Value.ToString() : "—";

            if (svc.RollOffFirstRoller != null)
            {
                ImGui.TextColored(Theme.WinGreen,
                    $"  ✓ {svc.RollOffFirstRoller} goes first!  ({svc.RollOffP1}: {p1s}  vs  {svc.RollOffP2}: {p2s})");
            }
            else if (svc.RollOffTied)
            {
                ImGui.TextColored(Theme.Warning, $"  ↺ TIE — both re-roll /random 10!");
            }
            else
            {
                ImGui.TextColored(Theme.Muted, $"  {svc.RollOffP1}: {p1s}    {svc.RollOffP2}: {p2s}");
            }
            ImGui.Spacing();
        }

        // ── First-roller pick (manual override) ──────────────────────────
        // Always available pre-match: if roll-off auto-detection misses the 10s
        // (or no roll-off was held), the host just clicks who goes first.
        if (hasMatch && match!.Status == MatchStatus.Pending)
        {
            ImGui.TextColored(Theme.Muted, "First roller:");
            ImGui.SameLine();
            DrawFirstRollerPick(svc, match, match.Player1!, Theme.Player1);
            ImGui.SameLine();
            DrawFirstRollerPick(svc, match, match.Player2!, Theme.Player2);

            if (svc.IsRollOffActive)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("✕ Cancel roll-off##ROCancel"))
                    svc.CancelRollOff();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Clear the roll-off and re-enable Start Match");
            }
        }

        // ── Row 2: Start Match | Announce Winner ──────────────────────────
        bool rollOffDone  = svc.RollOffFirstRoller != null;
        bool canStart     = hasMatch && match!.Status == MatchStatus.Pending && (!svc.IsRollOffActive || rollOffDone);
        string startLabel = rollOffDone
            ? $"⚔️ Start  ({svc.RollOffFirstRoller} first)##MCStart"
            : "⚔️ Start Match##MCStart";

        MCButton(startLabel, canStart, Theme.Gold, () =>
        {
            string? first = svc.RollOffFirstRoller;
            ChatSender.Send($"{cmd} {FormatMacro(MacroStartMatch, match, t, first: first ?? match?.Player1)}");
            svc.StartCurrentMatch(first);
        });
        if (ImGui.IsItemHovered() && canStart)
            ImGui.SetTooltip(FormatMacro(MacroStartMatch, match, t,
                first: svc.RollOffFirstRoller ?? match?.Player1));
        ImGui.SameLine();
        if (ImGui.SmallButton("📋##SMcopy") && match != null)
            ImGui.SetClipboardText(FormatMacro(MacroStartMatch, match, t,
                first: svc.RollOffFirstRoller ?? match.Player1));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy to clipboard");

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(10, 0)); ImGui.SameLine();

        bool canWinner = lastDone != null;
        MCButton("🏆 Announce Winner##MCWinner", canWinner, Theme.Gold, () =>
        {
            ChatSender.Send($"{cmd} {FormatMacro(MacroWinner, lastDone, t, winner: lastDone?.Winner)}");
        });
        if (ImGui.IsItemHovered() && canWinner)
            ImGui.SetTooltip(FormatMacro(MacroWinner, lastDone, t, winner: lastDone?.Winner));
        ImGui.SameLine();
        if (ImGui.SmallButton("📋##AWcopy") && canWinner)
            ImGui.SetClipboardText(FormatMacro(MacroWinner, lastDone, t, winner: lastDone?.Winner));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy to clipboard");

        // ── Live match extras: timer, nudge, manual roll entry ────────────
        if (match?.Status == MatchStatus.InProgress && plugin.GameState.ActiveGame is { } liveGame)
        {
            ImGui.Spacing();
            if (RollTimer.DrawInline(plugin.Configuration, liveGame))
                ImGui.SameLine();
            MCButton("⏰ Nudge##MCNudge", true, Theme.Warning, () =>
                ChatSender.Send($"{cmd} {liveGame.CurrentPlayerTurn} — /random {liveGame.CurrentMax} when you're ready! ⏰"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remind {liveGame.CurrentPlayerTurn} it's their roll");

            ManualRollEntry.Draw(plugin.GameState, "MC");
        }

        // ── Custom macros ─────────────────────────────────────────────────
        var customMacros = plugin.Configuration.AnnouncementMacros;
        if (customMacros.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Custom:");
            var contextMatch = match ?? lastDone;
            for (int i = 0; i < customMacros.Count; i++)
            {
                var cm  = customMacros[i];
                var msg = FormatMacro(cm.Template, contextMatch, t,
                    first: svc.RollOffFirstRoller, winner: lastDone?.Winner);
                if (i > 0) ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Player1 with { W = 0.18f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Player1 with { W = 0.32f }));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Player1 with { W = 0.48f }));
                if (ImGui.SmallButton($"{cm.Name}##CM{i}"))
                    ChatSender.Send($"{cmd} {msg}");
                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{cmd} {msg}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"📋##CMc{i}"))
                    ImGui.SetClipboardText(msg);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy to clipboard");
            }
        }

        ImGui.Spacing();
        ImGui.TreePop();
    }

    private bool MatchesSearch(TournamentMatch m) =>
        (m.Player1?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (m.Player2?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static void DrawFirstRollerPick(TournamentService svc, TournamentMatch match,
        string player, Vector4 color)
    {
        bool selected = string.Equals(svc.RollOffFirstRoller, player, StringComparison.OrdinalIgnoreCase);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(color with { W = selected ? 0.50f : 0.15f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(color with { W = 0.38f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(color with { W = 0.55f }));
        if (ImGui.SmallButton($"{(selected ? "✓ " : "")}{player}##first{player}"))
            svc.SetRollOffWinner(match, player);
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selected
                ? $"{player} rolls first"
                : $"Set {player} to roll first (manual roll-off winner)");
    }

    private static void MCButton(string label, bool enabled, Vector4 color, Action onClick)
    {
        if (enabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(color with { W = 0.22f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(color with { W = 0.38f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(color with { W = 0.55f }));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.10f)));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.10f)));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.10f)));
        }
        bool clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);
        if (clicked && enabled) onClick();
    }

    private static string FormatMacro(string template, TournamentMatch? m, Tournament? t,
        string? first = null, string? winner = null) =>
        template
            .Replace("{p1}",     m?.Player1                      ?? "?")
            .Replace("{p2}",     m?.Player2                      ?? "?")
            .Replace("{start}",  t?.StartingNumber.ToString()    ?? "?")
            .Replace("{first}",  first                           ?? m?.Player1 ?? "?")
            .Replace("{winner}", winner                          ?? m?.Winner  ?? "?")
            .Replace("{round}",  ((m?.RoundIndex ?? 0) + 1).ToString())
            .Replace("{match}",  ((m?.MatchIndex ?? 0) + 1).ToString())
            .Replace("{venue}",  t?.VenueName                    ?? "");

    private static string GetMatchRoundLabel(Tournament t, TournamentMatch m)
    {
        if (t.Format == BracketFormat.DoubleElim)
        {
            return m.Side switch
            {
                BracketSide.GrandFinals => m.MatchIndex == 1 ? "GF Reset" : "Grand Finals",
                BracketSide.Losers      => $"LB R{m.RoundIndex + 1}",
                _                       => $"WB R{m.RoundIndex + 1}",
            };
        }
        return m.RoundIndex == t.NumRounds - 1 ? "Finals" : $"Round {m.RoundIndex + 1}";
    }

    private static IEnumerable<TournamentMatch> AllMatchesOf(Tournament t)
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

    // ── Relay controls ────────────────────────────────────────────────────

    private void DrawRelayControls(Tournament t, TournamentRelayService relay)
    {
        ImGui.TextColored(Theme.Muted, "  📡");
        ImGui.SameLine();

        if (relay.IsHosting)
        {
            // Code as a clickable button — click copies it
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.15f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.28f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.40f }));
            if (ImGui.SmallButton(relay.RelayCode!))
                ImGui.SetClipboardText(relay.RelayCode!);
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Click to copy code  ·  Spectators type this in the Join Relay box");

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Link"))
                ImGui.SetClipboardText(relay.GetWebUrl());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Copy web viewer link  →  {relay.GetWebUrl()}");

            ImGui.SameLine();
            if (ImGui.SmallButton("⟳ Resync"))
                relay.ResyncBroadcast(t);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-broadcast full bracket for late joiners\n(sends 3–4 compressed /say messages)");

            ImGui.SameLine();
            bool broadcastToChat = relay.BroadcastToChat;
            if (ImGui.Checkbox("📡/say##RelayChat", ref broadcastToChat))
            {
                relay.BroadcastToChat = broadcastToChat;
                plugin.Configuration.RelayBroadcastToChat = broadcastToChat;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Broadcast bracket updates via /say (required for in-game spectators)\n" +
                    "Uncheck to use web viewer only — no /say messages will appear in chat");

            // Web sync health — failures used to be invisible outside the log
            if (relay.LastWebSyncAt.HasValue)
            {
                ImGui.SameLine();
                if (relay.LastWebSyncOk)
                {
                    int ago = (int)(DateTime.Now - relay.LastWebSyncAt.Value).TotalSeconds;
                    ImGui.TextColored(Theme.WinGreen, $"✓{ago}s");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Web viewer synced {ago}s ago");
                }
                else
                {
                    ImGui.TextColored(Theme.Danger, "⚠ web");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Last web sync FAILED — the web viewer may be stale.\nCheck your connection, then press ⟳ Resync.");
                }
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Danger with { W = 0.18f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Danger with { W = 0.32f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.LosRed  with { W = 0.55f }));
            if (ImGui.SmallButton("■ Stop Relay"))
                relay.StopHostRelay();
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.15f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.28f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.40f }));
            if (ImGui.SmallButton("Start Relay"))
                relay.StartHostRelay(t);
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Broadcast bracket updates via /say\nSpectators at the venue join with your code");
        }
    }

    // ── Spectator view ────────────────────────────────────────────────────

    private void DrawSpectatorView(TournamentRelayService relay)
    {
        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.4 + 0.6);

        ImGui.Spacing();
        ImGui.TextColored(Theme.WinGreen * new Vector4(1, 1, 1, pulse), "📡 SPECTATING");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Gold, $" {relay.WatchCode}");

        ImGui.SameLine(ImGui.GetWindowWidth() - 90f);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Danger with { W = 0.18f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Danger with { W = 0.32f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.LosRed  with { W = 0.55f }));
        if (ImGui.SmallButton("Leave Relay"))
            relay.LeaveRelay();
        ImGui.PopStyleColor(3);

        ImGui.Separator();
        ImGui.Spacing();

        if (relay.SpectatorStatusMsg != null)
            ImGui.TextColored(Theme.Muted, $"  {relay.SpectatorStatusMsg}");

        var t = relay.SpectatorTournament;
        if (t == null)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "  Waiting for host to broadcast bracket...");
            ImGui.TextColored(Theme.Muted, "  (Ask the host to press ⟳ Resync if the tournament is already running.)");
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, t.Name);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted,
            $"  ·  {TotalParticipants(t)} players  ·  start: {t.StartingNumber:N0}");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (t.IsComplete)
            DrawChampionBanner(t);

        float availH = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("##SpectatorBracket", new Vector2(0, availH), false))
        {
            DrawBracket(t, readOnly: true);
            ImGui.EndChild();
        }
    }

    // ── Bracket drawing ───────────────────────────────────────────────────

    private void DrawBracket(Tournament t, bool readOnly = false)
    {
        if (t.Format == BracketFormat.DoubleElim)
            DrawBracketDE(t, readOnly);
        else if (t.Layout == BracketLayout.LeftToRight)
            DrawBracketLR(t, readOnly);
        else
            DrawBracketV(t, readOnly);
    }

    private void DrawBracketV(Tournament t, bool readOnly = false)
    {
        int numRounds  = t.NumRounds;
        int totalSlots = t.TotalSlots;
        float colWidth = MatchW + RoundGap;

        if (numRounds <= 1)
        {
            float smallW = Padding * 2 + MatchW;
            float smallH = Padding * 2 + LabelH + SlotH * 2 + 4f;
            if (!ImGui.BeginChild("##BracketCanvas", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.None))
                return;
            ImGui.Dummy(new Vector2(smallW, smallH));
            var o  = ImGui.GetItemRectMin();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(o, o + new Vector2(smallW, smallH),
                Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
            PlaceRoundLabel(dl, o, Padding, "Finals", Theme.Gold);
            DrawMatchBox(dl, t, t.Rounds[0][0], o + new Vector2(Padding, LabelH), new Vector2(MatchW, SlotH * 2), readOnly);
            ImGui.EndChild();
            return;
        }

        int   numSubRounds = numRounds - 1;
        float numCols      = 2f * numSubRounds + 1f;

        // Fit bracket to available width by compressing the inter-round gap.
        // MatchW stays constant; only the gap shrinks. Fall back to scroll if too narrow.
        float availW  = ImGui.GetContentRegionAvail().X;
        float fitColW = (availW - Padding * 2f) / numCols;
        bool  fitMode = fitColW >= MatchW + 16f; // 16px minimum gap
        colWidth      = fitMode ? fitColW : MatchW + RoundGap;
        float dynGap  = fitMode ? (fitColW - MatchW) : RoundGap;

        float canvasW = Padding * 2f + numCols * colWidth;
        float canvasH = Padding * 2f + LabelH + (totalSlots / 2 + 1) * SlotH;

        var flags = fitMode ? ImGuiWindowFlags.None : ImGuiWindowFlags.HorizontalScrollbar;
        if (!ImGui.BeginChild("##BracketCanvas", ImGui.GetContentRegionAvail(), false, flags))
            return;

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        var origin   = ImGui.GetItemRectMin();
        var drawList = ImGui.GetWindowDrawList();

        // Auto-center on Finals the first time each tournament is viewed.
        if (_lastBracketId != t.Id)
        {
            _lastBracketId = t.Id;
            if (!fitMode)
            {
                float finalsX = Padding + numSubRounds * colWidth + MatchW * 0.5f;
                ImGui.SetScrollX(Math.Max(0f, finalsX - ImGui.GetWindowWidth() * 0.5f));
            }
        }

        // Viewport bounds in canvas-space for LOD culling.
        float scrollY = ImGui.GetScrollY();
        float scrollX = ImGui.GetScrollX();
        float viewH   = ImGui.GetWindowHeight();
        float viewW   = ImGui.GetWindowWidth();

        // Dark bracket background
        drawList.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
        drawList.AddRect(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(Theme.CardBorder with { W = 0.3f }), 8f, ImDrawFlags.None, 1f);

        // Round labels
        for (int r = 0; r < numSubRounds; r++)
        {
            string label = RoundLabel(r, numRounds);
            PlaceRoundLabel(drawList, origin, Padding + r * colWidth, label, Theme.Muted);
            PlaceRoundLabel(drawList, origin, Padding + (2 * numSubRounds - r) * colWidth, label, Theme.Muted);
        }
        PlaceRoundLabel(drawList, origin, Padding + numSubRounds * colWidth, "Finals", Theme.Gold);

        // Centre divider (decorative)
        float centreX = origin.X + Padding + numSubRounds * colWidth + MatchW * 0.5f;
        DrawThickLine(drawList,
            new(centreX, origin.Y + LabelH + 4),
            new(centreX, origin.Y + canvasH - Padding),
            Theme.ToU32(Theme.CardBorder with { W = 0.12f }), 1f);

        // Draw rounds — only matches/connectors inside the visible viewport are processed.
        // Connectors at round r can span up to 2^r * SlotH pixels vertically (adjacent
        // match centres grow exponentially), so the connector cull margin scales with r.
        // This means early rounds (where 99 % of matches live) are culled tightly while
        // late rounds (few matches, long connectors) effectively disable culling for themselves.
        for (int r = 0; r < numRounds; r++)
        {
            bool isFinal = r == numRounds - 1;
            var  round   = t.Rounds[r];
            int  count   = round.Count;

            float connMarginY = Math.Max(SlotH * 2f, (float)Math.Pow(2, r) * SlotH);

            if (isFinal)
            {
                float x  = Padding + numSubRounds * colWidth;
                float cy = MatchCenterY(r, 0);
                if (MatchBoxInView(cy, x, scrollY, scrollX, viewH, viewW))
                {
                    DrawMatchBox(drawList, t, round[0], origin + new Vector2(x, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (t.IsComplete && t.Champion != null)
                        DrawChampionOverlay(drawList, origin + new Vector2(x, cy - SlotH), new Vector2(MatchW, SlotH * 2));
                }
            }
            else
            {
                int   halfCount = count / 2;
                float xLeft     = Padding + r * colWidth;
                float xRight    = Padding + (2 * numSubRounds - r) * colWidth;

                for (int m = 0; m < halfCount; m++)
                {
                    float cy = MatchCenterY(r, m);
                    if (MatchBoxInView(cy, xLeft, scrollY, scrollX, viewH, viewW))
                        DrawMatchBox(drawList, t, round[m], origin + new Vector2(xLeft, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (ConnectorInView(cy, xLeft, scrollY, scrollX, viewH, viewW, connMarginY))
                        DrawDualConnector(drawList, origin, r, m, cy, xLeft, true,  colWidth, numSubRounds, numRounds, round[m], dynGap);
                }
                for (int m = halfCount; m < count; m++)
                {
                    int   mLocal = m - halfCount;
                    float cy     = MatchCenterY(r, mLocal);
                    if (MatchBoxInView(cy, xRight, scrollY, scrollX, viewH, viewW))
                        DrawMatchBox(drawList, t, round[m], origin + new Vector2(xRight, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (ConnectorInView(cy, xRight, scrollY, scrollX, viewH, viewW, connMarginY))
                        DrawDualConnector(drawList, origin, r, mLocal, cy, xRight, false, colWidth, numSubRounds, numRounds, round[m], dynGap);
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawBracketLR(Tournament t, bool readOnly = false)
    {
        int numRounds  = t.NumRounds;
        int totalSlots = t.TotalSlots;
        float colWidth = MatchW + RoundGap;

        if (numRounds <= 1)
        {
            float smallW = Padding * 2 + MatchW;
            float smallH = Padding * 2 + LabelH + SlotH * 2 + 4f;
            if (!ImGui.BeginChild("##BracketCanvas", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.None))
                return;
            ImGui.Dummy(new Vector2(smallW, smallH));
            var o  = ImGui.GetItemRectMin();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(o, o + new Vector2(smallW, smallH),
                Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
            PlaceRoundLabel(dl, o, Padding, "Finals", Theme.Gold);
            DrawMatchBox(dl, t, t.Rounds[0][0], o + new Vector2(Padding, LabelH), new Vector2(MatchW, SlotH * 2), readOnly);
            ImGui.EndChild();
            return;
        }

        float canvasW = Padding * 2 + numRounds * colWidth;
        float canvasH = Padding * 2 + LabelH + totalSlots * SlotH;

        if (!ImGui.BeginChild("##BracketCanvas", ImGui.GetContentRegionAvail(), false,
                ImGuiWindowFlags.HorizontalScrollbar))
            return;

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        var origin   = ImGui.GetItemRectMin();
        var drawList = ImGui.GetWindowDrawList();

        float scrollY = ImGui.GetScrollY();
        float scrollX = ImGui.GetScrollX();
        float viewH   = ImGui.GetWindowHeight();
        float viewW   = ImGui.GetWindowWidth();

        drawList.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
        drawList.AddRect(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(Theme.CardBorder with { W = 0.3f }), 8f, ImDrawFlags.None, 1f);

        // Round labels
        for (int r = 0; r < numRounds; r++)
        {
            bool isFinal = r == numRounds - 1;
            string label = isFinal ? "Finals" : RoundLabel(r, numRounds);
            PlaceRoundLabel(drawList, origin, Padding + r * colWidth, label,
                isFinal ? Theme.Gold : Theme.Muted);
        }

        for (int r = 0; r < numRounds; r++)
        {
            bool isFinal = r == numRounds - 1;
            var  round   = t.Rounds[r];
            float x      = Padding + r * colWidth;
            float connMarginY = Math.Max(SlotH * 2f, (float)Math.Pow(2, r) * SlotH);

            for (int m = 0; m < round.Count; m++)
            {
                float cy = MatchCenterY(r, m);
                if (MatchBoxInView(cy, x, scrollY, scrollX, viewH, viewW))
                {
                    DrawMatchBox(drawList, t, round[m], origin + new Vector2(x, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (isFinal && t.IsComplete && t.Champion != null)
                        DrawChampionOverlay(drawList, origin + new Vector2(x, cy - SlotH), new Vector2(MatchW, SlotH * 2));
                }
                if (!isFinal && ConnectorInView(cy, x, scrollY, scrollX, viewH, viewW, connMarginY))
                    DrawLRConnector(drawList, origin, r, m, cy, x, colWidth, round[m]);
            }
        }

        ImGui.EndChild();
    }

    // ── Double-Elimination ────────────────────────────────────────────────

    private void DrawBracketDE(Tournament t, bool readOnly = false)
    {
        // ── Winners Bracket ───────────────────────────────────────────────
        ImGui.TextColored(Theme.Gold, " Winners Bracket");
        ImGui.Separator();
        if (t.Layout == BracketLayout.VBracket)
            DrawDESubBracketV("##DEWBv", t, t.WBRounds, readOnly);
        else
            DrawDESubBracket("##DEWB", t, t.WBRounds, isWB: true, readOnly);

        // ── Losers Bracket ────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.55f, 1f, 1f), " Losers Bracket");
        ImGui.Separator();
        DrawDELBCanvas("##DELB", t, readOnly);

        // ── Grand Finals ──────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, " Grand Finals");
        ImGui.Separator();
        ImGui.Spacing();
        DrawDEGrandFinals(t, readOnly);
    }

    private void DrawDESubBracket(string childId, Tournament t,
        List<List<TournamentMatch>> rounds, bool isWB, bool readOnly)
    {
        if (rounds.Count == 0) return;

        int   numR       = rounds.Count;
        int   totalSlots = rounds[0].Count * 2;
        float colWidth   = MatchW + RoundGap;
        float canvasW    = Padding * 2f + numR * colWidth;
        float canvasH    = Padding * 2f + LabelH + totalSlots * SlotH;
        float childH     = Math.Min(canvasH, 280f);

        if (!ImGui.BeginChild(childId,
                new Vector2(ImGui.GetContentRegionAvail().X, childH), false,
                ImGuiWindowFlags.HorizontalScrollbar))
            return;

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        var origin = ImGui.GetItemRectMin();
        var dl     = ImGui.GetWindowDrawList();

        float scrollY = ImGui.GetScrollY(), scrollX = ImGui.GetScrollX();
        float viewH   = ImGui.GetWindowHeight(), viewW = ImGui.GetWindowWidth();

        dl.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
        dl.AddRect(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(Theme.CardBorder with { W = 0.3f }), 8f, ImDrawFlags.None, 1f);

        for (int r = 0; r < numR; r++)
        {
            bool isLast = r == numR - 1;
            string label = isWB
                ? (isLast ? "WB Finals" : RoundLabel(r, numR + 1)) // +1 so labels match SE
                : (isLast ? "LB Finals" : $"LB R{r + 1}");
            PlaceRoundLabel(dl, origin, Padding + r * colWidth, label,
                isLast ? Theme.Gold : Theme.Muted);
        }

        for (int r = 0; r < numR; r++)
        {
            bool  isLast       = r == numR - 1;
            float x            = Padding + r * colWidth;
            float connMarginY  = Math.Max(SlotH * 2f, (float)Math.Pow(2, r) * SlotH);

            for (int m = 0; m < rounds[r].Count; m++)
            {
                float cy = MatchCenterY(r, m);
                if (MatchBoxInView(cy, x, scrollY, scrollX, viewH, viewW))
                    DrawMatchBox(dl, t, rounds[r][m], origin + new Vector2(x, cy - SlotH),
                        new Vector2(MatchW, SlotH * 2), readOnly);

                if (!isLast && ConnectorInView(cy, x, scrollY, scrollX, viewH, viewW, connMarginY))
                    DrawLRConnector(dl, origin, r, m, cy, x, colWidth, rounds[r][m]);
            }
        }

        ImGui.EndChild();
    }

    private void DrawDESubBracketV(string childId, Tournament t,
        List<List<TournamentMatch>> rounds, bool readOnly)
    {
        if (rounds.Count == 0) return;

        int numRounds  = rounds.Count;
        int totalSlots = rounds[0].Count * 2;
        float colWidth = MatchW + RoundGap;

        if (numRounds <= 1)
        {
            float smallW = Padding * 2 + MatchW;
            float smallH = Padding * 2 + LabelH + SlotH * 2 + 4f;
            float h = Math.Min(smallH, 280f);
            if (!ImGui.BeginChild(childId, new Vector2(ImGui.GetContentRegionAvail().X, h), false, ImGuiWindowFlags.None))
                return;
            ImGui.Dummy(new Vector2(smallW, smallH));
            var o  = ImGui.GetItemRectMin();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(o, o + new Vector2(smallW, smallH),
                Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
            PlaceRoundLabel(dl, o, Padding, "WB Finals", Theme.Gold);
            DrawMatchBox(dl, t, rounds[0][0], o + new Vector2(Padding, LabelH), new Vector2(MatchW, SlotH * 2), readOnly);
            ImGui.EndChild();
            return;
        }

        int   numSubRounds = numRounds - 1;
        float numCols      = 2f * numSubRounds + 1f;

        float availW  = ImGui.GetContentRegionAvail().X;
        float fitColW = (availW - Padding * 2f) / numCols;
        bool  fitMode = fitColW >= MatchW + 16f;
        colWidth      = fitMode ? fitColW : MatchW + RoundGap;
        float dynGap  = fitMode ? (fitColW - MatchW) : RoundGap;

        float canvasW = Padding * 2f + numCols * colWidth;
        float canvasH = Padding * 2f + LabelH + (totalSlots / 2 + 1) * SlotH;
        float childH  = Math.Min(canvasH, 280f);

        var flags = fitMode ? ImGuiWindowFlags.None : ImGuiWindowFlags.HorizontalScrollbar;
        if (!ImGui.BeginChild(childId, new Vector2(ImGui.GetContentRegionAvail().X, childH), false, flags))
            return;

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        var origin   = ImGui.GetItemRectMin();
        var drawList = ImGui.GetWindowDrawList();

        if (_lastBracketId != t.Id)
        {
            _lastBracketId = t.Id;
            if (!fitMode)
            {
                float finalsX = Padding + numSubRounds * colWidth + MatchW * 0.5f;
                ImGui.SetScrollX(Math.Max(0f, finalsX - ImGui.GetWindowWidth() * 0.5f));
            }
        }

        float scrollY = ImGui.GetScrollY(), scrollX = ImGui.GetScrollX();
        float viewH   = ImGui.GetWindowHeight(), viewW = ImGui.GetWindowWidth();

        drawList.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
        drawList.AddRect(origin, origin + new Vector2(canvasW, canvasH),
            Theme.ToU32(Theme.CardBorder with { W = 0.3f }), 8f, ImDrawFlags.None, 1f);

        for (int r = 0; r < numSubRounds; r++)
        {
            string label = RoundLabel(r, numRounds);
            PlaceRoundLabel(drawList, origin, Padding + r * colWidth, label, Theme.Muted);
            PlaceRoundLabel(drawList, origin, Padding + (2 * numSubRounds - r) * colWidth, label, Theme.Muted);
        }
        PlaceRoundLabel(drawList, origin, Padding + numSubRounds * colWidth, "WB Finals", Theme.Gold);

        float centreX = origin.X + Padding + numSubRounds * colWidth + MatchW * 0.5f;
        DrawThickLine(drawList,
            new(centreX, origin.Y + LabelH + 4),
            new(centreX, origin.Y + canvasH - Padding),
            Theme.ToU32(Theme.CardBorder with { W = 0.12f }), 1f);

        for (int r = 0; r < numRounds; r++)
        {
            bool isFinal = r == numRounds - 1;
            var  round   = rounds[r];
            int  count   = round.Count;
            float connMarginY = Math.Max(SlotH * 2f, (float)Math.Pow(2, r) * SlotH);

            if (isFinal)
            {
                float x  = Padding + numSubRounds * colWidth;
                float cy = MatchCenterY(r, 0);
                if (MatchBoxInView(cy, x, scrollY, scrollX, viewH, viewW))
                    DrawMatchBox(drawList, t, round[0], origin + new Vector2(x, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
            }
            else
            {
                int   halfCount = count / 2;
                float xLeft     = Padding + r * colWidth;
                float xRight    = Padding + (2 * numSubRounds - r) * colWidth;

                for (int m = 0; m < halfCount; m++)
                {
                    float cy = MatchCenterY(r, m);
                    if (MatchBoxInView(cy, xLeft, scrollY, scrollX, viewH, viewW))
                        DrawMatchBox(drawList, t, round[m], origin + new Vector2(xLeft, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (ConnectorInView(cy, xLeft, scrollY, scrollX, viewH, viewW, connMarginY))
                        DrawDualConnector(drawList, origin, r, m, cy, xLeft, true, colWidth, numSubRounds, numRounds, round[m], dynGap);
                }
                for (int m = halfCount; m < count; m++)
                {
                    int   mLocal = m - halfCount;
                    float cy     = MatchCenterY(r, mLocal);
                    if (MatchBoxInView(cy, xRight, scrollY, scrollX, viewH, viewW))
                        DrawMatchBox(drawList, t, round[m], origin + new Vector2(xRight, cy - SlotH), new Vector2(MatchW, SlotH * 2), readOnly);
                    if (ConnectorInView(cy, xRight, scrollY, scrollX, viewH, viewW, connMarginY))
                        DrawDualConnector(drawList, origin, r, mLocal, cy, xRight, false, colWidth, numSubRounds, numRounds, round[m], dynGap);
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawDELBCanvas(string childId, Tournament t, bool readOnly)
    {
        var lb = t.LBRounds;
        if (lb.Count == 0) return;

        int   numLBR   = lb.Count;
        float colWidth = MatchW + RoundGap;

        // Compute center-Y for each match in each LB round.
        // LBR0 is the anchor: Y[m] = Padding + LabelH + m * 2 * SlotH
        // Cull rounds (next round has fewer matches): mid-point of the pair that feeds them
        // Carry rounds (same match count): same Y as previous round
        var roundY = new float[numLBR][];
        roundY[0] = Enumerable.Range(0, lb[0].Count)
            .Select(m => Padding + LabelH + m * 2f * SlotH).ToArray();
        for (int r = 1; r < numLBR; r++)
        {
            bool isCull = lb[r].Count < lb[r - 1].Count;
            roundY[r] = isCull
                ? Enumerable.Range(0, lb[r].Count)
                    .Select(m => (roundY[r - 1][2 * m] + roundY[r - 1][2 * m + 1]) * 0.5f)
                    .ToArray()
                : (float[])roundY[r - 1].Clone();
        }

        float lbCanvasH = Padding * 2f + LabelH + lb[0].Count * 2f * SlotH;
        float lbCanvasW = Padding * 2f + numLBR * colWidth;
        float childH    = Math.Min(lbCanvasH, 220f);

        if (!ImGui.BeginChild(childId,
                new Vector2(ImGui.GetContentRegionAvail().X, childH), false,
                ImGuiWindowFlags.HorizontalScrollbar))
            return;

        ImGui.Dummy(new Vector2(lbCanvasW, lbCanvasH));
        var origin = ImGui.GetItemRectMin();
        var dl     = ImGui.GetWindowDrawList();

        float scrollY = ImGui.GetScrollY(), scrollX = ImGui.GetScrollX();
        float viewH   = ImGui.GetWindowHeight(), viewW = ImGui.GetWindowWidth();

        dl.AddRectFilled(origin, origin + new Vector2(lbCanvasW, lbCanvasH),
            Theme.ToU32(new Vector4(0.03f, 0.03f, 0.07f, 0.6f)), 8f);
        dl.AddRect(origin, origin + new Vector2(lbCanvasW, lbCanvasH),
            Theme.ToU32(Theme.CardBorder with { W = 0.3f }), 8f, ImDrawFlags.None, 1f);

        // Round labels — alternate tints: feed rounds slightly purple, cull rounds muted
        for (int r = 0; r < numLBR; r++)
        {
            bool isLast = r == numLBR - 1;
            string label = isLast ? "LB Finals" : $"LB R{r + 1}";
            PlaceRoundLabel(dl, origin, Padding + r * colWidth, label,
                isLast ? Theme.Gold : (r % 2 == 0 ? Theme.Muted : new Vector4(0.6f, 0.55f, 0.9f, 1f)));
        }

        for (int r = 0; r < numLBR; r++)
        {
            float x = Padding + r * colWidth;
            for (int m = 0; m < lb[r].Count; m++)
            {
                float cy    = roundY[r][m];
                var   match = lb[r][m];

                if (MatchBoxInView(cy, x, scrollY, scrollX, viewH, viewW))
                    DrawMatchBox(dl, t, match, origin + new Vector2(x, cy - SlotH),
                        new Vector2(MatchW, SlotH * 2), readOnly);

                if (r + 1 >= numLBR) continue;

                // Connector to next LB round
                bool   isCullNext = lb[r + 1].Count < lb[r].Count;
                float  targetCY;
                float  targetSlotY;
                if (isCullNext)
                {
                    targetCY    = roundY[r + 1][m / 2];
                    targetSlotY = origin.Y + (m % 2 == 0 ? targetCY - SlotH * 0.5f : targetCY + SlotH * 0.5f);
                }
                else
                {
                    targetCY    = roundY[r + 1][m];
                    targetSlotY = origin.Y + targetCY - SlotH * 0.5f; // always P1 (LB winner)
                }

                DrawDELBConnector(dl, origin, cy, targetSlotY, x, x + colWidth, match);
            }
        }

        ImGui.EndChild();
    }

    private static void DrawDELBConnector(
        ImDrawListPtr dl, Vector2 origin,
        float cy, float targetSlotY, float x, float nextX,
        TournamentMatch match)
    {
        float startX = origin.X + x + MatchW;
        float endX   = origin.X + nextX;
        float midX   = startX + RoundGap * 0.5f;
        float startY = origin.Y + cy;

        uint  lineColor;
        float thickness;
        if (match.IsCompleted)
        {
            lineColor = Theme.ToU32(Theme.WinGreen with { W = 0.55f });
            thickness = 2f;
        }
        else if (match.Status == MatchStatus.InProgress)
        {
            float pulse = (float)(Math.Sin(ImGui.GetTime() * 3.0) * 0.2 + 0.6);
            lineColor = Theme.ToU32(Theme.Warning with { W = pulse });
            thickness = 1.75f;
        }
        else
        {
            lineColor = Theme.ToU32(Theme.CardBorder with { W = 0.4f });
            thickness = 1.25f;
        }

        DrawThickLine(dl, new(startX, startY),    new(midX, startY),    lineColor, thickness);
        DrawThickLine(dl, new(midX,   startY),    new(midX, targetSlotY), lineColor, thickness);
        DrawThickLine(dl, new(midX,   targetSlotY), new(endX, targetSlotY), lineColor, thickness);
    }

    private void DrawDEGrandFinals(Tournament t, bool readOnly)
    {
        var gf = t.GrandFinalsMatch;
        if (gf == null) return;

        var  dl      = ImGui.GetWindowDrawList();
        float boxW   = MatchW;
        float boxH   = SlotH * 2;

        // GF game 1
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Padding);
        ImGui.Dummy(new Vector2(boxW, boxH));
        var gfPos = ImGui.GetItemRectMin();
        DrawMatchBox(dl, t, gf, gfPos, new Vector2(boxW, boxH), readOnly);
        if (t.IsComplete && gf.IsCompleted && t.GrandFinalsReset == null)
            DrawChampionOverlay(dl, gfPos, new Vector2(boxW, boxH));

        // Bracket reset prompt
        if (t.GrandFinalsNeedsReset && !readOnly)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Padding);
            ImGui.TextColored(Theme.Warning, $"{gf.Winner} won game 1 — bracket reset?");
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Padding);

            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Warning  with { W = 0.25f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Warning  with { W = 0.40f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Warning  with { W = 0.55f }));
            if (ImGui.Button("Play Reset Match"))
                plugin.TournamentService.TriggerBracketReset();
            ImGui.PopStyleColor(3);

            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.20f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.35f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.50f }));
            if (ImGui.Button($"Declare {gf.Winner} Champion"))
                plugin.TournamentService.DeclareChampionWithoutReset();
            ImGui.PopStyleColor(3);
        }

        // GF reset game
        if (t.GrandFinalsReset != null)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Padding);
            ImGui.TextColored(new Vector4(0.7f, 0.65f, 1f, 1f), "— Reset Game —");
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Padding);
            ImGui.Dummy(new Vector2(boxW, boxH));
            var resetPos = ImGui.GetItemRectMin();
            DrawMatchBox(dl, t, t.GrandFinalsReset, resetPos, new Vector2(boxW, boxH), readOnly);
            if (t.IsComplete && t.GrandFinalsReset.IsCompleted)
                DrawChampionOverlay(dl, resetPos, new Vector2(boxW, boxH));
        }

        ImGui.Spacing();
    }

    // ── Connector ─────────────────────────────────────────────────────────

    private static void DrawLRConnector(
        ImDrawListPtr dl, Vector2 origin,
        int r, int m, float cy, float x, float colWidth,
        TournamentMatch match)
    {
        int   nextM   = m / 2;
        float nextCY  = MatchCenterY(r + 1, nextM);
        float startX  = origin.X + x + MatchW;
        float endX    = origin.X + x + colWidth;
        float midX    = startX + RoundGap * 0.5f;
        float startY  = origin.Y + cy;
        float targetY = origin.Y + (m % 2 == 0 ? nextCY - SlotH * 0.5f : nextCY + SlotH * 0.5f);

        uint lineColor;
        float thickness;
        if (match.IsCompleted)
        {
            lineColor = Theme.ToU32(Theme.WinGreen with { W = 0.55f });
            thickness = 2f;
        }
        else if (match.Status == MatchStatus.InProgress)
        {
            float pulse = (float)(Math.Sin(ImGui.GetTime() * 3.0) * 0.2 + 0.6);
            lineColor = Theme.ToU32(Theme.Warning with { W = pulse });
            thickness = 1.75f;
        }
        else
        {
            lineColor = Theme.ToU32(Theme.CardBorder with { W = 0.4f });
            thickness = 1.25f;
        }

        DrawThickLine(dl, new(startX, startY),  new(midX,   startY),  lineColor, thickness);
        DrawThickLine(dl, new(midX,   startY),  new(midX,   targetY), lineColor, thickness);
        DrawThickLine(dl, new(midX,   targetY), new(endX,   targetY), lineColor, thickness);
    }

    private static void DrawDualConnector(
        ImDrawListPtr dl, Vector2 origin,
        int r, int m, float cy, float x,
        bool goRight, float colWidth, int numSubRounds, int numRounds,
        TournamentMatch match, float roundGap = RoundGap)
    {
        bool isLastSub = r == numSubRounds - 1;
        int  nextM     = m / 2;

        float nextCY = isLastSub
            ? MatchCenterY(numRounds - 1, 0)
            : MatchCenterY(r + 1, nextM);

        // At the last sub-round the Finals has two slots; left SF feeds the top, right SF the bottom.
        // For all other rounds, even local-index matches feed the top slot of the parent, odd feed the bottom.
        bool  upper   = isLastSub ? goRight : (m % 2 == 0);
        float targetY = origin.Y + (upper ? nextCY - SlotH * 0.5f : nextCY + SlotH * 0.5f);
        float startY  = origin.Y + cy;

        float startX, midX, endX;
        if (goRight)
        {
            startX = origin.X + x + MatchW;
            endX   = origin.X + (isLastSub
                ? Padding + numSubRounds * colWidth
                : Padding + (r + 1) * colWidth);
            midX   = startX + roundGap * 0.5f;
        }
        else
        {
            startX = origin.X + x;
            endX   = origin.X + (isLastSub
                ? Padding + numSubRounds * colWidth + MatchW
                : Padding + (2 * numSubRounds - (r + 1)) * colWidth + MatchW);
            midX   = startX - roundGap * 0.5f;
        }

        uint lineColor;
        float thickness;
        if (match.IsCompleted)
        {
            lineColor = Theme.ToU32(Theme.WinGreen with { W = 0.55f });
            thickness = 2f;
        }
        else if (match.Status == MatchStatus.InProgress)
        {
            float pulse = (float)(Math.Sin(ImGui.GetTime() * 3.0) * 0.2 + 0.6);
            lineColor = Theme.ToU32(Theme.Warning with { W = pulse });
            thickness = 1.75f;
        }
        else
        {
            lineColor = Theme.ToU32(Theme.CardBorder with { W = 0.4f });
            thickness = 1.25f;
        }

        DrawThickLine(dl, new(startX, startY),  new(midX,   startY),  lineColor, thickness);
        DrawThickLine(dl, new(midX,   startY),  new(midX,   targetY), lineColor, thickness);
        DrawThickLine(dl, new(midX,   targetY), new(endX,   targetY), lineColor, thickness);
    }

    // ── Champion overlay on Final box ─────────────────────────────────────

    private static void DrawChampionOverlay(ImDrawListPtr dl, Vector2 topLeft, Vector2 size)
    {
        float p = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.5 + 0.5);
        for (int i = 1; i <= 3; i++)
        {
            dl.AddRect(topLeft - new Vector2(i * 2, i * 2),
                       topLeft + size + new Vector2(i * 2, i * 2),
                       Theme.ToU32(Theme.Gold with { W = (0.3f + p * 0.3f) / i }),
                       6f + i, ImDrawFlags.None, 1.5f);
        }
        string badge = "🏆 CHAMPION";
        var ts   = ImGui.CalcTextSize(badge);
        var tpos = topLeft - new Vector2(-((size.X - ts.X) * 0.5f), ts.Y + 4f);
        dl.AddText(tpos, Theme.ToU32(Theme.Gold with { W = 0.8f + p * 0.2f }), badge);
    }

    // ── Match box ─────────────────────────────────────────────────────────

    private void DrawMatchBox(ImDrawListPtr dl, Tournament t, TournamentMatch match,
        Vector2 topLeft, Vector2 size, bool readOnly = false)
    {
        var botRight = topLeft + size;
        var midLeft  = new Vector2(topLeft.X,  topLeft.Y + size.Y * 0.5f);
        var midRight = new Vector2(botRight.X, topLeft.Y + size.Y * 0.5f);
        bool isCurrent = match == t.CurrentMatch;

        // Background
        uint bg = match.IsBye
            ? Theme.ToU32(new Vector4(0.07f, 0.07f, 0.12f, 0.7f))
            : Theme.ToU32(Theme.CardBg);
        dl.AddRectFilled(topLeft, botRight, bg, 5f);

        // Subtle per-player row tints (only when match not yet decided)
        if (!match.IsBye && !match.IsCompleted)
        {
            dl.AddRectFilled(topLeft  + new Vector2(5, 1), midRight - new Vector2(0, 1),
                Theme.ToU32(Theme.Player1 with { W = 0.06f }));
            dl.AddRectFilled(midLeft  + new Vector2(5, 1), botRight - new Vector2(0, 1),
                Theme.ToU32(Theme.Player2 with { W = 0.06f }));
        }

        // Border
        float pulseAlpha = isCurrent ? 0.55f + (float)(Math.Sin(ImGui.GetTime() * 3.5) * 0.45) : 0f;
        uint border = isCurrent
            ? Theme.ToU32(Theme.Gold with { W = pulseAlpha + 0.4f })
            : Theme.ToU32(Theme.CardBorder);
        dl.AddRect(topLeft, botRight, border, 5f, ImDrawFlags.None, isCurrent ? 2.5f : 1f);

        if (isCurrent)
            dl.AddRect(topLeft - new Vector2(2, 2), botRight + new Vector2(2, 2),
                Theme.ToU32(Theme.Gold with { W = pulseAlpha * 0.4f }), 7f, ImDrawFlags.None, 1f);

        // Search highlight
        if (_searchFilter.Length > 0 && !match.IsBye && MatchesSearch(match))
        {
            float sp = 0.5f + (float)(Math.Sin(ImGui.GetTime() * 5.0) * 0.5);
            dl.AddRect(topLeft - new Vector2(3, 3), botRight + new Vector2(3, 3),
                Theme.ToU32(Theme.Warning with { W = 0.35f + sp * 0.5f }), 8f, ImDrawFlags.None, 2.5f);
        }

        // Row divider
        DrawThickLine(dl,
            new Vector2(topLeft.X + 5,  midLeft.Y),
            new Vector2(botRight.X - 5, midLeft.Y),
            Theme.ToU32(Theme.CardBorder with { W = 0.45f }), 1f);

        // Status strip (left edge)
        uint statusColor = match.Status switch
        {
            MatchStatus.Completed  => Theme.ToU32(Theme.WinGreen with { W = 0.8f }),
            MatchStatus.InProgress => Theme.ToU32(Theme.Warning  with { W = 0.8f }),
            MatchStatus.Bye        => Theme.ToU32(Theme.Muted    with { W = 0.3f }),
            _                      => Theme.ToU32(Theme.Muted    with { W = 0.2f }),
        };
        dl.AddRectFilled(topLeft, new Vector2(topLeft.X + 4, botRight.Y), statusColor, 5f);

        // Round·match badge (top-right, dim)
        if (!match.IsBye)
        {
            string badge = $"R{match.RoundIndex + 1}·{match.MatchIndex + 1}";
            var bts = ImGui.CalcTextSize(badge);
            dl.AddText(topLeft + new Vector2(size.X - bts.X - 5f, 4f),
                Theme.ToU32(Theme.Muted with { W = 0.35f }), badge);
        }

        // Player rows
        DrawPlayerInBox(dl, topLeft,  new Vector2(size.X, SlotH), match.Player1 ?? "TBD",
            match.IsCompleted && match.Winner == match.Player1,
            match.IsCompleted && match.Winner != match.Player1,
            false);

        string p2Label = match.IsBye ? "BYE" : match.Player2 ?? "TBD";
        DrawPlayerInBox(dl, midLeft, new Vector2(size.X, SlotH), p2Label,
            match.IsCompleted && match.Winner == match.Player2,
            match.IsCompleted && match.Winner != match.Player2,
            match.IsBye);

        // Interaction (skipped in spectator/read-only mode — no game records, no repairs)
        if (!readOnly)
        {
            // Side is part of the ID — double-elim WB/LB matches share round/match indices
            string ctxId = $"##ctx_{match.Side}_{match.RoundIndex}_{match.MatchIndex}";
            ImGui.SetCursorScreenPos(topLeft);
            ImGui.InvisibleButton($"##m_{match.Side}_{match.RoundIndex}_{match.MatchIndex}", size);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                if (match.IsCompleted && match.GameId.HasValue)
                    ImGui.TextColored(Theme.Muted, "Click: roll history  ·  Right-click: repair");
                else if (match.IsCompleted)
                    ImGui.TextColored(Theme.Muted, "Right-click to repair (clear result / rename)");
                else if (!match.IsBye)
                    ImGui.TextColored(Theme.Muted, "Right-click: force winner / rename");
                ImGui.EndTooltip();
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && match.IsCompleted && match.GameId.HasValue)
            {
                pendingDetailMatch = match;
                ImGui.OpenPopup("##matchDetail");
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !match.IsBye &&
                (match.BothPlayersReady || match.IsCompleted))
                ImGui.OpenPopup(ctxId);

            if (ImGui.BeginPopup(ctxId))
            {
                if (!match.IsCompleted && match.BothPlayersReady)
                {
                    ImGui.TextColored(Theme.Warning, "Force winner:");
                    if (match.Player1 != null && ImGui.Selectable($"★ {match.Player1}##fw1"))
                        TournamentSvc.ForceWinner(match, match.Player1);
                    if (match.Player2 != null && ImGui.Selectable($"★ {match.Player2}##fw2"))
                        TournamentSvc.ForceWinner(match, match.Player2);
                    ImGui.Separator();
                }

                if (match.IsCompleted)
                {
                    ImGui.TextColored(Theme.Warning, $"Result: {match.Winner} won");
                    if (ImGui.Selectable("↶ Clear this result"))
                    {
                        int dropped = TournamentSvc.ClearMatchResult(match);
                        _repairMsg      = dropped > 0
                            ? $"Result cleared — {dropped} downstream result(s) also reverted to pending."
                            : "Result cleared.";
                        _repairMsgUntil = ImGui.GetTime() + 8.0;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Reverts this match to pending.\nDownstream results that depended on the winner also revert.\nSpectators/web are resynced automatically.");
                    ImGui.Separator();
                }

                ImGui.TextColored(Theme.Muted, "Rename player:");
                foreach (var p in new[] { match.Player1, match.Player2 })
                {
                    if (p == null || p == "BYE") continue;
                    if (ImGui.Selectable($"✎ {p}##rn{p}"))
                    {
                        _renameOld       = p;
                        _renameInput     = p;
                        _openRenamePopup = true;
                    }
                }
                ImGui.EndPopup();
            }
        }
    }

    // ── Rename popup ──────────────────────────────────────────────────────

    private void DrawRenamePopup()
    {
        if (_openRenamePopup)
        {
            ImGui.OpenPopup("##renamePlayer");
            _openRenamePopup = false;
        }
        if (!ImGui.BeginPopup("##renamePlayer")) return;

        ImGui.TextColored(Theme.Gold, $"Rename: {_renameOld}");
        ImGui.TextColored(Theme.Muted, "Updates the whole bracket and any live game.\nSpectators/web are resynced automatically.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(220);
        bool enter = ImGui.InputText("##renameIn", ref _renameInput, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        var  newName = _renameInput.Trim();

        // A name already seated elsewhere would make winner resolution ambiguous.
        // Comparing against _renameOld case-insensitively still allows pure
        // capitalization fixes (e.g. "alice smith" → "Alice Smith").
        bool taken = !string.Equals(newName, _renameOld, StringComparison.OrdinalIgnoreCase) &&
                     TournamentSvc.ActiveTournament?.EnumerateMatches().Any(m =>
                         string.Equals(m.Player1, newName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(m.Player2, newName, StringComparison.OrdinalIgnoreCase)) == true;
        if (taken)
            ImGui.TextColored(Theme.Danger, $"\"{newName}\" is already in the bracket.");

        bool valid = _renameOld != null && newName.Length > 0 && !taken &&
                     !string.Equals(newName, "BYE", StringComparison.OrdinalIgnoreCase) &&
                     newName != _renameOld;

        if (!valid) ImGui.BeginDisabled();
        bool apply = ImGui.Button("Apply", new Vector2(80, 0)) || (enter && valid);
        if (!valid) ImGui.EndDisabled();
        if (apply && valid)
        {
            TournamentSvc.RenamePlayer(_renameOld!, newName);
            _repairMsg      = $"Renamed {_renameOld} → {newName}.";
            _repairMsgUntil = ImGui.GetTime() + 8.0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static void DrawPlayerInBox(ImDrawListPtr dl, Vector2 rowOrigin, Vector2 rowSize,
        string name, bool isWinner, bool isLoser, bool isBye)
    {
        if (isWinner)
            dl.AddRectFilled(rowOrigin + new Vector2(4, 0), rowOrigin + rowSize,
                Theme.ToU32(Theme.WinGreen with { W = 0.14f }));

        uint textColor = isBye    ? Theme.ToU32(Theme.Muted   with { W = 0.35f })
                       : isWinner ? Theme.ToU32(Theme.WinGreen)
                       : isLoser  ? Theme.ToU32(Theme.Muted   with { W = 0.45f })
                                  : Theme.ToU32(Theme.White);

        var display = name.Length > 22 ? name[..20] + ".." : name;
        dl.AddText(rowOrigin + new Vector2(10f, (rowSize.Y - 13f) * 0.5f), textColor, display);

        if (isWinner)
            dl.AddText(rowOrigin + new Vector2(rowSize.X - 20f, (rowSize.Y - 13f) * 0.5f),
                Theme.ToU32(Theme.Gold), "★");
    }

    // ── Roll feed ─────────────────────────────────────────────────────────

    private void DrawRollFeed(Tournament t)
    {
        var feed = TournamentSvc.GetRollFeed();

        ImGui.TextColored(Theme.Gold, $"Roll Feed  ({feed.Count} rolls)");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, "  Click a completed match box to view its full history.");
        ImGui.Separator();

        float feedH = ImGui.GetContentRegionAvail().Y - 4f;
        if (!ImGui.BeginChild("##RollFeed", new Vector2(0, feedH), false)) return;

        if (feed.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "No rolls yet — start the first match!");
        }
        else
        {
            for (int i = feed.Count - 1; i >= 0; i--)
            {
                var entry   = feed[i];
                var roll    = entry.Roll;
                bool isP1   = string.Equals(roll.PlayerName, entry.Match.Player1, StringComparison.OrdinalIgnoreCase);
                var nameCol = isP1 ? Theme.Player1 : Theme.Player2;
                var rollCol = roll.IsGameOver
                    ? Theme.Danger
                    : Theme.DangerGradient(1f - (float)roll.RolledValue / entry.Game.StartingNumber);

                ImGui.TextColored(rollCol, "▮");
                ImGui.SameLine(0, 3f);
                ImGui.TextColored(entry.Live ? Theme.Warning : Theme.Muted, $"[{entry.MatchLabel}]");
                ImGui.SameLine();
                ImGui.TextColored(nameCol, roll.PlayerName);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Muted, "→");
                ImGui.SameLine();
                ImGui.TextColored(rollCol, roll.RolledValue.ToString("N0"));
                ImGui.SameLine();
                ImGui.TextColored(Theme.Muted, $"/{roll.MaxValue:N0}");

                if (roll.IsGameOver)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Danger, "  💀");
                }
                else if (entry.Live)
                {
                    ImGui.SameLine();
                    float pulse = (float)(Math.Sin(ImGui.GetTime() * 3) * 0.5 + 0.5);
                    ImGui.TextColored(Theme.Warning with { W = 0.5f + pulse * 0.5f }, " ◀ live");
                }
            }
        }

        ImGui.EndChild();
    }

    // ── Match detail popup ────────────────────────────────────────────────

    private void DrawMatchDetailPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 360), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##matchDetail")) return;

        var match = pendingDetailMatch;
        if (match == null) { ImGui.EndPopup(); return; }

        var game = TournamentSvc.GetMatchGame(match);
        if (game == null)
        {
            ImGui.TextColored(Theme.Muted, "Game record not found.");
            ImGui.EndPopup();
            return;
        }

        ImGui.TextColored(Theme.Gold, $"Round {match.RoundIndex + 1}  ·  Match {match.MatchIndex + 1}");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(Theme.Player1, game.Player1Name);
        ImGui.SameLine(); ImGui.TextColored(Theme.Muted, " vs ");
        ImGui.SameLine(); ImGui.TextColored(Theme.Player2, game.Player2Name);
        ImGui.Spacing();

        if (game.WinnerName != null)
        {
            ImGui.TextColored(Theme.WinGreen, $"🏆 {game.WinnerName} wins");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Danger, $"  💀 {game.LoserName} rolled 1");
        }
        if (game.BetAmount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Gold, $"  ·  {game.BetAmount:N0} gil");
        }

        ImGui.TextColored(Theme.Muted,
            $"Starting: {game.StartingNumber:N0}  ·  {game.Rolls.Count} rolls  ·  {game.Duration:mm\\:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Theme.Gold, "Roll History");
        ImGui.Spacing();

        float listH = ImGui.GetContentRegionAvail().Y - 30f;
        if (ImGui.BeginChild("##DetailRolls", new Vector2(0, listH), true))
        {
            foreach (var roll in game.Rolls)
            {
                bool isP1   = string.Equals(roll.PlayerName, game.Player1Name, StringComparison.OrdinalIgnoreCase);
                var nameCol = isP1 ? Theme.Player1 : Theme.Player2;
                var rollCol = roll.IsGameOver
                    ? Theme.Danger
                    : Theme.DangerGradient(1f - (float)roll.RolledValue / game.StartingNumber);

                ImGui.TextColored(rollCol, "▮");
                ImGui.SameLine(0, 3f);
                ImGui.TextColored(nameCol, roll.PlayerName);
                ImGui.SameLine(); ImGui.TextColored(Theme.Muted, "rolled");
                ImGui.SameLine(); ImGui.TextColored(rollCol, roll.RolledValue.ToString("N0"));
                ImGui.SameLine(); ImGui.TextColored(Theme.Muted, $"/ {roll.MaxValue:N0}");
                if (roll.IsGameOver) { ImGui.SameLine(); ImGui.TextColored(Theme.Danger, "  ← 💀 DEAD"); }
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Close", new Vector2(80, 0)))
        {
            pendingDetailMatch = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("📋 Copy", new Vector2(90, 0)))
            ImGui.SetClipboardText(TournamentSvc.ExportMatchText(match));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy this match's roll history to clipboard");

        ImGui.EndPopup();
    }

    // ── Full report popup ─────────────────────────────────────────────────

    private void DrawFullReportPopup()
    {
        if (_openReportPopup)
        {
            ImGui.OpenPopup("##fullReport");
            _openReportPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(560, 520), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##fullReport")) return;

        ImGui.TextColored(Theme.Gold, "📜 Full Match Report");
        ImGui.SameLine();
        if (ImGui.SmallButton("⟳ Refresh##report"))
            _reportText = TournamentSvc.ExportFullReport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rebuild the report with the latest rolls");
        ImGui.Separator();

        var textSize = ImGui.GetContentRegionAvail();
        textSize.Y -= 34f;
        if (ImGui.BeginChild("##reportText", textSize, true))
        {
            ImGui.TextUnformatted(_reportText);
            ImGui.EndChild();
        }

        if (ImGui.Button("📋 Copy All", new Vector2(110, 0)))
            ImGui.SetClipboardText(_reportText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy the entire report to clipboard\n(paste into Discord or a text file to keep a backup)");
        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(80, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ── Setup panel ───────────────────────────────────────────────────────

    private void DrawSetupPanel()
    {
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.Spacing();

        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.4 + 0.6);
        ImGui.SetCursorPosX((w - ImGui.CalcTextSize("⚔  NEW TOURNAMENT  ⚔").X) * 0.5f);
        ImGui.TextColored(Theme.Gold * new Vector4(1, 1, 1, pulse), "⚔  NEW TOURNAMENT  ⚔");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Tournament name", ref newTournamentName, 80);

        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Venue (optional)", ref newVenue, 80);

        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Starting number", ref newStarting);
        if (newStarting < 2) newStarting = 2;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputText("Bet (gil)", ref newBetStr, 20, ImGuiInputTextFlags.CharsDecimal);

        ImGui.SetNextItemWidth(210);
        ImGui.Combo("Bracket layout", ref _pendingLayout, "V-Bracket (Centre Finals)\0Left to Right\0");

        ImGui.SetNextItemWidth(210);
        ImGui.Combo("Format", ref _pendingFormat, "Single Elimination\0Double Elimination (max 16)\0");
        if (_pendingFormat == 1 && playerList.Count > 16)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, "  capped at 16");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Bracket size preview
        int slots = 1;
        while (slots < Math.Max(playerList.Count, 2)) slots <<= 1;
        int byes = slots - playerList.Count;

        ImGui.TextColored(Theme.Gold, $"Players  ({playerList.Count})");
        if (playerList.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"  →  {slots}-slot bracket");
            if (byes > 0) { ImGui.SameLine(); ImGui.TextColored(Theme.Muted, $"({byes} bye{(byes > 1 ? "s" : "")})"); }
        }

        ImGui.Spacing();

        float listH = Math.Clamp(playerList.Count * 28f + 8f, 60f, 220f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.ToU32(Theme.CardBg));
        if (ImGui.BeginChild("##PlayerList", new Vector2(w - 4, listH), true))
        {
            if (playerList.Count == 0)
            {
                ImGui.TextColored(Theme.Muted, "No players added yet.");
            }
            else
            {
                int removeIdx = -1;
                for (int i = 0; i < playerList.Count; i++)
                {
                    ImGui.TextColored(Theme.Muted, $"{i + 1,2}.");
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.White, playerList[i]);

                    ImGui.SameLine(w - 40f);
                    ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(new Vector4(0.5f, 0.1f, 0.1f, 0.6f)));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Danger));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.LosRed));
                    if (ImGui.SmallButton($"×##{i}")) removeIdx = i;
                    ImGui.PopStyleColor(3);
                }
                if (removeIdx >= 0) { playerList.RemoveAt(removeIdx); duplicateWarning = null; }
            }
            ImGui.EndChild();
        }
        ImGui.PopStyleColor();

        ImGui.Spacing();

        // Add player row
        ImGui.SetNextItemWidth(w - 110f);
        bool pressedEnter = ImGui.InputText("##newPlayer", ref newPlayerInput, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();

        bool canAdd = newPlayerInput.Trim().Length > 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("+ Add", new Vector2(55f, 0)) || (pressedEnter && canAdd))
            TryAddPlayer();
        if (!canAdd) ImGui.EndDisabled();

        if (duplicateWarning != null)
            ImGui.TextColored(Theme.Warning, $"  \"{duplicateWarning}\" is already in the list.");

        ImGui.Spacing();

        // Clear / Shuffle / Copy row
        if (playerList.Count > 0)
        {
            if (ImGui.SmallButton("Clear all"))
                ImGui.OpenPopup("##confirmClearAll");
            if (ImGui.BeginPopup("##confirmClearAll"))
            {
                ImGui.TextColored(Theme.Warning, $"Remove all {playerList.Count} players?");
                if (ImGui.Button("Yes, clear", new Vector2(100, 0)))
                {
                    playerList.Clear(); duplicateWarning = null; importResult = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Keep them", new Vector2(100, 0)))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Shuffle"))
            {
                for (int i = playerList.Count - 1; i > 0; i--)
                {
                    int j = Random.Shared.Next(i + 1);
                    (playerList[i], playerList[j]) = (playerList[j], playerList[i]);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Randomise seed order");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy List"))
                ImGui.SetClipboardText(string.Join('\n', playerList));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy all player names to clipboard, one per line");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, "  |  Seed order determines bracket placement.");
        }

        ImGui.Spacing();

        // ── Import from file ──────────────────────────────────────────────
        ImGui.TextColored(Theme.Muted, "Import from file:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(w - ImGui.GetCursorPosX() - 70f);
        ImGui.InputText("##importPath", ref importPath, 512);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Plain text file — one player name per line.\nLines starting with # are ignored.");
        ImGui.SameLine();
        if (ImGui.Button("Import", new Vector2(60f, 0)))
            ImportPlayersFromFile(importPath);

        if (importResult != null)
        {
            bool ok = importResult.StartsWith("Imported");
            ImGui.TextColored(ok ? Theme.WinGreen : Theme.Warning, $"  {importResult}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Create button
        bool canCreate = playerList.Count >= 2;
        float btnW = 280f;
        ImGui.SetCursorPosX((w - btnW) * 0.5f);

        if (!canCreate) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Gold with { W = 0.25f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Gold with { W = 0.40f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Gold with { W = 0.55f }));
        if (ImGui.Button($"⚔  Create Bracket  ({playerList.Count} players)", new Vector2(btnW, 36f)))
        {
            long bet    = long.TryParse(newBetStr, out var b) ? Math.Max(b, 0) : 0;
            var  layout = _pendingLayout == 1 ? BracketLayout.LeftToRight : BracketLayout.VBracket;
            var  format = _pendingFormat == 1 ? BracketFormat.DoubleElim  : BracketFormat.SingleElim;
            plugin.TournamentService.CreateTournament(newTournamentName, playerList, newStarting, bet, newVenue.Trim(), layout, format);
            playerList.Clear();
            newPlayerInput   = string.Empty;
            duplicateWarning = null;
        }
        ImGui.PopStyleColor(3);
        if (!canCreate) ImGui.EndDisabled();

        if (playerList.Count == 1)
        {
            ImGui.SetCursorPosX((w - ImGui.CalcTextSize("Need at least 2 players.").X) * 0.5f);
            ImGui.TextColored(Theme.Danger, "Need at least 2 players.");
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "  Tips:");
        ImGui.TextColored(Theme.Muted, "  • Non-power-of-2 counts are fine — byes fill the gaps.");
        ImGui.TextColored(Theme.Muted, "  • Right-click any match in the bracket to force a winner.");
        ImGui.TextColored(Theme.Muted, "  • /dr tournament  opens this window directly.");

        ImGui.Spacing();
        ImGui.Separator();

        // ── Join as Spectator ─────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "  📡 Join as Spectator");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, "— enter the host's 6-character relay code:");
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f);
        ImGui.SetNextItemWidth(84f);
        ImGui.InputText("##watchCode", ref _watchCodeInput, 6);
        // Normalise to uppercase as the user types
        if (_watchCodeInput.Length > 0)
            _watchCodeInput = _watchCodeInput.ToUpperInvariant();
        ImGui.SameLine();
        bool canJoin = _watchCodeInput.Trim().Length == 6;
        if (!canJoin) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.15f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.28f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.40f }));
        if (ImGui.Button("Join Relay"))
        {
            plugin.RelayService.JoinRelay(_watchCodeInput);
            _watchCodeInput = string.Empty;
        }
        ImGui.PopStyleColor(3);
        if (!canJoin) ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.Separator();

        // ── Quick test ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextColored(Theme.Warning, "  Quick Test");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, "— generate a bracket with random names instantly:");
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f);

        foreach (int count in (int[])[8, 16, 32])
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Warning with { W = 0.18f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Warning with { W = 0.32f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Warning with { W = 0.50f }));
            if (ImGui.Button($"⚡ {count} players", new Vector2(110f, 26f)))
                StartTestBracket(count);
            ImGui.PopStyleColor(3);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.Spacing();
    }

    private void StartTestBracket(int count)
    {
        var names = TestPlayerPool
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .ToList();

        var layout = _pendingLayout == 1 ? BracketLayout.LeftToRight : BracketLayout.VBracket;
        var format = _pendingFormat == 1 ? BracketFormat.DoubleElim  : BracketFormat.SingleElim;
        plugin.TournamentService.CreateTournament(
            $"Test Bracket ({count}p)", names,
            plugin.Configuration.DefaultStartingNumber, 0, layout: layout, format: format);

        _testMode = true;
    }

    private void SimulateMatch(TournamentMatch match)
    {
        if (match.Player1 == null || match.Player2 == null) return;
        var winner = Random.Shared.Next(2) == 0 ? match.Player1 : match.Player2;
        TournamentSvc.ForceWinner(match, winner);
    }

    private void SimulateAll()
    {
        var t = TournamentSvc.ActiveTournament;
        if (t == null) return;

        // Keep advancing as long as there's a ready match — cap iterations to avoid
        // an infinite loop if the bracket state is somehow inconsistent.
        int guard = 512;
        while (!t.IsComplete && guard-- > 0)
        {
            var match = t.CurrentMatch;
            if (match == null || !match.BothPlayersReady) break;
            SimulateMatch(match);
        }
    }

    private void ImportPlayersFromFile(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            importResult = "No path specified.";
            return;
        }

        var trimmed = rawPath.Trim();

        // Require an absolute path — relative paths resolve to FFXIV's CWD (the game install
        // folder), not where the user expects, giving confusing "file not found" errors.
        if (!Path.IsPathRooted(trimmed))
        {
            importResult = "Please use a full path, e.g. C:\\Users\\You\\players.txt";
            return;
        }

        // Reject path traversal attempts before normalising
        if (trimmed.Contains(".."))
        {
            importResult = "Path must not contain '..'.";
            return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(trimmed); }
        catch { importResult = "Invalid file path."; return; }

        if (!File.Exists(fullPath)) { importResult = "File not found."; return; }

        // Size guard — 64 KB is far more than enough for any player list
        var info = new FileInfo(fullPath);
        if (info.Length > 65_536) { importResult = "File too large (max 64 KB)."; return; }

        string[] lines;
        try { lines = File.ReadAllLines(fullPath, Encoding.UTF8); }
        catch (Exception ex) { importResult = $"Read error: {ex.Message}"; return; }

        int added = 0, skipped = 0;
        foreach (var raw in lines)
        {
            if (playerList.Count >= 512) break;

            var name = raw.Trim();
            if (name.Length == 0 || name.StartsWith('#')) continue;

            // Silently truncate absurdly long names instead of erroring
            if (name.Length > 64) name = name[..64];

            // "BYE" is reserved for auto-advance slots — skip silently
            if (string.Equals(name, "BYE", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            if (playerList.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            playerList.Add(name);
            added++;
        }

        duplicateWarning = null;
        importResult = added == 0
            ? $"Nothing added ({skipped} duplicate{(skipped != 1 ? "s" : "")})."
            : $"Imported {added} player{(added != 1 ? "s" : "")}" +
              (skipped > 0 ? $", {skipped} duplicate{(skipped != 1 ? "s" : "")} skipped." : ".");
    }

    private void TryAddPlayer()
    {
        var name = newPlayerInput.Trim();
        if (name.Length == 0) return;

        // "BYE" is reserved — the bracket uses it internally for auto-advance slots
        if (string.Equals(name, "BYE", StringComparison.OrdinalIgnoreCase))
        {
            duplicateWarning = "BYE is a reserved keyword";
            return;
        }

        if (playerList.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
        {
            duplicateWarning = name;
            return;
        }

        playerList.Add(name);
        newPlayerInput   = string.Empty;
        duplicateWarning = null;
    }

    // ── Utilities ─────────────────────────────────────────────────────────

    private static float MatchCenterY(int r, int m) =>
        Padding + LabelH + (float)Math.Pow(2, r) * (2 * m + 1) * SlotH;

    private static void PlaceRoundLabel(ImDrawListPtr dl, Vector2 origin, float xCol, string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        var pos  = origin + new Vector2(xCol + (MatchW - size.X) * 0.5f, Padding * 0.4f);
        dl.AddRectFilled(pos - new Vector2(10, 3), pos + size + new Vector2(10, 4),
            Theme.ToU32(color with { W = 0.15f }), 4f);
        dl.AddRect(pos - new Vector2(10, 3), pos + size + new Vector2(10, 4),
            Theme.ToU32(color with { W = 0.22f }), 4f, ImDrawFlags.None, 0.75f);
        dl.AddText(pos, Theme.ToU32(color), text);
    }

    private static string RoundLabel(int r, int numRounds)
    {
        int fromFinal = numRounds - 1 - r;
        return fromFinal switch
        {
            1 => "Semi-Finals",
            2 => "Quarter-Finals",
            _ => r == 0 ? "Round 1" : $"Round {r + 1}",
        };
    }

    private static int TotalParticipants(Tournament t)
    {
        var firstRound = t.Format == BracketFormat.DoubleElim
            ? (t.WBRounds.Count > 0 ? t.WBRounds[0] : null)
            : (t.Rounds.Count   > 0 ? t.Rounds[0]   : null);
        return firstRound?.Sum(m => (m.Player1 != null ? 1 : 0) + (m.IsBye ? 0 : m.Player2 != null ? 1 : 0)) ?? 0;
    }

    /// <summary>
    /// True if the match box (centred at canvas-Y cy, left edge at x) overlaps the visible viewport.
    /// Uses a one-SlotH margin so boxes that are just barely off-screen still render cleanly.
    /// </summary>
    private static bool MatchBoxInView(float cy, float x,
        float scrollY, float scrollX, float viewH, float viewW) =>
        cy + SlotH > scrollY - SlotH       &&
        cy - SlotH < scrollY + viewH + SlotH &&
        x + MatchW > scrollX - MatchW      &&
        x          < scrollX + viewW + MatchW;

    /// <summary>
    /// True if the connector originating from match centre cy might intersect the viewport.
    /// marginY scales with the round so long connectors (late rounds, few matches) are never wrongly culled.
    /// </summary>
    private static bool ConnectorInView(float cy, float x,
        float scrollY, float scrollX, float viewH, float viewW, float marginY) =>
        cy + SlotH > scrollY - marginY       &&
        cy - SlotH < scrollY + viewH + marginY &&
        x + MatchW > scrollX - MatchW - RoundGap &&
        x          < scrollX + viewW + MatchW + RoundGap;

    private static void DrawThickLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float thickness)
    {
        var d = b - a;
        float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len < 0.5f) return;
        var perp = new Vector2(-d.Y / len * (thickness * 0.5f), d.X / len * (thickness * 0.5f));
        dl.AddQuadFilled(a + perp, b + perp, b - perp, a - perp, col);
    }
}
