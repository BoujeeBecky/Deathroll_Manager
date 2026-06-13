using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DeathrollManager.Helpers;
using DeathrollManager.Models;
using DeathrollManager.Services;

namespace DeathrollManager.Windows;

/// <summary>
/// Draws the animated battle scene. Call Draw() once per frame from any window or tab.
/// Animation state is shared — both the Main tab and the pop-out window see the same animation.
/// </summary>
public sealed class BattleRenderer : IDisposable
{
    private readonly GameStateService _gameState;

    // Figure dimensions — shared between DrawFighter (layout math) and DrawFigure (drawing)
    private const float FigBodyH            = 46f;
    private const float FigHeadR            = 16f;
    private const float FigLegX             = 17f;
    private const float FigLegH             = 26f;
    private const float FigArmFromShoulder  = 12f;
    private const float FigArmX             = 20f;
    private const float FigArmDrop          = 11f;
    private const float FigThick            = 3.5f;

    private enum FaceExpression { Neutral, Happy, Surprised }

    // Swing animation — triggered by each roll
    private bool   _swingPending;
    private bool   _swingPendingIsP1;
    private double _swingStart  = -999;
    private bool   _swingIsP1;
    private int    _lastRollCount;

    // Flinch animation — triggered for the player who receives the hit
    private bool   _flinchPending;
    private bool   _flinchPendingIsP1;
    private double _flinchStart = -999;
    private bool   _flinchIsP1;

    // Parry animation — triggered when a player rolls exactly the maximum (matches opponent)
    private bool   _parryPending;
    private double _parryStart  = -999;

    // Death animation — triggered when a game ends
    private bool           _deathPending;
    private bool           _deathPendingIsP1;
    private double         _deathStart  = -999;
    private bool           _deathIsP1;
    private DeathrollGame? _lastGame;

    // Test mode — rolls are simulated locally, no chat required
    private bool _testMode;

    // HP bars — fighting-game style. A player's HP is the max THEY are facing
    // (opponent's last roll ÷ starting number): your low roll damages the
    // opponent. Rolling 1 is the exception — the roller killed themselves, so
    // their own bar hits zero with the shatter. Targets are re-derived from
    // game state every frame, so undo/redo/reopen stay consistent for free.
    private float  _hp1Display = 1f, _hp2Display = 1f;
    private float  _hp1Ghost   = 1f, _hp2Ghost   = 1f; // damage trail, drains slower
    private double _lastHpAnimTime;

    // Fragment layout for shatter: (u, v, w, h) normalized within box + velocity (px/s)
    private static readonly (float u, float v, float w, float h, float vx, float vy)[] Frags =
    [
        (0.00f, 0.00f, 0.38f, 0.52f, -85f, -100f),
        (0.38f, 0.00f, 0.26f, 0.52f,   0f, -130f),
        (0.64f, 0.00f, 0.36f, 0.52f,  85f, -100f),
        (0.00f, 0.52f, 0.38f, 0.48f, -70f,   85f),
        (0.38f, 0.52f, 0.26f, 0.48f,   0f,  115f),
        (0.64f, 0.52f, 0.36f, 0.48f,  70f,   85f),
    ];

    public BattleRenderer(GameStateService gameState)
    {
        _gameState = gameState;
        gameState.StateChanged  += OnStateChanged;
        gameState.GameCompleted += OnGameEnd;
    }

    public void Dispose()
    {
        _gameState.StateChanged  -= OnStateChanged;
        _gameState.GameCompleted -= OnGameEnd;
    }

    private void OnStateChanged()
    {
        var game = _gameState.ActiveGame;
        if (game == null) { _lastRollCount = 0; return; }

        if (game.Rolls.Count == 0)
        {
            if (game.VenueName != "Test Mode") _testMode = false;
            CancelAnimations();
        }

        // A live game can never have a death armed (completion nulls ActiveGame),
        // so death state here means undo/reopen rolled time backwards — cancel
        // everything so the "dead" player respawns with their HP bar.
        if (_deathPending || _deathStart > 0)
        {
            CancelAnimations();
            _lastRollCount = game.Rolls.Count; // suppress the swing replay below
        }

        // Trigger attack only on a single new roll — a count jump (reopen) is
        // state restoration, not an attack to animate.
        if (game.Rolls.Count == _lastRollCount + 1)
        {
            var latestRoll = game.Rolls[^1];
            bool isP1 = string.Equals(latestRoll.PlayerName, game.Player1Name,
                StringComparison.OrdinalIgnoreCase);

            _swingPendingIsP1  = isP1;
            _swingPending      = true;

            _flinchPendingIsP1 = !isP1;
            _flinchPending     = true;

            // Parry: rolled exactly the maximum (matched the opponent's previous roll)
            // Requires at least 2 rolls so it doesn't fire on the opening roll
            if (latestRoll.RolledValue == latestRoll.MaxValue
                && latestRoll.RolledValue > 1
                && game.Rolls.Count > 1)
            {
                _parryPending = true;
            }
        }
        _lastRollCount = game.Rolls.Count;
    }

    private void CancelAnimations()
    {
        _swingPending = _flinchPending = _parryPending = _deathPending = false;
        _swingStart   = _flinchStart   = _parryStart   = _deathStart   = -999;
        _lastGame     = null;
    }

    /// <summary>Returns the scene to its idle state: animations cancelled,
    /// HP bars refilled, test mode cleared.</summary>
    public void ResetScene()
    {
        CancelAnimations();
        _testMode      = false;
        _lastRollCount = 0;
        _hp1Display = _hp2Display = 1f;
        _hp1Ghost   = _hp2Ghost   = 1f;
    }

    private void OnGameEnd(DeathrollGame game)
    {
        _lastGame         = game;
        _deathPendingIsP1 = string.Equals(game.LoserName, game.Player1Name,
            StringComparison.OrdinalIgnoreCase);
        _deathPending     = true;
        _lastRollCount    = 0;
    }

    // ── Entry point ───────────────────────────────────────────────────────

    public void Draw()
    {
        double now = ImGui.GetTime();

        // Resolve pending triggers (must run inside a Draw frame for valid timestamps)
        if (_swingPending)  { _swingStart  = now; _swingIsP1  = _swingPendingIsP1;  _swingPending  = false; }
        if (_flinchPending) { _flinchStart = now; _flinchIsP1 = _flinchPendingIsP1; _flinchPending = false; }
        if (_parryPending)  { _parryStart  = now;                                    _parryPending  = false; }
        if (_deathPending)  { _deathStart  = now; _deathIsP1  = _deathPendingIsP1;  _deathPending  = false; }

        // Swing progress: 0→1→0 sine arc over 0.45s
        float swingFrac = 0f;
        if (_swingStart > 0)
        {
            double e = now - _swingStart;
            const double dur = 0.45;
            swingFrac = e < dur ? (float)Math.Sin(e / dur * Math.PI) : 0f;
        }

        // Flinch progress: 0→1→0 sine arc over 0.35s
        float flinchFrac = 0f;
        if (_flinchStart > 0)
        {
            double e = now - _flinchStart;
            const double dur = 0.65;
            flinchFrac = e < dur ? (float)Math.Sin(e / dur * Math.PI) : 0f;
        }

        // Parry progress: linear 0→1 over 0.65s (clamped; drives a sin flash)
        float parryFrac = _parryStart > 0
            ? Math.Clamp((float)((now - _parryStart) / 0.65), 0f, 1f)
            : 0f;

        // Death progress: linear 0→1 over 1.3s, stays at 1
        float deathT  = _deathStart > 0 ? Math.Clamp((float)((now - _deathStart) / 1.3), 0f, 1f) : 0f;
        bool  isDying = _deathStart > 0 && deathT < 1f;

        // Canvas setup
        var   dl     = ImGui.GetWindowDrawList();
        var   origin = ImGui.GetCursorScreenPos();
        float cw     = ImGui.GetContentRegionAvail().X;
        float ch     = 210f;

        DrawCanvasBackground(dl, origin, cw, ch);
        ImGui.Dummy(new Vector2(cw, ch));

        var activeGame  = _gameState.ActiveGame;
        var displayGame = activeGame ?? (isDying || deathT >= 1f ? _lastGame : null);

        if (displayGame == null)
        {
            DrawIdleOverlay(dl, origin, cw, ch);
        }
        else
        {
            float p1Swing  = _swingIsP1  ? swingFrac  : 0f;
            float p2Swing  = !_swingIsP1 ? swingFrac  : 0f;
            float p1Flinch = (_flinchStart > 0 &&  _flinchIsP1) ? flinchFrac : 0f;
            float p2Flinch = (_flinchStart > 0 && !_flinchIsP1) ? flinchFrac : 0f;

            // HP animation — guard dt so the shared tab+popup double-draw
            // doesn't advance the animation twice per frame
            float dt = (float)Math.Max(now - _lastHpAnimTime, 0);
            if (dt > 0)
            {
                dt = Math.Min(dt, 0.1f); // clamp hitches
                AnimateHp(ref _hp1Display, ref _hp1Ghost, TargetHp(displayGame, true),  dt);
                AnimateHp(ref _hp2Display, ref _hp2Ghost, TargetHp(displayGame, false), dt);
                _lastHpAnimTime = now;
            }

            DrawFighter(dl, origin, cw, ch, displayGame, isP1: true,
                p1Swing, p1Flinch, parryFrac,
                (_deathStart > 0 &&  _deathIsP1) ? deathT : 0f,
                _hp1Display, _hp1Ghost);

            DrawFighter(dl, origin, cw, ch, displayGame, isP1: false,
                p2Swing, p2Flinch, parryFrac,
                (_deathStart > 0 && !_deathIsP1) ? deathT : 0f,
                _hp2Display, _hp2Ghost);

            DrawVsLabel(dl, origin, cw, ch, displayGame);

            if (parryFrac > 0f && parryFrac < 1f)
                DrawParryFlash(dl, origin, cw, ch, parryFrac);

            if (p1Flinch > 0f && p1Flinch < 1f)
                DrawHitFlash(dl, origin, cw, ch, p1Flinch, isP1: true);
            if (p2Flinch > 0f && p2Flinch < 1f)
                DrawHitFlash(dl, origin, cw, ch, p2Flinch, isP1: false);
        }

        // Controls below canvas
        ImGui.Spacing();
        if (activeGame != null)
            DrawRollControls(activeGame);
        else if (_lastGame != null && _deathStart > 0)
            DrawOutcomeStrip(_lastGame);
        else
            DrawTestStartButton();
    }

    // ── HP model ──────────────────────────────────────────────────────────

    private static float TargetHp(DeathrollGame game, bool isP1)
    {
        string name = isP1 ? game.Player1Name : game.Player2Name;

        // The fatal 1 kills the roller — their own bar empties (with the shatter)
        if (game.Status == GameStatus.Completed &&
            string.Equals(game.LoserName, name, StringComparison.OrdinalIgnoreCase))
            return 0f;

        if (game.StartingNumber <= 0) return 1f;

        // HP = the max this player faces: the opponent's last roll ÷ starting
        // number. The fatal roll is excluded so the winner's bar doesn't also
        // collapse to 1/start when the game ends.
        var oppRoll = game.Rolls.LastOrDefault(r =>
            !string.Equals(r.PlayerName, name, StringComparison.OrdinalIgnoreCase) && !r.IsGameOver);

        return oppRoll == null
            ? 1f
            : Math.Clamp((float)oppRoll.RolledValue / game.StartingNumber, 0f, 1f);
    }

    // Live bar eases quickly toward the target; the ghost trail holds, then
    // drains at a constant rate behind it — fighting-game damage trail.
    private static void AnimateHp(ref float display, ref float ghost, float target, float dt)
    {
        display = target > display
            ? Math.Min(display + dt * 2.5f, target)                  // refill (new game / undo): quick sweep
            : display + (target - display) * Math.Min(dt * 9f, 1f);  // damage: fast ease-out
        if (Math.Abs(target - display) < 0.001f) display = target;

        ghost = ghost < display
            ? display                                   // never behind the live bar
            : Math.Max(ghost - dt * 0.30f, display);    // slow constant drain
    }

    // ── Canvas background ─────────────────────────────────────────────────

    private static void DrawCanvasBackground(ImDrawListPtr dl, Vector2 o, float cw, float ch)
    {
        dl.AddRectFilled(o, o + new Vector2(cw, ch),
            Theme.ToU32(new Vector4(0.10f, 0.10f, 0.20f, 1f)), 8f);
        dl.AddRect(o, o + new Vector2(cw, ch),
            Theme.ToU32(Theme.CardBorder with { W = 0.65f }), 8f, ImDrawFlags.None, 1.5f);
    }

    // ── Idle overlay ──────────────────────────────────────────────────────

    private static void DrawIdleOverlay(ImDrawListPtr dl, Vector2 o, float cw, float ch)
    {
        double t     = ImGui.GetTime();
        float  figY  = o.Y + ch * 0.60f;
        var    color = Theme.Muted with { W = 0.55f };

        // Two idle figures swaying in opposing phase so they don't mirror each other exactly
        double ph1 = t * 1.1;
        double ph2 = t * 1.1 + Math.PI;

        float p1cx = o.X + cw * 0.25f + (float)Math.Sin(ph1) * 3f;
        float p1fy = figY + MathF.Abs((float)Math.Sin(ph1 * 0.9f)) * -2f;
        float p2cx = o.X + cw * 0.75f + (float)Math.Sin(ph2) * 3f;
        float p2fy = figY + MathF.Abs((float)Math.Sin(ph2 * 0.9f)) * -2f;

        DrawFigure(dl, p1cx, p1fy, color, -45f, 26f, false, 0f, FaceExpression.Happy, 0f);
        DrawFigure(dl, p2cx, p2fy, color, 225f, 26f, true,  0f, FaceExpression.Happy, 0f);

        const string msg = "No Active Game";
        var sz = ImGui.CalcTextSize(msg);
        dl.AddText(o + new Vector2((cw - sz.X) * 0.5f, ch * 0.38f),
            Theme.ToU32(Theme.Muted with { W = 0.5f }), msg);
    }

    // ── Fighter ───────────────────────────────────────────────────────────

    private static void DrawFighter(ImDrawListPtr dl, Vector2 o, float cw, float ch,
        DeathrollGame game, bool isP1,
        float swingFrac, float flinchFrac, float parryFrac, float deathT,
        float hpFrac, float hpGhost)
    {
        const float boxW   = 140f;
        const float boxH   = 36f;
        const float rvBoxW = 46f;

        float baseCx = o.X + cw * (isP1 ? 0.22f : 0.78f);
        float boxY   = o.Y + ch - boxH - 14f;
        var   boxMin = new Vector2(baseCx - boxW * 0.5f, boxY);
        var   boxMax = boxMin + new Vector2(boxW, boxH);

        string name      = isP1 ? game.Player1Name : game.Player2Name;
        var    teamColor = isP1 ? Theme.Player1 : Theme.Player2;
        bool   isTurn    = string.Equals(game.CurrentPlayerTurn, name, StringComparison.OrdinalIgnoreCase);
        var    boxColor  = Theme.DangerGradient(game.DangerLevel);

        // ── Figure position ──────────────────────────────────────────────
        float stepDir = isP1 ? 1f : -1f;

        // Step forward during attack
        float stepX = swingFrac * 14f * stepDir;

        // Recoil + rapid shake when flinching
        float recoilX = flinchFrac > 0 ? (float)Math.Sin(flinchFrac * Math.PI) * -16f * stepDir : 0f;
        float shakeX  = flinchFrac > 0.05f && flinchFrac < 0.90f
            ? (float)Math.Sin(flinchFrac * Math.PI * 8f) * 3f : 0f;

        // Both sides step slightly forward during parry
        float parryX = parryFrac > 0f ? (float)Math.Sin(parryFrac * Math.PI) * 8f * stepDir : 0f;

        // Gentle idle sway and bob (only when no other animation is running)
        bool   isIdle  = swingFrac == 0f && flinchFrac == 0f && deathT == 0f;
        double idlePh  = ImGui.GetTime() * 1.1 + (isP1 ? 0.0 : Math.PI);
        float  swayX   = isIdle ? (float)Math.Sin(idlePh) * 3f : 0f;
        float  bobY    = isIdle ? MathF.Abs((float)Math.Sin(idlePh * 0.9)) * -2f : 0f;

        // Torso leans toward opponent during attack (shoulder shifts, feet stay planted)
        float leanX = swingFrac * 6f * stepDir;

        float cx    = baseCx + stepX + recoilX + shakeX + parryX + swayX;
        float feetY = boxY - FigLegH - 6f + bobY;

        // ── Figure color / alpha ─────────────────────────────────────────
        float figAlpha = deathT > 0 ? Math.Max(1f - deathT * 1.05f, 0f) : 1f;
        var   figColor = deathT > 0
            ? Vector4.Lerp(teamColor, Theme.Danger, Math.Min(deathT * 2.5f, 1f)) with { W = figAlpha }
            : teamColor with { W = isTurn ? 1.0f : 0.55f };

        // ── Face expression ──────────────────────────────────────────────
        var face = FaceExpression.Happy;
        if      (deathT > 0)                               face = FaceExpression.Surprised;
        else if (flinchFrac > 0.08f && flinchFrac < 0.88f) face = FaceExpression.Surprised;
        else if (parryFrac  > 0.05f && parryFrac  < 0.80f) face = FaceExpression.Surprised;

        // ── Sword angle ──────────────────────────────────────────────────
        float swordAng = isP1
            ? -45f + swingFrac * 68f   // P1: up-right → down-right
            : 225f - swingFrac * 68f;  // P2: up-left  → down-left

        // Idle sword oscillation: slow raise-and-lower cycle while at rest, opposing phase per side
        if (isIdle)
        {
            double swOsc = ImGui.GetTime() * 1.6 + (isP1 ? 0.0 : Math.PI);
            swordAng += (float)Math.Sin(swOsc) * 18f;
        }

        // ── Name box ────────────────────────────────────────────────────
        if (deathT > 0)
            DrawShatterBox(dl, boxMin, boxW, boxH, boxColor, deathT);
        else
            DrawNameBox(dl, boxMin, boxMax, name, teamColor, isTurn, hpFrac, hpGhost);

        // ── Roll value box ───────────────────────────────────────────────
        if (deathT <= 0)
        {
            var lastRoll = game.Rolls.LastOrDefault(r =>
                string.Equals(r.PlayerName, name, StringComparison.OrdinalIgnoreCase));
            string rollStr = lastRoll != null ? lastRoll.RolledValue.ToString("N0") : "--";
            bool   isDead  = lastRoll?.IsGameOver == true;

            var rvMin = isP1
                ? new Vector2(boxMax.X,          boxY)
                : new Vector2(boxMin.X - rvBoxW, boxY);
            var rvMax = rvMin + new Vector2(rvBoxW, boxH);

            dl.AddRectFilled(rvMin, rvMax, Theme.ToU32(isDead ? Theme.Danger with { W = 0.30f } : Theme.CardBg), 4f);
            dl.AddRect(rvMin, rvMax, Theme.ToU32(teamColor with { W = 0.55f }), 4f, ImDrawFlags.None, 1f);

            var rtSz = ImGui.CalcTextSize(rollStr);
            dl.AddText(rvMin + new Vector2((rvBoxW - rtSz.X) * 0.5f, (boxH - rtSz.Y) * 0.5f),
                Theme.ToU32(isDead ? Theme.Danger : Theme.White), rollStr);
        }

        // ── Stick figure ─────────────────────────────────────────────────
        DrawFigure(dl, cx, feetY, figColor, swordAng, 28f, !isP1, swingFrac, face, leanX);

        // ── Turn glow on ground ──────────────────────────────────────────
        if (isTurn && deathT <= 0)
        {
            float pulse = (float)(Math.Sin(ImGui.GetTime() * 4.0) * 0.5 + 0.5);
            dl.AddCircleFilled(new Vector2(baseCx, boxY - 3f),
                20f, Theme.ToU32(teamColor with { W = pulse * 0.16f }), 24);
        }
    }

    // ── Parry flash ───────────────────────────────────────────────────────

    private static void DrawParryFlash(ImDrawListPtr dl, Vector2 o, float cw, float ch, float t)
    {
        float flash  = (float)Math.Sin(t * Math.PI); // 0→1→0
        var   center = new Vector2(o.X + cw * 0.5f, o.Y + ch * 0.42f);

        dl.AddCircleFilled(center, flash * 38f, Theme.ToU32(Theme.Gold with { W = flash * 0.30f }), 32);

        const string txt = "PARRY!";
        var sz = ImGui.CalcTextSize(txt);
        dl.AddText(center - new Vector2(sz.X * 0.5f, sz.Y * 0.5f),
            Theme.ToU32(Theme.Gold with { W = flash }), txt);
    }

    // ── Hit flash ─────────────────────────────────────────────────────────

    private static void DrawHitFlash(ImDrawListPtr dl, Vector2 o, float cw, float ch, float t, bool isP1)
    {
        float flash = (float)Math.Sin(t * Math.PI); // 0→1→0 matching the flinch arc
        float cx    = o.X + cw * (isP1 ? 0.22f : 0.78f);
        float cy    = o.Y + ch * 0.32f;

        dl.AddCircleFilled(new Vector2(cx, cy), flash * 26f, Theme.ToU32(Theme.Danger with { W = flash * 0.22f }), 24);

        const string txt = "HIT!";
        var sz = ImGui.CalcTextSize(txt);
        dl.AddText(new Vector2(cx - sz.X * 0.5f, cy - sz.Y * 0.5f),
            Theme.ToU32(Theme.Danger with { W = flash }), txt);
    }

    // ── Name box / HP bar ─────────────────────────────────────────────────

    private static void DrawNameBox(ImDrawListPtr dl, Vector2 min, Vector2 max,
        string name, Vector4 teamColor, bool isTurn, float hpFrac, float hpGhost)
    {
        float w = max.X - min.X;
        float h = max.Y - min.Y;

        // Empty-bar background
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.CardBg), 5f);

        // Ghost trail — pale red segment lagging behind the live bar after damage
        if (hpGhost > hpFrac + 0.003f)
        {
            var gMin = new Vector2(min.X + w * hpFrac, min.Y);
            var gMax = new Vector2(min.X + w * Math.Min(hpGhost, 1f), max.Y);
            dl.AddRectFilled(gMin, gMax, Theme.ToU32(Theme.Danger with { W = 0.40f }),
                hpGhost > 0.97f ? 5f : 0f, ImDrawFlags.RoundCornersRight);
        }

        // Live HP fill — danger-gradient tint, pulsing when critical
        if (hpFrac > 0.003f)
        {
            var   fill = Theme.DangerGradient(1f - hpFrac);
            float a    = hpFrac < 0.25f
                ? 0.40f + (float)(Math.Sin(ImGui.GetTime() * 6.0) * 0.5 + 0.5) * 0.35f
                : 0.55f;
            dl.AddRectFilled(min, new Vector2(min.X + w * hpFrac, max.Y),
                Theme.ToU32(fill with { W = a }), 5f,
                hpFrac > 0.97f ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
        }

        dl.AddRect(min, max,
            Theme.ToU32(isTurn ? teamColor : Theme.CardBorder), 5f, ImDrawFlags.None, isTurn ? 2f : 1f);

        if (isTurn)
        {
            float p = (float)(Math.Sin(ImGui.GetTime() * 3.5) * 0.5 + 0.5);
            dl.AddRect(min, max, Theme.ToU32(teamColor with { W = p * 0.50f }), 5f, ImDrawFlags.None, 3f);
        }

        var   ts    = ImGui.CalcTextSize(name);
        float scale = ts.X > w - 8f ? (w - 8f) / ts.X : 1f;
        dl.AddText(min + new Vector2((w - ts.X * scale) * 0.5f, (h - ts.Y) * 0.5f),
            Theme.ToU32(isTurn ? Theme.White : Theme.Muted), name);
    }

    // ── Shatter box ───────────────────────────────────────────────────────

    private static void DrawShatterBox(ImDrawListPtr dl, Vector2 boxMin, float boxW, float boxH,
        Vector4 color, float t)
    {
        float ease = t * t;
        foreach (var (u, v, fw, fh, vx, vy) in Frags)
        {
            var   fMin = boxMin + new Vector2(u * boxW + vx * ease, v * boxH + vy * ease);
            var   fMax = fMin + new Vector2(fw * boxW, fh * boxH);
            float a    = Math.Max(1f - t * 1.35f, 0f);

            dl.AddRectFilled(fMin, fMax, Theme.ToU32(color           with { W = 0.28f * a }), 2f);
            dl.AddRect(fMin,      fMax,  Theme.ToU32(Theme.CardBorder with { W = a        }), 2f, ImDrawFlags.None, 1f);
        }
    }

    // ── VS label ──────────────────────────────────────────────────────────

    private static void DrawVsLabel(ImDrawListPtr dl, Vector2 o, float cw, float ch, DeathrollGame game)
    {
        const string vs  = "V.S.";
        var          sz  = ImGui.CalcTextSize(vs);
        float midX = o.X + (cw - sz.X) * 0.5f;
        float midY = o.Y + ch * 0.52f - sz.Y * 0.5f;
        dl.AddText(new Vector2(midX, midY), Theme.ToU32(Theme.Muted with { W = 0.7f }), vs);

        if (game.BetAmount > 0)
        {
            string bet = $"{game.BetAmount:N0} gil";
            var    bsz = ImGui.CalcTextSize(bet);
            dl.AddText(new Vector2(o.X + (cw - bsz.X) * 0.5f, midY + sz.Y + 4f),
                Theme.ToU32(Theme.Gold with { W = 0.85f }), bet);
        }
    }

    // ── Stick figure ──────────────────────────────────────────────────────

    private static void DrawFigure(ImDrawListPtr dl, float cx, float feetY,
        Vector4 color, float swordAngleDeg, float swordLen, bool leftSword,
        float swingFrac = 0f, FaceExpression face = FaceExpression.Neutral, float leanX = 0f)
    {
        // Shoulder follows the lean; feet stay planted at cx
        float shoulderX = cx + leanX;
        float shoulderY = feetY - FigBodyH;
        float headCY    = shoulderY - FigHeadR - 2f;
        float armY      = shoulderY + FigArmFromShoulder;

        uint col    = Theme.ToU32(color);
        uint eyeCol = Theme.ToU32(new Vector4(0f, 0f, 0f, Math.Min(color.W * 0.80f, 1f)));

        // Body (shoulder leans, feet planted — creates forward lean on attack)
        DrawThickLine(dl, new Vector2(shoulderX, shoulderY), new Vector2(cx, feetY), col, FigThick);

        // Legs
        DrawThickLine(dl, new Vector2(cx, feetY), new Vector2(cx - FigLegX, feetY + FigLegH), col, FigThick);
        DrawThickLine(dl, new Vector2(cx, feetY), new Vector2(cx + FigLegX, feetY + FigLegH), col, FigThick);

        // Arms — attacking arm extends and rises during swing; back arm counter-swings for balance
        float attackDir   = leftSword ? -1f : 1f;
        float atkReach    = swingFrac *  9f * attackDir;
        float atkRise     = swingFrac * -6f;
        float balReach    = swingFrac * -5f * attackDir; // back arm swings opposite

        float atkEndX = shoulderX + FigArmX * attackDir + atkReach;
        float atkEndY = armY + FigArmDrop + atkRise;
        float balEndX = shoulderX - FigArmX * attackDir + balReach;
        float balEndY = armY + FigArmDrop;

        DrawThickLine(dl, new Vector2(shoulderX, armY), new Vector2(atkEndX, atkEndY), col, FigThick);
        DrawThickLine(dl, new Vector2(shoulderX, armY), new Vector2(balEndX, balEndY), col, FigThick);

        // Head (centered on shoulderX so it follows the lean)
        dl.AddCircleFilled(new Vector2(shoulderX, headCY), FigHeadR, col, 20);

        // Eyes — larger and raised when surprised
        float eyeR = face == FaceExpression.Surprised ? 3.5f : 2.5f;
        float eyeY = face == FaceExpression.Surprised ? headCY - 4.0f : headCY - 2.0f;
        dl.AddCircleFilled(new Vector2(shoulderX - 5f, eyeY), eyeR, eyeCol, 8);
        dl.AddCircleFilled(new Vector2(shoulderX + 5f, eyeY), eyeR, eyeCol, 8);

        // Mouth
        switch (face)
        {
            case FaceExpression.Happy:
                // V-shape smile: two short diagonals meeting at the chin
                DrawThickLine(dl,
                    new Vector2(shoulderX - 4.5f, headCY + 4.0f),
                    new Vector2(shoulderX - 0.5f, headCY + 6.5f), eyeCol, 1.5f);
                DrawThickLine(dl,
                    new Vector2(shoulderX + 0.5f, headCY + 6.5f),
                    new Vector2(shoulderX + 4.5f, headCY + 4.0f), eyeCol, 1.5f);
                break;

            case FaceExpression.Surprised:
                // Open-mouth O: small filled dark circle
                dl.AddCircleFilled(new Vector2(shoulderX, headCY + 6.0f), 3.2f, eyeCol, 12);
                break;
        }

        // Sword — hand position follows the attacking arm endpoint
        float handX = atkEndX;
        float handY = atkEndY;
        float rad   = swordAngleDeg * MathF.PI / 180f;
        float cosA  = MathF.Cos(rad);
        float sinA  = MathF.Sin(rad);

        // Grip
        DrawThickLine(dl, new Vector2(handX, handY),
            new Vector2(handX - cosA * 9f, handY - sinA * 9f), col, 2.5f);
        // Guard
        float perpRad   = rad + MathF.PI * 0.5f;
        const float guardHalf = 6f;
        var guardPt = new Vector2(handX - cosA * 5f, handY - sinA * 5f);
        DrawThickLine(dl,
            guardPt + new Vector2(MathF.Cos(perpRad) * guardHalf, MathF.Sin(perpRad) * guardHalf),
            guardPt - new Vector2(MathF.Cos(perpRad) * guardHalf, MathF.Sin(perpRad) * guardHalf),
            col, 2f);
        // Blade
        DrawThickLine(dl, new Vector2(handX, handY),
            new Vector2(handX + cosA * swordLen, handY + sinA * swordLen), col, 2.5f);
    }

    // Draws a thick line as a filled quad — AddLine silently does nothing in Dalamud.Bindings.ImGui
    private static void DrawThickLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float thickness)
    {
        var   d   = b - a;
        float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len < 0.5f) return;
        var perp = new Vector2(-d.Y / len * (thickness * 0.5f), d.X / len * (thickness * 0.5f));
        dl.AddQuadFilled(a + perp, b + perp, b - perp, a - perp, col);
    }

    // ── Controls below canvas ─────────────────────────────────────────────

    private void DrawRollControls(DeathrollGame game)
    {
        float pulse  = (float)(Math.Sin(ImGui.GetTime() * 4.0) * 0.5 + 0.5);
        bool  isP1   = game.CurrentPlayerTurn == game.Player1Name;
        var   tColor = (isP1 ? Theme.Player1 : Theme.Player2) with { W = 0.6f + pulse * 0.4f };

        string turnText = _testMode
            ? $"▶  {game.CurrentPlayerTurn}'s turn  ◀  [Test]"
            : $"▶  {game.CurrentPlayerTurn}'s turn  ◀";
        float avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - ImGui.CalcTextSize(turnText).X) * 0.5f);
        ImGui.TextColored(tColor, turnText);
        ImGui.Spacing();

        var  localName = Plugin.PlayerState.CharacterName ?? string.Empty;
        bool isMyTurn  = _testMode || (localName.Length > 0 &&
                         string.Equals(localName, game.CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase));

        const float btnW = 260f;
        const float btnH = 36f;
        ImGui.SetCursorPosX((avail - btnW) * 0.5f);

        if (isMyTurn)
        {
            var btnCol = Vector4.Lerp(Theme.Safe, Theme.WinGreen, pulse);
            ImGui.PushStyleColor(ImGuiCol.Button,        btnCol);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.WinGreen with { W = 0.85f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.WinGreen);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
            string label = _testMode
                ? $"🎲  Roll for {game.CurrentPlayerTurn}"
                : $"🎲  Roll!  /random {game.CurrentMax}";
            if (ImGui.Button(label, new Vector2(btnW, btnH)))
            {
                if (_testMode) SimulateRoll(game);
                else           ChatSender.SendRandom(game.CurrentMax);
            }
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button($"🎲  /random {game.CurrentMax}", new Vector2(btnW, btnH));
            ImGui.EndDisabled();
            string wait = $"Waiting for {game.CurrentPlayerTurn}…";
            ImGui.SetCursorPosX((avail - ImGui.CalcTextSize(wait).X) * 0.5f);
            ImGui.TextColored(Theme.Muted, wait);
        }
    }

    private void DrawTestStartButton()
    {
        float avail = ImGui.GetContentRegionAvail().X;
        const float btnW = 220f;
        ImGui.SetCursorPosX((avail - btnW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.Muted with { W = 0.15f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Muted with { W = 0.25f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Muted with { W = 0.35f });
        if (ImGui.Button("🎲  Start Test Battle", new Vector2(btnW, 30f)))
        {
            string p1 = Plugin.PlayerState.CharacterName ?? "Player 1";
            _gameState.StartGame(p1, "Test Bot", 1000, 0, "Test Mode");
            _testMode = true;
        }
        ImGui.PopStyleColor(3);
        var hint = ImGui.CalcTextSize("Simulates rolls locally — no chat needed");
        ImGui.SetCursorPosX((avail - hint.X) * 0.5f);
        ImGui.TextColored(Theme.Muted with { W = 0.4f }, "Simulates rolls locally — no chat needed");
    }

    private void SimulateRoll(DeathrollGame game)
    {
        int roll = Random.Shared.Next(1, game.CurrentMax + 1);
        _gameState.TryAddRoll(game.CurrentPlayerTurn, roll, game.CurrentMax);
    }

    private void DrawOutcomeStrip(DeathrollGame game)
    {
        ImGui.Spacing();
        float avail = ImGui.GetContentRegionAvail().X;
        float pulse = (float)(Math.Sin(ImGui.GetTime() * 2.0) * 0.4 + 0.6);

        string winner = $"🏆  {game.WinnerName} wins!";
        ImGui.SetCursorPosX((avail - ImGui.CalcTextSize(winner).X) * 0.5f);
        ImGui.TextColored(Theme.WinGreen * new Vector4(1, 1, 1, pulse), winner);

        string loser = $"💀  {game.LoserName} rolled 1";
        ImGui.SetCursorPosX((avail - ImGui.CalcTextSize(loser).X) * 0.5f);
        ImGui.TextColored(Theme.Danger, loser);

        ImGui.Spacing();
        const float btnW = 160f;
        ImGui.SetCursorPosX((avail - btnW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Muted with { W = 0.15f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Muted with { W = 0.25f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Muted with { W = 0.35f }));
        if (ImGui.Button("⟲ Reset Battle", new Vector2(btnW, 26f)))
            ResetScene();
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear the scene and respawn the fighters\n(the finished game stays in History)");
    }
}
