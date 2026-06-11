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
        GameState         = new GameStateService(Configuration, Log);
        ChatMonitor       = new ChatMonitorService(ChatGui, GameState, Configuration, Log);
        TournamentService = new TournamentService(GameState, Log);
        RelayService      = new TournamentRelayService(ChatGui, Log, Framework);
        BattleRenderer    = new BattleRenderer(GameState);

        // Keep relay in sync with tournament state: send match results as they happen,
        // and stop the relay automatically if the tournament is cancelled.
        TournamentService.TournamentStateChanged += () =>
        {
            var t = TournamentService.ActiveTournament;
            RelayService.OnTournamentStateChanged(t);
            if (t == null) RelayService.StopHostRelay();
        };

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

        ChatMonitor.UnmatchedRollDetected += mainWindow.OnUnmatchedRoll;
        ChatMonitor.UnmatchedRollDetected += OnUnmatchedRollForTournament;
    }

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

    private void OnOpenConfig()  => settingsWindow.IsOpen   = true;
    private void OnOpenMain()    => mainWindow.IsOpen        = true;
}
