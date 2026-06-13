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

/// <summary>Free-text chat macro (Macros tab) — plain text, no placeholders.
/// Distinct from AnnouncementMacro, which is a tournament MC template.</summary>
[Serializable]
public class ChatMacro
{
    public string    Name    { get; set; } = "Macro";
    public string    Text    { get; set; } = string.Empty;
    public MCChannel Channel { get; set; } = MCChannel.Say;
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

    // Free-text chat macros (Macros tab)
    public List<ChatMacro> ChatMacros     { get; set; } = new();
    public int             MacroCharLimit { get; set; } = 150; // per-message split limit (150 safe, ~175 max)

    // Tournament hosting QOL
    public bool AutoCallUp           { get; set; } = false; // announce next pairing when a match completes
    public int  RollTimerSeconds     { get; set; } = 0;     // per-roll countdown, 0 = off
    public bool RelayBroadcastToChat { get; set; } = true;  // 📡/say checkbox — relay /say broadcast on/off

    // Theme & flair
    public string ThemeName  { get; set; } = "Classic";
    public bool   SoundCues  { get; set; } = false;      // game sfx on death roll / champion

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
