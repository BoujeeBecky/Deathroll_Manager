using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using DeathrollManager.Models;

namespace DeathrollManager;

public enum MCChannel { Say, Yell, Shout, Party, FreeCompany }

[Serializable]
public class AnnouncementMacro
{
    public string Name     { get; set; } = "Custom Macro";
    public string Template { get; set; } = "{p1} vs {p2}";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Chat channels to monitor for roll messages
    public bool MonitorSayChat       { get; set; } = true;
    public bool MonitorPartyChat     { get; set; } = true;
    public bool MonitorLinkshellChat { get; set; } = true;
    public bool MonitorFCChat        { get; set; } = true;
    public bool MonitorYellChat      { get; set; } = false;

    // Gameplay defaults
    public int  DefaultStartingNumber { get; set; } = 1000;
    public bool AutoDetectGames       { get; set; } = true;
    public bool ShowBetInWindow       { get; set; } = true;

    // UI tweaks
    public bool ShowRollTimestamps  { get; set; } = false;
    public bool FlashOnGameOver     { get; set; } = true;
    public bool PopOutBattlePanel   { get; set; } = false;

    // Tournament defaults
    public BracketLayout DefaultBracketLayout    { get; set; } = BracketLayout.VBracket;
    public BracketFormat DefaultBracketFormat    { get; set; } = BracketFormat.SingleElim;
    public MCChannel     MCAnnouncementChannel   { get; set; } = MCChannel.Say;

    // Announcement macros (custom /say templates for tournament MCs)
    public List<AnnouncementMacro> AnnouncementMacros { get; set; } = new();

    public static string MCChannelCommand(MCChannel ch) => ch switch
    {
        MCChannel.Yell        => "/yell",
        MCChannel.Shout       => "/shout",
        MCChannel.Party       => "/p",
        MCChannel.FreeCompany => "/fc",
        _                     => "/say",
    };

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
