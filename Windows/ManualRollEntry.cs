using Dalamud.Bindings.ImGui;
using DeathrollManager.Models;
using DeathrollManager.Services;

namespace DeathrollManager.Windows;

/// <summary>
/// Compact "type the roll in by hand" widget for when chat detection misses a roll.
/// Drawn in both the Game tab and the Tournament MC controls — input state is
/// shared because only one game is ever active at a time.
/// </summary>
internal static class ManualRollEntry
{
    private static int    _value;
    private static string _feedback = string.Empty;
    private static double _feedbackUntil;

    public static void Draw(GameStateService gameState, string id)
    {
        var game = gameState.ActiveGame;
        if (game == null) return;
        if (game.Status is not (GameStatus.WaitingForFirstRoll or GameStatus.InProgress)) return;

        ImGui.TextColored(Theme.Muted, "Missed a roll? Type it in:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt($"##manualroll{id}", ref _value, 0, 0);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"The value that was rolled (1–{game.CurrentMax:N0})");

        bool valid     = _value >= 1 && _value <= game.CurrentMax;
        bool firstRoll = game.Status == GameStatus.WaitingForFirstRoll;

        // On the first roll either player may open (turn order self-corrects);
        // after that, only the current player's turn can be recorded.
        ImGui.SameLine();
        DrawRecordButton(gameState, game, game.CurrentPlayerTurn, valid, id);
        if (firstRoll)
        {
            ImGui.SameLine();
            var other = game.CurrentPlayerTurn == game.Player1Name ? game.Player2Name : game.Player1Name;
            DrawRecordButton(gameState, game, other, valid, id + "b");
        }

        if (game.Rolls.Count > 0)
        {
            bool canUndo = gameState.CanUndo;
            ImGui.SameLine();
            if (!canUndo) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"↶ Undo##undo{id}") && canUndo)
                gameState.UndoLastRoll();
            if (!canUndo) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                var last = game.Rolls[^1];
                ImGui.SetTooltip(canUndo
                    ? $"Remove the last recorded roll\n({last.PlayerName}: {last.RolledValue:N0})"
                    : "Undo limit reached (10 steps) — redo or add a roll first");
            }
        }

        if (gameState.CanRedo)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"↷ Redo##redo{id}"))
                gameState.RedoLastUndoneRoll();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Re-apply the last undone roll ({gameState.RedoCount} available)");
        }

        if (!valid)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"(enter 1–{game.CurrentMax:N0})");
        }
        else if (_feedback.Length > 0 && ImGui.GetTime() < _feedbackUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, _feedback);
        }
    }

    private static void DrawRecordButton(GameStateService gameState, DeathrollGame game,
        string player, bool valid, string id)
    {
        if (!valid) ImGui.BeginDisabled();
        bool clicked = ImGui.SmallButton($"➕ {player}##rec{id}");
        if (!valid) ImGui.EndDisabled();

        if (valid && ImGui.IsItemHovered())
            ImGui.SetTooltip(_value == 1
                ? $"Record: {player} rolled 1 💀 — this ends the game!"
                : $"Record: {player} rolled {_value:N0} (out of {game.CurrentMax:N0})");

        if (clicked && valid)
        {
            if (gameState.TryAddManualRoll(player, _value))
            {
                _value    = 0;
                _feedback = string.Empty;
            }
            else
            {
                _feedback      = "Not accepted — is it that player's turn?";
                _feedbackUntil = ImGui.GetTime() + 4.0;
            }
        }
    }
}
