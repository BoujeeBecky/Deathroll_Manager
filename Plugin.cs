using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeathrollManager.Services;
using DeathrollManager.Windows;

namespace DeathrollManager;

public sealed class Plugin : IDalamudPlugin
{
    // Static references used throughout the plugin
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog  Log       { get; private set; } = null!;
    [PluginService] internal static IFramework Framework  { get; private set; } = null!;

    public readonly Configuration           Configuration;
    public readonly GameStateService        GameState;
    public readonly ChatMonitorService      ChatMonitor;
    public readonly TournamentService       TournamentService;
    public readonly TournamentRelayService  RelayService;
    public readonly BattleRenderer          BattleRenderer;

    private readonly WindowSystem     windowSystem = new("DeathrollManager");
    private readonly MainWindow       mainWindow;
    private readonly SettingsWindow   settingsWindow;
    public  readonly TournamentWindow TournamentWindow;
    public  readonly BattleWindow     BattleWindow;

    public Plugin()
    {
        Configuration     = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Windows.Theme.Apply(Configuration.ThemeName);
        GameState         = new GameStateService(Configuration, Log);
        ChatMonitor       = new ChatMonitorService(ChatGui, GameState, Configuration, Log);
        TournamentService = new TournamentService(GameState, Log);
        RelayService      = new TournamentRelayService(ChatGui, Log, Framework)
        {
            BroadcastToChat = Configuration.RelayBroadcastToChat,
        };
        BattleRenderer    = new BattleRenderer(GameState);

        // Keep relay in sync with tournament state: send match results as they happen,
        // and stop the relay automatically if the tournament is cancelled.
        TournamentService.TournamentStateChanged += () =>
        {
            var t = TournamentService.ActiveTournament;
            RelayService.OnTournamentStateChanged(t);
            if (t == null) RelayService.StopHostRelay();

            // Champion fanfare (once per tournament)
            if (t is { IsComplete: true } && _champSoundFor != t.Id)
            {
                _champSoundFor = t.Id;
                if (Configuration.SoundCues) Helpers.SoundCue.Play(Helpers.SoundCue.Champion);
            }
        };

        GameState.GameCompleted += OnGameCompletedSound;

        mainWindow       = new MainWindow(this);
        settingsWindow   = new SettingsWindow(this);
        TournamentWindow = new TournamentWindow(this);
        BattleWindow     = new BattleWindow(BattleRenderer);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(TournamentWindow);
        windowSystem.AddWindow(BattleWindow);

        // Auto-open the pop-out battle window when a new game starts (if setting enabled)
        GameState.StateChanged += () =>
        {
            if (Configuration.PopOutBattlePanel && GameState.ActiveGame?.Rolls.Count == 0)
                BattleWindow.IsOpen = true;
        };

        CommandManager.AddHandler("/dr", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Deathroll Manager  |  /dr tournament  |  /dr settings"
        });
        CommandManager.AddHandler("/deathroll", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Deathroll Manager"
        });

        PluginInterface.UiBuilder.Draw        += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenMain;

        Framework.Update += OnFrameworkUpdate;

        ChatMonitor.UnmatchedRollDetected += mainWindow.OnUnmatchedRoll;
        ChatMonitor.UnmatchedRollDetected += OnUnmatchedRollForTournament;
        TournamentService.BracketRepaired += OnBracketRepaired;
    }

    // Incremental MATCH dedup can't express cleared/renamed results — full resync.
    private void OnBracketRepaired(Models.Tournament t)
    {
        // A repair that un-completes the tournament re-arms the champion fanfare,
        // so a corrected grand-finals result still gets its celebration.
        if (!t.IsComplete) _champSoundFor = null;
        RelayService.ResyncBroadcast(t);
    }

    private System.Guid? _champSoundFor;

    private void OnGameCompletedSound(Models.DeathrollGame game)
    {
        if (Configuration.SoundCues) Helpers.SoundCue.Play(Helpers.SoundCue.Death);
    }

    // Drains the paced chat queue (multi-message macros) on the game thread.
    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _) =>
        Helpers.ChatSender.PumpQueue();

    private void OnUnmatchedRollForTournament(string player, int rolled, int outOf)
    {
        // No window-open gate: an armed roll-off must keep consuming /random 10s
        // even if the host closes the bracket window mid-roll-off.
        TournamentService.HandleRollOffCandidate(player, rolled, outOf);
    }

    public void Dispose()
    {
        ChatMonitor.UnmatchedRollDetected -= mainWindow.OnUnmatchedRoll;
        ChatMonitor.UnmatchedRollDetected -= OnUnmatchedRollForTournament;
        TournamentService.BracketRepaired -= OnBracketRepaired;
        GameState.GameCompleted           -= OnGameCompletedSound;

        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw        -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenMain;

        CommandManager.RemoveHandler("/dr");
        CommandManager.RemoveHandler("/deathroll");

        windowSystem.RemoveAllWindows();
        ChatMonitor.Dispose();
        RelayService.Dispose();
        BattleRenderer.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "settings" or "config":
                settingsWindow.IsOpen = true;
                break;
            case "tournament" or "bracket":
                TournamentWindow.IsOpen = true;
                break;
            default:
                mainWindow.IsOpen = true;
                break;
        }
    }

    /// <summary>Opens the settings window (title-bar gear, /dr settings, config button).</summary>
    public void OpenSettings() => settingsWindow.IsOpen = true;

    private void OnOpenConfig()  => settingsWindow.IsOpen   = true;
    private void OnOpenMain()    => mainWindow.IsOpen        = true;
}
