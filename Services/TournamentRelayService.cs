using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using DeathrollManager.Helpers;
using DeathrollManager.Models;

namespace DeathrollManager.Services;

/// <summary>
/// Broadcasts tournament bracket updates via /say so spectators at the venue can follow
/// along in real-time without running their own game. Also syncs bracket state to the
/// tometools.com/api/dr/{code} Cloudflare Pages Function for the web bracket viewer.
/// </summary>
public class TournamentRelayService : IDisposable
{
    private readonly IChatGui    chatGui;
    private readonly IPluginLog  log;
    private readonly HttpClient  _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // ── Protocol ──────────────────────────────────────────────────────────
    // Messages look like:  [DR·A3F9K2·V1·EVENT·arg1·arg2·...]
    // Middle dot U+00B7 is the field separator — safe in FFXIV player names
    // (names only contain A-Z, spaces, hyphens, apostrophes).

    private const char Sep       = '·';
    private const int  ChunkSize = 20;   // player names per PLY message (legacy fallback)
    private const int  SnapChunk = 420;  // base64 chars per SNAP message (~457 bytes encoded)
    private static readonly Regex MsgPattern = new(
        @"^\[DR·([A-Z0-9]{6})·V1·(.+)\]$", RegexOptions.Compiled);

    // ── Host state ────────────────────────────────────────────────────────
    public string? RelayCode   { get; private set; }
    public bool    IsHosting   => RelayCode != null;
    private string? _writeToken;

    // Key: (side, roundIndex, matchIndex) — "SE"/"WB"/"LB"/"GF"/"GFR"
    private readonly HashSet<(string side, int r, int m)> _broadcastMatches = new();
    private bool _champBroadcast;

    // ── Spectator state ───────────────────────────────────────────────────
    public string?     WatchCode           { get; private set; }
    public bool        IsSpectating        => WatchCode != null;
    public Tournament? SpectatorTournament { get; private set; }
    public string?     SpectatorStatusMsg  { get; private set; }

    // Sender validation — set from the first relay message received.
    // Subsequent messages from a different character are rejected (prevents spoofing).
    private string? _hostSenderName;

    // Pending bracket header (filled by START, consumed by SNAP or PLY assembly)
    private string?       _pendingName;
    private int           _pendingStartNum;
    private long          _pendingBet;
    private string?       _pendingVenue;
    private BracketLayout _pendingLayout;
    private BracketFormat _pendingFormat;
    private int           _expectedSlotCount;

    // Legacy PLY chunk accumulator
    private int                _nextChunkIndex;
    private readonly List<string> _pendingPlayers = new();

    // SNAP chunk accumulator
    private string?[]? _snapChunks;

    // ── Events ────────────────────────────────────────────────────────────
    public event Action? RelayStateChanged;

    public TournamentRelayService(IChatGui chatGui, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.log     = log;
        chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
        _httpClient.Dispose();
    }

    // ── Host API ──────────────────────────────────────────────────────────

    public void StartHostRelay(Tournament t)
    {
        RelayCode        = GenerateCode();
        _writeToken      = GenerateWriteToken();
        _champBroadcast  = false;
        _broadcastMatches.Clear();
        log.Information($"[DR Relay] Hosting code {RelayCode}");
        SendBracketSync(t);
        _ = SyncToWebAsync(t);
        RelayStateChanged?.Invoke();
    }

    public void StopHostRelay()
    {
        if (!IsHosting) return;
        SendRelayMessage("END");
        RelayCode        = null;
        _writeToken      = null;
        _champBroadcast  = false;
        _broadcastMatches.Clear();
        log.Information("[DR Relay] Relay stopped");
        RelayStateChanged?.Invoke();
    }

    /// <summary>Re-broadcasts the full bracket — call this so late joiners can catch up.</summary>
    public void ResyncBroadcast(Tournament t)
    {
        if (!IsHosting) return;
        log.Information("[DR Relay] Resync broadcast");
        SendBracketSync(t);
        _ = SyncToWebAsync(t);
    }

    /// <summary>Called on every TournamentStateChanged — sends only newly-completed matches.</summary>
    public void OnTournamentStateChanged(Tournament? t)
    {
        if (!IsHosting || t == null) return;

        bool newResults = false;
        foreach (var (side, r, m, match) in AllMatchesForRelay(t))
        {
            if (!match.IsCompleted || match.Winner == null || match.IsBye) continue;
            var key = (side, r, m);
            if (!_broadcastMatches.Add(key)) continue; // already sent
            SendRelayMessage(
                $"MATCH{Sep}{side}{Sep}{r}{Sep}{m}{Sep}WIN{Sep}{Sanitize(match.Winner)}");
            newResults = true;
        }

        if (t.IsComplete && t.Champion != null && !_champBroadcast)
        {
            _champBroadcast = true;
            SendRelayMessage($"CHAMP{Sep}{Sanitize(t.Champion)}");
            newResults = true;
        }

        if (newResults) _ = SyncToWebAsync(t);
    }

    public string GetWebUrl() =>
        RelayCode == null ? string.Empty : $"https://tometools.com/bracket?code={RelayCode}";

    // ── Spectator API ─────────────────────────────────────────────────────

    public void JoinRelay(string rawCode)
    {
        var code = rawCode.Trim().ToUpperInvariant();
        if (code.Length != 6) return;

        WatchCode           = code;
        SpectatorTournament = null;
        SpectatorStatusMsg  = $"Joined {code} — waiting for bracket data...";
        _hostSenderName     = null;
        _pendingPlayers.Clear();
        _expectedSlotCount  = 0;
        _nextChunkIndex     = 0;
        _pendingName        = null;
        _snapChunks         = null;

        log.Information($"[DR Relay] Spectating code {code}");
        RelayStateChanged?.Invoke();
    }

    public void LeaveRelay()
    {
        WatchCode           = null;
        SpectatorTournament = null;
        SpectatorStatusMsg  = null;
        _hostSenderName     = null;
        _pendingPlayers.Clear();
        _expectedSlotCount  = 0;
        _snapChunks         = null;
        log.Information("[DR Relay] Left relay");
        RelayStateChanged?.Invoke();
    }

    // ── Chat listener ─────────────────────────────────────────────────────

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsSpectating || WatchCode == null) return;

        var text  = message.Message.TextValue;
        var match = MsgPattern.Match(text);
        if (!match.Success || match.Groups[1].Value != WatchCode) return;

        // Sender validation: the first relay message establishes the host identity.
        // Subsequent messages from a different sender are rejected to prevent spoofing.
        var sender = message.Sender.TextValue;
        if (_hostSenderName == null)
            _hostSenderName = sender;
        else if (!string.Equals(_hostSenderName, sender, StringComparison.OrdinalIgnoreCase))
            return;

        var parts = match.Groups[2].Value.Split(Sep);
        if (parts.Length == 0) return;

        try   { ProcessRelayEvent(parts); }
        catch (Exception ex) { log.Warning(ex, "[DR Relay] Error processing relay event"); }
    }

    private void ProcessRelayEvent(string[] parts)
    {
        switch (parts[0])
        {
            case "START" when parts.Length >= 5:
                _pendingName       = parts[1];
                _pendingStartNum   = int.TryParse(parts[2], out var sn) ? sn : 1000;
                _pendingBet        = long.TryParse(parts[3], out var b)  ? b  : 0;
                _expectedSlotCount = int.TryParse(parts[4], out var sc)  ? sc : 0;
                _pendingVenue      = parts.Length >= 6 ? parts[5] : string.Empty;
                _pendingLayout     = parts.Length >= 7 && parts[6] == "LR"
                    ? BracketLayout.LeftToRight : BracketLayout.VBracket;
                _pendingFormat     = parts.Length >= 8 && parts[7] == "DE"
                    ? BracketFormat.DoubleElim : BracketFormat.SingleElim;
                _pendingPlayers.Clear();
                _nextChunkIndex     = 0;
                _snapChunks         = null;
                SpectatorTournament = null;
                SpectatorStatusMsg  = $"Receiving \"{_pendingName}\"...";
                RelayStateChanged?.Invoke();
                break;

            case "SNAP" when parts.Length >= 4:
                ProcessSnapChunk(parts);
                break;

            // PLY0, PLY1, ... — legacy chunked player list (fallback if Deflate fails on host)
            case string ply when ply.StartsWith("PLY"):
                if (!int.TryParse(ply[3..], out var idx) || idx != _nextChunkIndex) break;
                for (int i = 1; i < parts.Length; i++)
                    _pendingPlayers.Add(parts[i]);
                _nextChunkIndex++;
                if (_pendingPlayers.Count >= _expectedSlotCount && _expectedSlotCount > 0)
                    TryBuildSpectatorBracket();
                break;

            // Format: MATCH·SIDE·r·m·WIN·winner  (SIDE = SE/WB/LB/GF/GFR)
            case "MATCH" when parts.Length >= 6 && parts[4] == "WIN":
                ApplyMatchResult(parts);
                break;

            case "CHAMP" when parts.Length >= 2:
                SpectatorStatusMsg = $"Champion: {parts[1]}";
                RelayStateChanged?.Invoke();
                break;

            case "END":
                SpectatorStatusMsg = "Relay ended by host.";
                RelayStateChanged?.Invoke();
                break;
        }
    }

    // ── SNAP (Deflate-compressed full-state sync) ──────────────────────────
    //
    // Payload (before compression):
    //   Line 0:  Sep-joined player list (same order as seeded bracket)
    //   Line 1+: "SIDE·r·m·winner" for each completed match, in bracket order
    //   (optional last line): "CHAMP·name" if tournament is complete
    //
    // Compressed with Deflate, encoded as Base64, then chunked into 420-char pieces.
    // Message format: SNAP·chunkIndex·totalChunks·base64slice

    private void ProcessSnapChunk(string[] parts)
    {
        if (!int.TryParse(parts[1], out var idx) || !int.TryParse(parts[2], out var total) || total <= 0) return;

        if (idx == 0)
            _snapChunks = new string?[total];

        if (_snapChunks == null || _snapChunks.Length != total || idx >= total) return;
        _snapChunks[idx] = parts[3];

        if (!_snapChunks.All(c => c != null)) return;

        var compressed = string.Concat(_snapChunks);
        _snapChunks = null;
        ProcessSnapComplete(compressed);
    }

    private void ProcessSnapComplete(string compressed)
    {
        var payload = DeflateDecompress(compressed);
        if (payload == null)
        {
            log.Warning("[DR Relay] SNAP decompression failed or exceeded size limit");
            SpectatorStatusMsg = "Relay error: corrupted sync data.";
            RelayStateChanged?.Invoke();
            return;
        }

        if (_pendingName == null || _expectedSlotCount <= 0) return;

        var lines   = payload.Split('\n');
        var players = lines[0].Split(Sep).ToList();
        var slots   = players.Take(_expectedSlotCount).ToList();

        SpectatorTournament = _pendingFormat == BracketFormat.DoubleElim
            ? Tournament.CreateDoubleElim(
                _pendingName, slots, _pendingStartNum, _pendingBet, _pendingVenue ?? string.Empty,
                shuffle: false, layout: _pendingLayout)
            : Tournament.Create(
                _pendingName, slots, _pendingStartNum, _pendingBet, _pendingVenue ?? string.Empty,
                shuffle: false, layout: _pendingLayout);

        // Apply completed match results in order; silently fire no events until the end
        string? champStatus = null;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) continue;
            var lp = lines[i].Split(Sep);
            if (lp.Length >= 2 && lp[0] == "CHAMP")
            {
                champStatus = $"Champion: {lp[1]}";
                continue;
            }
            if (lp.Length < 4) continue;
            if (!int.TryParse(lp[1], out var r) || !int.TryParse(lp[2], out var m)) continue;
            var match = ResolveMatch(SpectatorTournament, lp[0], r, m);
            if (match == null || match.IsCompleted) continue;
            SpectatorTournament.RecordWinner(match, lp[3]);
        }

        SpectatorStatusMsg = champStatus ?? $"Following: {_pendingName}";
        log.Information($"[DR Relay] SNAP bracket built: {_pendingName} ({slots.Count} slots, {lines.Length - 1} results)");
        RelayStateChanged?.Invoke();
    }

    private void TryBuildSpectatorBracket()
    {
        if (_pendingName == null || _expectedSlotCount <= 0) return;

        var slots = _pendingPlayers.Take(_expectedSlotCount).ToList();
        SpectatorTournament = _pendingFormat == BracketFormat.DoubleElim
            ? Tournament.CreateDoubleElim(
                _pendingName, slots, _pendingStartNum, _pendingBet, _pendingVenue ?? string.Empty,
                shuffle: false, layout: _pendingLayout)
            : Tournament.Create(
                _pendingName, slots, _pendingStartNum, _pendingBet, _pendingVenue ?? string.Empty,
                shuffle: false, layout: _pendingLayout);

        SpectatorStatusMsg = $"Following: {_pendingName}";
        log.Information($"[DR Relay] Spectator bracket built: {_pendingName} ({slots.Count} slots)");
        RelayStateChanged?.Invoke();
    }

    private void ApplyMatchResult(string[] parts)
    {
        if (SpectatorTournament == null) return;
        // Format: MATCH·SIDE·r·m·WIN·winner
        var side = parts[1];
        if (!int.TryParse(parts[2], out var r) || !int.TryParse(parts[3], out var m)) return;
        var match = ResolveMatch(SpectatorTournament, side, r, m);
        if (match == null || match.IsCompleted) return; // idempotent — resync may replay results
        SpectatorTournament.RecordWinner(match, parts[5]);
        RelayStateChanged?.Invoke();
    }

    private static TournamentMatch? ResolveMatch(Tournament t, string side, int r, int m)
    {
        if (side == "GF")  return t.GrandFinalsMatch;
        if (side == "GFR") return t.GrandFinalsReset;
        if (side == "LB")
        {
            if (r >= t.LBRounds.Count || m >= t.LBRounds[r].Count) return null;
            return t.LBRounds[r][m];
        }
        // "WB" (DE) or "SE"
        var rounds = t.Format == BracketFormat.DoubleElim ? t.WBRounds : t.Rounds;
        if (r >= rounds.Count || m >= rounds[r].Count) return null;
        return rounds[r][m];
    }

    // ── Broadcast helpers ─────────────────────────────────────────────────

    private void SendBracketSync(Tournament t)
    {
        // Reconstruct the seeded list from the first round of the relevant bracket.
        // DE uses WBRounds[0]; SE uses Rounds[0]. BYE positions preserved so the
        // spectator's Create(shuffle:false) produces an identical bracket layout.
        var seeded     = new List<string>();
        var firstRound = t.Format == BracketFormat.DoubleElim
            ? (t.WBRounds.Count > 0 ? t.WBRounds[0] : null)
            : (t.Rounds.Count   > 0 ? t.Rounds[0]   : null);
        if (firstRound != null)
        {
            foreach (var m in firstRound)
            {
                seeded.Add(m.Player1 ?? "BYE");
                seeded.Add(m.IsBye ? "BYE" : (m.Player2 ?? "BYE"));
            }
        }

        // START header — spectators need bracket metadata before the SNAP payload arrives
        string layoutCode = t.Layout == BracketLayout.LeftToRight ? "LR" : "VB";
        string formatCode = t.Format == BracketFormat.DoubleElim   ? "DE" : "SE";
        SendRelayMessage(
            $"START{Sep}{Sanitize(t.Name)}{Sep}{t.StartingNumber}{Sep}{t.BetAmount}" +
            $"{Sep}{seeded.Count}{Sep}{Sanitize(t.VenueName)}{Sep}{layoutCode}{Sep}{formatCode}");

        // Build SNAP payload: line 0 = players, lines 1+ = results, optional CHAMP line
        var sb = new StringBuilder();
        sb.Append(string.Join(Sep, seeded.Select(Sanitize)));
        foreach (var (side, r, m, match) in AllMatchesForRelay(t))
        {
            if (!match.IsCompleted || match.Winner == null || match.IsBye) continue;
            sb.Append('\n');
            sb.Append($"{side}{Sep}{r}{Sep}{m}{Sep}{Sanitize(match.Winner)}");
        }
        if (t.IsComplete && t.Champion != null)
        {
            sb.Append('\n');
            sb.Append($"CHAMP{Sep}{Sanitize(t.Champion)}");
        }

        var compressed = DeflateCompress(sb.ToString());
        if (compressed != null)
        {
            int total = Math.Max(1, (compressed.Length + SnapChunk - 1) / SnapChunk);
            for (int i = 0; i < total; i++)
            {
                var slice = compressed.Substring(i * SnapChunk,
                    Math.Min(SnapChunk, compressed.Length - i * SnapChunk));
                SendRelayMessage($"SNAP{Sep}{i}{Sep}{total}{Sep}{slice}");
            }
        }
        else
        {
            // Deflate failed — fall back to legacy PLY + MATCH spam
            log.Warning("[DR Relay] Deflate failed, falling back to PLY/MATCH protocol");
            for (int i = 0; i < seeded.Count; i += ChunkSize)
            {
                var slice = seeded.Skip(i).Take(ChunkSize).Select(Sanitize);
                SendRelayMessage($"PLY{i / ChunkSize}{Sep}{string.Join(Sep, slice)}");
            }
            foreach (var (side, r, m, match) in AllMatchesForRelay(t))
            {
                if (!match.IsCompleted || match.Winner == null || match.IsBye) continue;
                SendRelayMessage($"MATCH{Sep}{side}{Sep}{r}{Sep}{m}{Sep}WIN{Sep}{Sanitize(match.Winner)}");
            }
            if (t.IsComplete && t.Champion != null)
                SendRelayMessage($"CHAMP{Sep}{Sanitize(t.Champion)}");
        }

        // Update broadcast tracking so OnTournamentStateChanged only sends new matches
        _broadcastMatches.Clear();
        _champBroadcast = false;
        foreach (var (side, r, m, match) in AllMatchesForRelay(t))
        {
            if (!match.IsCompleted || match.Winner == null || match.IsBye) continue;
            _broadcastMatches.Add((side, r, m));
        }
        if (t.IsComplete && t.Champion != null)
            _champBroadcast = true;
    }

    // Yields every non-structural match with a stable (side, r, m) key usable in MATCH messages.
    // GF and GFR use r=0 m=0 — they're single matches identified entirely by their side code.
    private static IEnumerable<(string side, int r, int m, TournamentMatch match)> AllMatchesForRelay(Tournament t)
    {
        if (t.Format == BracketFormat.DoubleElim)
        {
            for (int r = 0; r < t.WBRounds.Count; r++)
                for (int m = 0; m < t.WBRounds[r].Count; m++)
                    yield return ("WB", r, m, t.WBRounds[r][m]);
            for (int r = 0; r < t.LBRounds.Count; r++)
                for (int m = 0; m < t.LBRounds[r].Count; m++)
                    yield return ("LB", r, m, t.LBRounds[r][m]);
            if (t.GrandFinalsMatch != null) yield return ("GF",  0, 0, t.GrandFinalsMatch);
            if (t.GrandFinalsReset  != null) yield return ("GFR", 0, 0, t.GrandFinalsReset);
        }
        else
        {
            for (int r = 0; r < t.Rounds.Count; r++)
                for (int m = 0; m < t.Rounds[r].Count; m++)
                    yield return ("SE", r, m, t.Rounds[r][m]);
        }
    }

    private void SendRelayMessage(string payload)
    {
        if (!IsHosting) return;
        ChatSender.Send($"/say [DR{Sep}{RelayCode}{Sep}V1{Sep}{payload}]");
    }

    private static string Sanitize(string? s) => (s ?? string.Empty).Replace(Sep, '-');

    // ── Compression ────────────────────────────────────────────────────────

    private static string? DeflateCompress(string text)
    {
        try
        {
            var input = Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal))
                ds.Write(input, 0, input.Length);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    private static string? DeflateDecompress(string base64, int maxBytes = 65536)
    {
        try
        {
            var compressed = Convert.FromBase64String(base64);
            using var input  = new MemoryStream(compressed);
            using var output = new MemoryStream();
            using var ds     = new DeflateStream(input, CompressionMode.Decompress);
            var buf = new byte[4096];
            int read;
            while ((read = ds.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, read);
                if (output.Length > maxBytes) return null; // zip bomb protection
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch { return null; }
    }

    // ── Code / token generation ────────────────────────────────────────────

    private static string GenerateCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // exclude I, O, 1, 0 (visually ambiguous)
        return new string(Enumerable.Range(0, 6)
            .Select(_ => chars[Random.Shared.Next(chars.Length)])
            .ToArray());
    }

    private static string GenerateWriteToken()
    {
        var bytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Web sync ──────────────────────────────────────────────────────────

    private async Task SyncToWebAsync(Tournament t)
    {
        if (_writeToken == null || RelayCode == null) return;
        try
        {
            var json    = SerializeTournamentJson(t);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url     = $"https://tometools.com/api/dr/{RelayCode}";
            using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
            req.Headers.TryAddWithoutValidation("X-Write-Token", _writeToken);
            using var resp = await _httpClient.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                log.Warning($"[DR Relay] Web sync failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DR Relay] Web sync error");
        }
    }

    private static string SerializeTournamentJson(Tournament t)
    {
        static object MatchObj(TournamentMatch m) => new
        {
            p1     = m.Player1,
            p2     = m.Player2,
            status = m.Status switch
            {
                MatchStatus.Completed  => "completed",
                MatchStatus.InProgress => "in_progress",
                MatchStatus.Bye        => "bye",
                _                      => "pending",
            },
            winner = m.Winner,
        };

        object[][] Rounds(List<List<TournamentMatch>> src) =>
            src.Select(r => r.Select(MatchObj).ToArray()).ToArray();

        string layout = t.Layout == BracketLayout.LeftToRight ? "lr" : "vbracket";
        object payload;

        if (t.Format == BracketFormat.DoubleElim)
        {
            var wbRounds = Rounds(t.WBRounds);
            var lbRounds = Rounds(t.LBRounds);
            payload = new
            {
                name                  = t.Name,
                startingNumber        = t.StartingNumber,
                bet                   = t.BetAmount,
                venue                 = t.VenueName,
                isComplete            = t.IsComplete,
                champion              = t.Champion,
                layout,
                format                = "de",
                rounds                = wbRounds,   // kept in "rounds" so function validation passes
                wbRounds,
                lbRounds,
                grandFinals           = t.GrandFinalsMatch == null ? null : MatchObj(t.GrandFinalsMatch),
                grandFinalsReset      = t.GrandFinalsReset  == null ? null : MatchObj(t.GrandFinalsReset),
                grandFinalsNeedsReset = t.GrandFinalsNeedsReset,
            };
        }
        else
        {
            payload = new
            {
                name           = t.Name,
                startingNumber = t.StartingNumber,
                bet            = t.BetAmount,
                venue          = t.VenueName,
                isComplete     = t.IsComplete,
                champion       = t.Champion,
                layout,
                format         = "se",
                rounds         = Rounds(t.Rounds),
            };
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}
