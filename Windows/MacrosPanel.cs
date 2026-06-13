using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DeathrollManager.Helpers;

namespace DeathrollManager.Windows;

/// <summary>
/// The "Macros" tab — user-defined free-text chat macros. A slimmed-down,
/// in-plugin version of tometools.com Shoutmaker: text is split at word
/// boundaries to a per-message char limit, then either sent to chat directly
/// (paced ~2s apart) or copied in FFXIV User Macro format with /wait 2 lines.
/// </summary>
public sealed class MacrosPanel
{
    private const int MaxMessages = 15; // FFXIV macros are 15 lines — same cap as Shoutmaker

    private readonly Configuration config;

    private int  _selIdx = -1;
    private bool _dirty;

    // FFXIV-safe decorative symbols (BMP). OS emoji are deliberately absent —
    // the game's chat font can't render them; these match Shoutmaker's set.
    private static readonly string[] Symbols =
        ["★", "☆", "♥", "♡", "♦", "♣", "♠", "♪", "♫", "●", "○", "■", "▲", "▼", "▶", "◀", "‼"];

    // Channel options shown in the combo, in display order
    private static readonly MCChannel[] ChanValues =
        [MCChannel.Say, MCChannel.Yell, MCChannel.Shout, MCChannel.Party, MCChannel.FreeCompany];
    private const string ChanComboItems = "/say\0/yell\0/shout\0/p\0/fc\0";

    public MacrosPanel(Configuration config) => this.config = config;

    // ── Text splitting (ported from Shoutmaker's smSplit) ─────────────────
    // Word-boundary split to the limit; oversized words hard-split. Unlike the
    // website (which collapses all whitespace), explicit newlines in the text
    // box always start a new message — intuitive for multi-part macros.

    internal static List<string> SplitText(string text, int limit)
    {
        // limit guard: a hand-edited config with limit <= 0 would loop forever
        // in the hard-split below. Control chars (incl. \r from pasted Windows
        // newlines) become spaces — they have no business in a chat message.
        limit = Math.Max(limit, 10);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c == '\n' ? c : char.IsControl(c) ? ' ' : c);

        var chunks = new List<string>();
        foreach (var para in sb.ToString().Split('\n'))
        {
            var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cur   = string.Empty;
            foreach (var w in words)
            {
                var test = cur.Length == 0 ? w : cur + " " + w;
                if (test.Length <= limit) { cur = test; continue; }

                if (cur.Length > 0) chunks.Add(cur);
                var rest = w;
                while (rest.Length > limit)
                {
                    chunks.Add(rest[..limit]);
                    rest = rest[limit..];
                }
                cur = rest;
            }
            if (cur.Length > 0) chunks.Add(cur);
        }
        return chunks;
    }

    // ── Draw ──────────────────────────────────────────────────────────────

    public void Draw()
    {
        var macros = config.ChatMacros;
        if (_selIdx >= macros.Count) _selIdx = macros.Count - 1;

        var avail = ImGui.GetContentRegionAvail();
        const float navW = 150f;

        // Left nav panel — same pattern as the Help tab
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.ToU32(Theme.CardBg));
        if (ImGui.BeginChild("##MacroNav", new Vector2(navW, avail.Y), true))
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Gold with { W = 0.18f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Gold with { W = 0.30f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.Gold with { W = 0.45f }));
            if (ImGui.Button("➕ Add Macro", new Vector2(-1, 0)))
            {
                var macro = new ChatMacro { Name = NextMacroName() };
                macros.Add(macro);
                config.Save();
                SelectMacro(macros.Count - 1);
            }
            ImGui.PopStyleColor(3);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            for (int i = 0; i < macros.Count; i++)
            {
                bool sel = _selIdx == i;
                if (sel) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ToU32(Theme.Gold));
                string label = sel && _dirty ? $"  {macros[i].Name} *" : $"  {macros[i].Name}";
                if (ImGui.Selectable($"{label}##mn{i}", sel) && !sel)
                    SelectMacro(i);
                if (sel) ImGui.PopStyleColor();
            }

            if (macros.Count == 0)
                ImGui.TextColored(Theme.Muted with { W = 0.6f }, "  No macros yet");
            ImGui.EndChild();
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // Right content panel
        if (!ImGui.BeginChild("##MacroContent", ImGui.GetContentRegionAvail(), false)) return;
        if (_selIdx < 0 || _selIdx >= macros.Count)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Create a macro on the left, or pick one to edit.");
            ImGui.Spacing();
            ImGui.TextWrapped("Macros are reusable chat snippets — hype lines, venue plugs, " +
                              "anything you want to fire off mid-match with one click. " +
                              "Long text auto-splits at word boundaries, just like Shoutmaker on tometools.com.");
        }
        else
        {
            // Per-selection ID scope — ImGui keeps active-edit state per widget
            // ID, so without this, switching macros mid-edit could bleed the
            // in-flight text of one macro into another.
            ImGui.PushID(_selIdx);
            DrawEditor(macros[_selIdx]);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void DrawEditor(ChatMacro macro)
    {
        var chunks = SplitText(macro.Text, Math.Clamp(config.MacroCharLimit, 50, 200));
        bool truncated = chunks.Count > MaxMessages;
        string cmd = Configuration.MCChannelCommand(macro.Channel);

        // ── Top action row: Send + channel + badges ───────────────────────
        bool canSend = chunks.Count > 0;
        if (!canSend) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.30f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.45f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.60f }));
        if (ImGui.Button($"📣 Send to {cmd}", new Vector2(150, 0)))
        {
            for (int i = 0; i < chunks.Count && i < MaxMessages; i++)
                ChatSender.EnqueuePaced($"{cmd} {chunks[i]}");
        }
        ImGui.PopStyleColor(3);
        if (!canSend) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(chunks.Count > 1
                ? $"Sends {Math.Min(chunks.Count, MaxMessages)} messages, paced 2s apart (flood-safe)"
                : "Sends this macro to chat");

        ImGui.SameLine();
        int ci = Array.IndexOf(ChanValues, macro.Channel);
        if (ci < 0) ci = 0;
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo("##macroChan", ref ci, ChanComboItems))
        {
            macro.Channel = ChanValues[ci];
            _dirty = true;
        }

        ImGui.SameLine();
        if (truncated)
            ImGui.TextColored(Theme.Danger, $"⚠ {chunks.Count} msgs — only first {MaxMessages} sent");
        else
            ImGui.TextColored(Theme.Muted, $"{chunks.Count} msg{(chunks.Count == 1 ? "" : "s")}");

        ImGui.Spacing();

        // ── Name ──────────────────────────────────────────────────────────
        var name = macro.Name;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Name##macroName", ref name, 48))
        {
            macro.Name = name;
            _dirty = true;
        }

        // ── Macro text ────────────────────────────────────────────────────
        var bodyText = macro.Text;
        if (ImGui.InputTextMultiline("##macroText", ref bodyText, 4096,
                new Vector2(-1, 130)))
        {
            macro.Text = bodyText;
            _dirty = true;
        }

        // ── Symbol insert row(s) ──────────────────────────────────────────
        // Clicking a button defocuses the text box, so appending to the string
        // here is safe — the box re-reads it once it's no longer active.
        // Buttons wrap to the panel width so narrow windows never clip them.
        ImGui.TextColored(Theme.Muted with { W = 0.7f }, "Insert:");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Appends to the end of the text.\nThese render in FFXIV chat — OS emoji (🎲💀 etc.) do not,\nthe game's font simply doesn't have them.");

        float panelRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var   style      = ImGui.GetStyle();
        for (int i = 0; i < Symbols.Length; i++)
        {
            if (i > 0)
            {
                float nextW = ImGui.CalcTextSize(Symbols[i]).X + style.FramePadding.X * 2f;
                if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextW < panelRight)
                    ImGui.SameLine(0, 2);
            }
            if (ImGui.SmallButton($"{Symbols[i]}##sym{i}"))
            {
                macro.Text += Symbols[i];
                _dirty = true;
            }
        }

        // ── Limit + counts row ────────────────────────────────────────────
        var limit = config.MacroCharLimit;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Limit/msg##macroLim", ref limit))
        {
            config.MacroCharLimit = Math.Clamp(limit, 50, 200);
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted with { W = 0.7f }, "(150 safe, ~175 max)");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, $"{macro.Text.Length} chars");

        ImGui.Spacing();

        // ── Save / Copy / Delete row ──────────────────────────────────────
        if (!_dirty) ImGui.BeginDisabled();
        if (ImGui.Button(_dirty ? "💾 Save*" : "💾 Save", new Vector2(90, 0)))
        {
            config.Save();
            _dirty = false;
        }
        if (!_dirty) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Persist all macro changes to disk");

        ImGui.SameLine();
        if (!canSend) ImGui.BeginDisabled();
        if (ImGui.Button("📋 Copy macro", new Vector2(110, 0)))
            ImGui.SetClipboardText(BuildMacroExport(chunks, cmd));
        if (!canSend) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy in FFXIV User Macro format —\nchannel-prefixed lines with /wait 2 between messages");

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.ToU32(Theme.Danger with { W = 0.18f }));
        if (ImGui.Button("🗑##macroDel", new Vector2(34, 0)))
            ImGui.OpenPopup("##confirmDelMacro");
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Delete \"{macro.Name}\"");

        if (ImGui.BeginPopup("##confirmDelMacro"))
        {
            ImGui.TextColored(Theme.Warning, $"Delete \"{macro.Name}\"?");
            if (ImGui.Button("Yes, delete", new Vector2(100, 0)))
            {
                config.ChatMacros.Remove(macro);
                config.Save();
                _dirty = false;
                SelectMacro(Math.Min(_selIdx, config.ChatMacros.Count - 1));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep it", new Vector2(100, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // ── Split preview (how it will land in chat) ──────────────────────
        if (chunks.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, "Preview");
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.ToU32(Theme.CardBg with { W = 0.6f }));
            if (ImGui.BeginChild("##macroPreview", ImGui.GetContentRegionAvail(), true))
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    bool cut = i >= MaxMessages;
                    ImGui.TextColored(cut ? Theme.Danger with { W = 0.5f } : Theme.Muted, $"{i + 1}.");
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text,
                        Theme.ToU32(cut ? Theme.Muted with { W = 0.4f } : Theme.White));
                    ImGui.TextWrapped($"{cmd} {chunks[i]}");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Muted with { W = 0.6f }, $"({chunks[i].Length}ch)");
                }
                ImGui.EndChild();
            }
            ImGui.PopStyleColor();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SelectMacro(int idx) => _selIdx = idx;

    private string NextMacroName()
    {
        int n = config.ChatMacros.Count + 1;
        while (config.ChatMacros.Exists(m => m.Name == $"Macro {n}")) n++;
        return $"Macro {n}";
    }

    /// <summary>FFXIV User Macro export — identical shape to Shoutmaker's copy output.</summary>
    private static string BuildMacroExport(List<string> chunks, string cmd)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count && i < MaxMessages; i++)
        {
            sb.Append(cmd).Append(' ').Append(chunks[i]).Append('\n');
            if (i < chunks.Count - 1 && i < MaxMessages - 1) sb.Append("/wait 2\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

}
