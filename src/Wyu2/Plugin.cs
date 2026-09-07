using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Net;
using Wyu2.Tracking;
using Wyu2.Ui;

namespace Wyu2;

/// <summary>Entry point: wires the trackers, the relay and the windows together.</summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/wyu2";
    private const string ShortCommand = "/wyu";

    private readonly Configuration.Configuration config;
    private readonly GameDataCache data;
    private readonly RelayClient client;
    private readonly RelaySession session;
    private readonly PresenceHub hub;
    private readonly NearbyScanner scanner;
    private readonly WindowSystem windows = new("Wyu2");
    private readonly MainWindow mainWindow;
    private readonly RadarWindow radarWindow;
    private readonly ZoneMapWindow mapWindow;
    private readonly ConfigWindow configWindow;
    private readonly WorldOverlay overlay;
    private readonly AlertService alerts;
    private readonly AetheryteFinder aetherytes;
    private readonly TeleporterIpc teleporter;
    private readonly IDtrBarEntry dtrEntry;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        config = pluginInterface.GetPluginConfig() as Configuration.Configuration ?? new Configuration.Configuration();
        if (config.Migrate())
            config.Save();

        data = new GameDataCache();
        client = new RelayClient(config, Version);
        session = new RelaySession(config, client);
        scanner = new NearbyScanner(config);

        alerts = new AlertService(config, data);
        aetherytes = new AetheryteFinder();
        teleporter = new TeleporterIpc();
        var activity = new ActivityResolver(data);
        var snapshots = new SelfSnapshotBuilder(config, data, activity);
        hub = new PresenceHub(config, client, session, snapshots, scanner, data);

        mapWindow = new ZoneMapWindow(config, hub, data);
        radarWindow = new RadarWindow(config, hub, scanner);
        configWindow = new ConfigWindow(config, hub, data);
        mainWindow = new MainWindow(
            config, hub, session, client, mapWindow, aetherytes, teleporter, () => configWindow.IsOpen = true);
        overlay = new WorldOverlay(config, hub);

        windows.AddWindow(mainWindow);
        windows.AddWindow(radarWindow);
        windows.AddWindow(mapWindow);
        windows.AddWindow(configWindow);

        radarWindow.IsOpen = config.Radar.Enabled && config.ShowMainWindowOnStart;
        mainWindow.IsOpen = config.ShowMainWindowOnStart;

        dtrEntry = Service.DtrBar.Get("Wyu2", "Wyu2");
        dtrEntry.OnClick = _ => mainWindow.Toggle();
        dtrEntry.Shown = config.ShowDtrEntry;

        Service.Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the friend list. Subcommands: radar, map, config, share on|off, pause [minutes], note <text>.",
        });

        Service.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /wyu2.",
            ShowInHelp = false,
        });

        Service.PluginInterface.UiBuilder.Draw += DrawUi;
        Service.PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Service.Framework.Update += OnFrameworkUpdate;
        Service.ClientState.Login += OnLogin;

        if (session.HasAccount)
            hub.SyncContactsNow();
    }

    /// <summary>Plugin version, sent to the relay as a diagnostic header.</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public void Dispose()
    {
        Service.Framework.Update -= OnFrameworkUpdate;
        Service.ClientState.Login -= OnLogin;
        Service.PluginInterface.UiBuilder.Draw -= DrawUi;
        Service.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        Service.Commands.RemoveHandler(MainCommand);
        Service.Commands.RemoveHandler(ShortCommand);

        dtrEntry.Remove();
        windows.RemoveAllWindows();

        hub.Dispose();
        session.Dispose();
        client.Dispose();
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            hub.Update();
            alerts.Evaluate(hub.Friends);
            UpdateDtr();
            HandleRadarFocus();
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "Wyu2 update failed");
        }
    }

    private void HandleRadarFocus()
    {
        var request = radarWindow.ConsumeFocusRequest();
        if (request is null)
            return;

        var friend = hub.Friends.FirstOrDefault(f => f.AccountId == request);
        if (friend is not null)
            mapWindow.FocusOn(friend);
    }

    private void UpdateDtr()
    {
        dtrEntry.Shown = config.ShowDtrEntry;
        if (!config.ShowDtrEntry)
            return;

        var online = hub.Friends.Count(f => config.CanSee(f.Settings) && f.IsOnline);
        dtrEntry.Text = $"Wyu2 {online}";
        dtrEntry.Tooltip = config.SharingEnabled
            ? $"{online} contact(s) sharing with you. {SelfSnapshotBuilder.Explain(hub.BlockReason)}."
            : $"{online} contact(s) sharing with you. You are not sharing.";
    }

    private void DrawUi()
    {
        windows.Draw();
        overlay.Draw();
    }

    private void OpenMainUi() => mainWindow.Toggle();

    private void OpenConfigUi() => configWindow.Toggle();

    private void OnLogin()
    {
        if (config.ShowMainWindowOnStart)
            mainWindow.IsOpen = true;

        if (session.HasAccount)
            hub.SyncContactsNow();
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (verb)
        {
            case "":
                mainWindow.Toggle();
                break;

            case "radar":
                radarWindow.Toggle();
                break;

            case "map":
                mapWindow.Toggle();
                break;

            case "config" or "settings":
                configWindow.Toggle();
                break;

            case "share":
                SetSharing(rest);
                break;

            case "pause":
                Pause(rest);
                break;

            case "note":
                config.StatusNote = rest;
                config.Save();
                hub.PublishNow();
                Service.Chat.Print(string.IsNullOrEmpty(rest) ? "Status note cleared." : $"Status note set to \"{rest}\".", "Wyu2");
                break;

            default:
                Service.Chat.PrintError(
                    $"Unknown subcommand \"{verb}\". Try: radar, map, config, share on|off, pause [minutes], note <text>.",
                    "Wyu2");
                break;
        }
    }

    private void SetSharing(string argument)
    {
        var enable = argument switch
        {
            "on" or "start" or "yes" => true,
            "off" or "stop" or "no" => false,
            _ => !config.SharingEnabled,
        };

        config.SharingEnabled = enable;
        config.PausedUntilUnixMs = 0;
        config.Save();

        if (enable)
            hub.PublishNow();
        else
            hub.PanicStop();

        Service.Chat.Print(enable ? "Sharing your presence." : "Stopped sharing.", "Wyu2");
    }

    private void Pause(string argument)
    {
        if (!int.TryParse(argument, out var minutes) || minutes <= 0)
            minutes = 15;

        config.PausedUntilUnixMs = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds();
        config.Save();
        Service.Chat.Print($"Sharing paused for {minutes} minute(s).", "Wyu2");
    }
}
