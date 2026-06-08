using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeathrollManager.Models;
using System.Collections.Generic;

namespace DeathrollManager.Windows;

public class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;

    private string _newMacroName     = "My Macro";
    private string _newMacroTemplate = "{p1} vs {p2}";

    public SettingsWindow(Plugin plugin) : base("Deathroll Manager — Settings###DRSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(380, 300),
            MaximumSize = new(600, 600),
        };
    }

    public override void Draw()
    {
        ImGui.TextColored(Theme.Gold, "Chat Channels to Monitor");
        ImGui.Separator();
        ImGui.Spacing();

        var say  = Config.MonitorSayChat;
        var party = Config.MonitorPartyChat;
        var ls    = Config.MonitorLinkshellChat;
        var fc    = Config.MonitorFCChat;
        var yell  = Config.MonitorYellChat;

        if (ImGui.Checkbox("Say",        ref say))  { Config.MonitorSayChat       = say;  Config.Save(); }
        if (ImGui.Checkbox("Party",      ref party)) { Config.MonitorPartyChat     = party; Config.Save(); }
        if (ImGui.Checkbox("Linkshell",  ref ls))    { Config.MonitorLinkshellChat = ls;   Config.Save(); }
        if (ImGui.Checkbox("Free Company", ref fc))  { Config.MonitorFCChat        = fc;   Config.Save(); }
        if (ImGui.Checkbox("Yell",       ref yell))  { Config.MonitorYellChat      = yell; Config.Save(); }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, "Gameplay");
        ImGui.Separator();
        ImGui.Spacing();

        var autoDetect = Config.AutoDetectGames;
        if (ImGui.Checkbox("Auto-detect games from chat", ref autoDetect))
        {
            Config.AutoDetectGames = autoDetect;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, matching /random rolls in chat are automatically\nadded to the active game.");

        var showBet = Config.ShowBetInWindow;
        if (ImGui.Checkbox("Show bet amount in main window", ref showBet))
        {
            Config.ShowBetInWindow = showBet;
            Config.Save();
        }

        var timestamps = Config.ShowRollTimestamps;
        if (ImGui.Checkbox("Show timestamps in roll history", ref timestamps))
        {
            Config.ShowRollTimestamps = timestamps;
            Config.Save();
        }

        var flash = Config.FlashOnGameOver;
        if (ImGui.Checkbox("Flash on game over", ref flash))
        {
            Config.FlashOnGameOver = flash;
            Config.Save();
        }

        var popOut = Config.PopOutBattlePanel;
        if (ImGui.Checkbox("Pop out Battle view as a separate window", ref popOut))
        {
            Config.PopOutBattlePanel = popOut;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the Battle scene opens as a floating window\nwhenever a game starts, so you can pin it anywhere on screen.");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, "Defaults");
        ImGui.Separator();
        ImGui.Spacing();

        var startNum = Config.DefaultStartingNumber;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Default starting number", ref startNum))
        {
            if (startNum < 2)   startNum = 2;
            if (startNum > 1_000_000) startNum = 1_000_000;
            Config.DefaultStartingNumber = startNum;
            Config.Save();
        }

        var layoutIdx = Config.DefaultBracketLayout == BracketLayout.LeftToRight ? 1 : 0;
        ImGui.SetNextItemWidth(210);
        if (ImGui.Combo("Default bracket layout", ref layoutIdx, "V-Bracket (Centre Finals)\0Left to Right\0"))
        {
            Config.DefaultBracketLayout = layoutIdx == 1 ? BracketLayout.LeftToRight : BracketLayout.VBracket;
            Config.Save();
        }

        var chanIdx = (int)Config.MCAnnouncementChannel;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("MC announcement channel", ref chanIdx, "Say\0Yell\0Shout\0Party\0Free Company\0"))
        {
            Config.MCAnnouncementChannel = (MCChannel)chanIdx;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Channel used by the MC Controls buttons in the Tournament window.\nRelay protocol always uses /say — this only affects announcements.");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, "Announcement Macros");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Placeholders: {p1}  {p2}  {start}  {first}  {winner}  {round}  {match}  {venue}");
        ImGui.Spacing();

        var macros = Config.AnnouncementMacros;
        for (int i = 0; i < macros.Count; i++)
        {
            var m    = macros[i];
            var name = m.Name;
            var tmpl = m.Template;

            ImGui.SetNextItemWidth(130);
            if (ImGui.InputText($"##mn{i}", ref name, 64))
            {
                m.Name = name;
                Config.Save();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(280);
            if (ImGui.InputText($"##mt{i}", ref tmpl, 256))
            {
                m.Template = tmpl;
                Config.Save();
            }
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.Danger with { W = 0.18f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.Danger with { W = 0.32f }));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.LosRed  with { W = 0.50f }));
            if (ImGui.SmallButton($"✕##del{i}"))
            {
                macros.RemoveAt(i);
                Config.Save();
                ImGui.PopStyleColor(3);
                break;
            }
            ImGui.PopStyleColor(3);
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(130);
        ImGui.InputText("##newmn", ref _newMacroName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("##newmt", ref _newMacroTemplate, 256);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ToU32(Theme.WinGreen with { W = 0.20f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ToU32(Theme.WinGreen with { W = 0.35f }));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ToU32(Theme.WinGreen with { W = 0.50f }));
        if (ImGui.SmallButton("+ Add"))
        {
            macros.Add(new AnnouncementMacro { Name = _newMacroName, Template = _newMacroTemplate });
            Config.Save();
            _newMacroName     = "My Macro";
            _newMacroTemplate = "{p1} vs {p2}";
        }
        ImGui.PopStyleColor(3);
    }
}
