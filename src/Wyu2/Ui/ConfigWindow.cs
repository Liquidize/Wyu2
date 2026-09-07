using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Wyu2.Configuration;
using Wyu2.Tracking;

namespace Wyu2.Ui;

/// <summary>Settings: what you share, when sharing stops on its own, and how the radar looks.</summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration.Configuration config;
    private readonly PresenceHub hub;
    private readonly Game.GameDataCache data;

    public ConfigWindow(Configuration.Configuration config, PresenceHub hub, Game.GameDataCache data)
        : base("Wyu2 settings##wyu2-config")
    {
        this.config = config;
        this.hub = hub;
        this.data = data;

        Size = new Vector2(560f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 320f),
            MaximumSize = new Vector2(1600f, 1600f),
        };
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##config-tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("What I share"))
        {
            if (tab.Success)
                DrawSharing();
        }

        using (var tab = ImRaii.TabItem("When I stop"))
        {
            if (tab.Success)
                DrawPrivacy();
        }

        using (var tab = ImRaii.TabItem("Radar"))
        {
            if (tab.Success)
                DrawRadar();
        }

        using (var tab = ImRaii.TabItem("Areas"))
        {
            if (tab.Success)
                DrawGeofences();
        }

        using (var tab = ImRaii.TabItem("Other"))
        {
            if (tab.Success)
                DrawOther();
        }
    }

    // ---------------------------------------------------------------- sharing

    private void DrawSharing()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "This is the default profile. Every contact gets exactly these fields unless you gave them a " +
            "narrower one on the Contacts tab. Anything switched off is never put into a payload at all.");

        ImGui.Spacing();
        if (ImGui.Button("Everything"))
            ReplaceProfile(SharingProfile.Everything());

        ImGui.SameLine();
        if (ImGui.Button("Zone only"))
            ReplaceProfile(SharingProfile.ZoneOnly());

        ImGui.SameLine();
        if (ImGui.Button("Online only"))
            ReplaceProfile(SharingProfile.Minimal());

        ImGui.Separator();
        var profile = config.DefaultProfile;

        Toggle("Character name", () => profile.ShareCharacterName, v => profile.ShareCharacterName = v,
            "Off means contacts see the display name you registered with instead of your character name. " +
            "Note that turning this off also stops the plugin matching you to a character standing nearby.");

        Toggle("World", () => profile.ShareWorld, v => profile.ShareWorld = v,
            "Home world and the world you are currently on.");

        Toggle("Zone and instance", () => profile.ShareZone, v => profile.ShareZone = v,
            "Which zone you are in, and which public instance of it. Required for coordinates to mean anything.");

        using (ImRaii.Disabled(!profile.ShareZone))
        {
            Toggle("Exact position", () => profile.SharePosition, v => profile.SharePosition = v,
                "Your coordinates, so contacts can put you on the radar and the map.");
        }

        Toggle("Activity", () => profile.ShareActivity, v => profile.ShareActivity = v,
            "What you are doing: the duty name, FATE, crafting, gathering, cutscenes and so on.");

        using (ImRaii.Disabled(!profile.ShareActivity))
        {
            Toggle("Name of what I am fighting", () => profile.ShareCombatTarget, v => profile.ShareCombatTarget = v,
                "Adds the enemy name to your activity line while you are in combat.");
        }

        Toggle("Job and level", () => profile.ShareJob, v => profile.ShareJob = v);
        Toggle("Online status", () => profile.ShareOnlineStatus, v => profile.ShareOnlineStatus = v,
            "The game's own status: away, busy, role playing, looking for party.");
        Toggle("Party size", () => profile.ShareParty, v => profile.ShareParty = v);
        Toggle("Free company tag", () => profile.ShareFreeCompany, v => profile.ShareFreeCompany = v);
        Toggle("Status note", () => profile.ShareNote, v => profile.ShareNote = v);

        ImGui.Separator();
        ImGui.TextUnformatted("Status note");
        ImGui.SetNextItemWidth(-1f);
        if (UiHelpers.InputText("##note", () => config.StatusNote, v => config.StatusNote = v, 120,
                "e.g. running roulettes, ping me for raids"))
        {
            config.Save();
        }

        ImGui.Separator();
        if (UiHelpers.SliderInt("Publish every", () => config.PublishIntervalSeconds,
                v => config.PublishIntervalSeconds = v, 3, 120, "%d seconds"))
        {
            config.Save();
        }

        if (UiHelpers.SliderInt("Fetch every", () => config.FetchIntervalSeconds,
                v => config.FetchIntervalSeconds = v, 3, 120, "%d seconds"))
        {
            config.Save();
        }
    }

    private void ReplaceProfile(SharingProfile profile)
    {
        config.DefaultProfile = profile;
        config.Save();
        hub.PublishNow();
    }

    private void Toggle(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        if (!UiHelpers.Checkbox(label, get, set, tooltip))
            return;

        config.Save();
        hub.PublishNow();
    }

    // ---------------------------------------------------------------- privacy

    private void DrawPrivacy()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Situations where the plugin stops publishing on its own, whatever else is enabled.");
        ImGui.Spacing();

        var rules = config.Privacy;

        Toggle("Stop in PvP", () => rules.PauseInPvp, v => rules.PauseInPvp = v,
            "Nothing is published while you are in a PvP instance.");
        Toggle("Stop in housing", () => rules.PauseInHousing, v => rules.PauseInHousing = v);
        Toggle("Stop inside duties", () => rules.PauseInDuty, v => rules.PauseInDuty = v,
            "Some people would rather not broadcast their raid progress.");
        Toggle("Stop while my status is Busy", () => rules.PauseWhenBusy, v => rules.PauseWhenBusy = v);

        ImGui.Separator();
        Toggle("Hide coordinates inside duties", () => rules.HidePositionInDuty, v => rules.HidePositionInDuty = v,
            "Contacts still see which duty you are in, but not where you are standing in it.");
        Toggle("Hide coordinates in housing", () => rules.HidePositionInHousing, v => rules.HidePositionInHousing = v);

        ImGui.Separator();
        ImGui.TextUnformatted("Never share these zones");
        UiHelpers.HelpMarker("Nothing at all is published while you are in a blocked zone.");

        if (Service.ClientState.IsLoggedIn)
        {
            var here = (ushort)Service.ClientState.TerritoryType;
            var blocked = rules.BlockedTerritories.Contains(here);
            var name = data.GetZoneName(here);

            if (ImGui.Button(blocked ? $"Unblock {name}" : $"Block {name}"))
            {
                if (blocked)
                    rules.BlockedTerritories.Remove(here);
                else
                    rules.BlockedTerritories.Add(here);

                config.Save();
            }
        }

        foreach (var territory in rules.BlockedTerritories.ToList())
        {
            using var id = ImRaii.PushId(territory);
            var name = data.GetZoneName(territory);
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(name) ? $"Zone {territory}" : name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                rules.BlockedTerritories.Remove(territory);
                config.Save();
            }
        }
    }

    // ---------------------------------------------------------------- radar

    private void DrawRadar()
    {
        ImGui.Spacing();
        var radar = config.Radar;

        if (UiHelpers.SliderFloat("Range", () => radar.RangeYalms, v => radar.RangeYalms = v, 20f, 500f, "%.0f yalms"))
            config.Save();

        if (UiHelpers.SliderFloat("Blip size", () => radar.BlipSize, v => radar.BlipSize = v, 2f, 16f, "%.0f"))
            config.Save();

        if (UiHelpers.SliderFloat("Opacity", () => radar.Opacity, v => radar.Opacity = v, 0.1f, 1f, "%.2f"))
            config.Save();

        if (UiHelpers.Checkbox("Rotate with the camera", () => radar.RotateWithCamera, v => radar.RotateWithCamera = v))
            config.Save();
        if (UiHelpers.Checkbox("Distance rings", () => radar.ShowDistanceRings, v => radar.ShowDistanceRings = v))
            config.Save();
        if (UiHelpers.Checkbox("Names", () => radar.ShowNames, v => radar.ShowNames = v))
            config.Save();
        if (UiHelpers.Checkbox("Job next to the name", () => radar.ShowJobIcons, v => radar.ShowJobIcons = v))
            config.Save();
        if (UiHelpers.Checkbox("Arrows for friends out of range", () => radar.ShowOffscreenArrows, v => radar.ShowOffscreenArrows = v))
            config.Save();
        if (UiHelpers.Checkbox("Show myself", () => radar.ShowSelf, v => radar.ShowSelf = v))
            config.Save();

        ImGui.Separator();
        if (UiHelpers.Checkbox("Animate the radar", () => radar.Animate, v => radar.Animate = v,
                "The rotating sweep and the ping each blip gives as it passes. Turn this off for a static " +
                "radar that only moves when people do."))
        {
            config.Save();
        }

        using (ImRaii.Disabled(!radar.Animate))
        {
            if (UiHelpers.Checkbox("Rotating sweep", () => radar.ShowSweep, v => radar.ShowSweep = v))
                config.Save();

            if (UiHelpers.Checkbox("Flash blips as the sweep passes", () => radar.PingOnSweep,
                    v => radar.PingOnSweep = v))
            {
                config.Save();
            }

            if (UiHelpers.SliderFloat("Sweep speed", () => radar.SweepSeconds, v => radar.SweepSeconds = v,
                    1f, 12f, "%.1f seconds per turn"))
            {
                config.Save();
            }
        }

        if (UiHelpers.Checkbox("Movement trails", () => radar.ShowTrails, v => radar.ShowTrails = v,
                "A fading breadcrumb behind each contact on the radar and the map, so you can see which " +
                "way they are heading. Built from updates you already have; nothing extra is sent or stored."))
        {
            config.Save();
        }

        using (ImRaii.Disabled(!radar.ShowTrails))
        {
            if (UiHelpers.SliderFloat("Trail length", () => radar.TrailSeconds, v => radar.TrailSeconds = v,
                    5f, 120f, "%.0f seconds"))
            {
                config.Save();
            }
        }

        if (UiHelpers.Checkbox("Predict where people are heading", () => radar.ShowPrediction,
                v => radar.ShowPrediction = v,
                "Draws a ghost ahead of anybody on the move, worked out from the trail. Useful for " +
                "cutting off a train that is already rolling, where aiming at somebody means arriving " +
                "where they were."))
        {
            config.Save();
        }

        using (ImRaii.Disabled(!radar.ShowPrediction))
        {
            if (UiHelpers.SliderFloat("Look ahead", () => radar.PredictSeconds, v => radar.PredictSeconds = v,
                    1f, 15f, "%.0f seconds"))
            {
                config.Save();
            }
        }

        if (UiHelpers.Checkbox("Pulse map markers", () => config.AnimateMapMarkers,
                v => config.AnimateMapMarkers = v,
                "Sends a sonar ring out of each marker on the friend map."))
        {
            config.Save();
        }

        ImGui.Separator();
        if (UiHelpers.ColorEdit("Background", () => radar.BackgroundColor, v => radar.BackgroundColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Grid", () => radar.GridColor, v => radar.GridColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Me", () => radar.SelfColor, v => radar.SelfColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Sweep", () => radar.SweepColor, v => radar.SweepColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Stale updates", () => radar.StaleColor, v => radar.StaleColor = v))
            config.Save();

        ImGui.Separator();
        if (UiHelpers.Checkbox("Draw friend names in the world", () => config.ShowWorldOverlay, v => config.ShowWorldOverlay = v,
                "Overlays a label above contacts you can actually see on screen."))
        {
            config.Save();
        }

        using (ImRaii.Disabled(!config.ShowWorldOverlay))
        {
            if (UiHelpers.Checkbox("Include their activity in the overlay", () => config.OverlayShowActivity,
                    v => config.OverlayShowActivity = v))
            {
                config.Save();
            }

            if (UiHelpers.SliderFloat("Overlay range", () => config.OverlayMaxDistance, v => config.OverlayMaxDistance = v,
                    10f, 500f, "%.0f yalms"))
            {
                config.Save();
            }
        }

        ImGui.Separator();
        DrawNativeMap();
    }

    // ------------------------------------------------------- the game's own map

    private void DrawNativeMap()
    {
        var native = config.NativeMap;

        ImGui.TextUnformatted("The game's own map");
        UiHelpers.HelpMarker(
            "Contacts are drawn straight onto the map and the minimap the game already gives you. " +
            "Only people in your zone, your public instance and on your world appear, since anybody " +
            "else's coordinates would point at somewhere you cannot walk to. Nothing is added to the " +
            "game's interface: the markers are painted over the top of it.");

        if (UiHelpers.Checkbox("Draw on the full map", () => native.OnAreaMap, v => native.OnAreaMap = v,
                "Only while the map is showing the zone you are standing in."))
        {
            config.Save();
        }

        if (UiHelpers.Checkbox("Draw on the minimap", () => native.OnMiniMap, v => native.OnMiniMap = v))
            config.Save();

        using (ImRaii.Disabled(!native.OnAreaMap && !native.OnMiniMap))
        {
            if (UiHelpers.Checkbox("Names on the full map", () => native.ShowNames, v => native.ShowNames = v,
                    "The minimap is left unlabelled either way; there is no room for it."))
            {
                config.Save();
            }

            if (UiHelpers.Checkbox("Beacons too", () => native.ShowBeacons, v => native.ShowBeacons = v))
                config.Save();

            using (ImRaii.Disabled(!native.OnMiniMap))
            {
                if (UiHelpers.Checkbox("Pin contacts to the minimap edge", () => native.ClampToMiniMapEdge,
                        v => native.ClampToMiniMapEdge = v,
                        "Somebody past the edge of the minimap is drawn on its rim, pointing the way, " +
                        "instead of vanishing."))
                {
                    config.Save();
                }
            }

            if (UiHelpers.SliderFloat("Marker size", () => native.MarkerSize, v => native.MarkerSize = v,
                    2f, 12f, "%.0f pixels"))
            {
                config.Save();
            }
        }
    }

    // ---------------------------------------------------------------- areas

    private void DrawGeofences()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Areas you want to hear about. An alert fires when somebody crosses in or out, not for as " +
            "long as they stand there, and only for contacts you can already see.");

        ImGui.Spacing();
        if (UiHelpers.Checkbox("Enable area alerts", () => config.GeofenceAlertsEnabled,
                v => config.GeofenceAlertsEnabled = v))
        {
            config.Save();
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!Service.ClientState.IsLoggedIn))
        {
            if (ImGui.Button("Watch this zone"))
            {
                var territory = (ushort)Service.ClientState.TerritoryType;
                config.Geofences.Add(new Configuration.GeofenceSettings
                {
                    Name = data.GetZoneName(territory) is { Length: > 0 } zone ? zone : $"Zone {territory}",
                    Scope = Game.GeofenceScope.Zone,
                    TerritoryTypeId = territory,
                });
                config.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("Watch where I am standing") &&
                Service.Objects.LocalPlayer is { } player)
            {
                var territory = (ushort)Service.ClientState.TerritoryType;
                config.Geofences.Add(new Configuration.GeofenceSettings
                {
                    Name = $"{data.GetZoneName(territory)} spot",
                    Scope = Game.GeofenceScope.Radius,
                    TerritoryTypeId = territory,
                    CentreX = player.Position.X,
                    CentreZ = player.Position.Z,
                });
                config.Save();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Watch near me"))
        {
            config.Geofences.Add(new Configuration.GeofenceSettings
            {
                Name = "Near me",
                Scope = Game.GeofenceScope.NearMe,
            });
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Follows you around, wherever you go.");

        ImGui.Separator();

        if (config.Geofences.Count == 0)
        {
            UiHelpers.TextMuted("No areas yet.");
            return;
        }

        foreach (var fence in config.Geofences.ToList())
        {
            using var id = ImRaii.PushId(fence.Id);

            ImGui.SetNextItemWidth(180f);
            var name = fence.Name;
            if (ImGui.InputText("##name", ref name, 60))
            {
                fence.Name = name;
                config.Save();
            }

            ImGui.SameLine();
            UiHelpers.TextMuted(fence.Scope switch
            {
                Game.GeofenceScope.Zone => "whole zone",
                Game.GeofenceScope.Radius => $"{fence.RadiusYalms:0}y circle",
                _ => $"within {fence.RadiusYalms:0}y of me",
            });

            if (fence.Scope != Game.GeofenceScope.Zone)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120f);
                var radius = fence.RadiusYalms;
                if (ImGui.SliderFloat("##radius", ref radius, 5f, 300f, "%.0f y"))
                {
                    fence.RadiusYalms = radius;
                    config.Save();
                }
            }

            ImGui.SameLine();
            if (UiHelpers.Checkbox("in", () => fence.OnEnter, v => fence.OnEnter = v))
                config.Save();

            ImGui.SameLine();
            if (UiHelpers.Checkbox("out", () => fence.OnExit, v => fence.OnExit = v))
                config.Save();

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                config.Geofences.RemoveAll(g => g.Id == fence.Id);
                config.Save();
            }
        }
    }

    // ---------------------------------------------------------------- other

    private void DrawOther()
    {
        ImGui.Spacing();

        if (UiHelpers.Checkbox("Server info bar entry", () => config.ShowDtrEntry, v => config.ShowDtrEntry = v,
                "Shows how many contacts are online next to the clock."))
        {
            config.Save();
        }

        if (UiHelpers.Checkbox("Open the friend list on login", () => config.ShowMainWindowOnStart,
                v => config.ShowMainWindowOnStart = v))
        {
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Beacons");

        if (UiHelpers.Checkbox("Draw beacons on the radar and map", () => config.ShowBeacons,
                v => config.ShowBeacons = v,
                "Marked spots your contacts dropped, and the ones you dropped yourself."))
        {
            config.Save();
        }

        if (UiHelpers.Checkbox("Say so in chat when one arrives", () => config.AnnounceBeaconsInChat,
                v => config.AnnounceBeaconsInChat = v))
        {
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Alerts");
        UiHelpers.HelpMarker(
            "Which contacts you hear about is set per contact: right click somebody in the friend list. " +
            "These control how the alert reaches you.");

        if (UiHelpers.Checkbox("Enable alerts", () => config.AlertsEnabled, v => config.AlertsEnabled = v))
            config.Save();

        using (ImRaii.Disabled(!config.AlertsEnabled))
        {
            if (UiHelpers.Checkbox("In the chat log", () => config.AlertInChat, v => config.AlertInChat = v))
                config.Save();

            if (UiHelpers.Checkbox("As a notification", () => config.AlertAsNotification,
                    v => config.AlertAsNotification = v))
            {
                config.Save();
            }

            if (UiHelpers.Checkbox("Play a sound", () => config.AlertSound, v => config.AlertSound = v))
                config.Save();

            using (ImRaii.Disabled(!config.AlertSound))
            {
                if (UiHelpers.SliderInt("Sound", () => config.AlertSoundId, v => config.AlertSoundId = v,
                        1, 16, "effect %d"))
                {
                    config.Save();
                }
            }
        }

        ImGui.Separator();
        if (UiHelpers.Checkbox("Use my own client for nearby contacts", () => config.UseNearbyScanForContacts,
                v => config.UseNearbyScanForContacts = v,
                "When a contact is standing next to you, read their position straight from the game instead of " +
                "waiting for the next relay update. Only applies to people already linked with you."))
        {
            config.Save();
        }

        if (UiHelpers.Checkbox("Also plot in-game friends who never opted in",
                () => config.TrackNonConsentingGameFriends, v => config.TrackNonConsentingGameFriends = v))
        {
            config.Save();
        }

        ImGui.TextColored(UiHelpers.Color(UiHelpers.Warn),
            "That last option only uses what your client already renders, but those friends never agreed to be " +
            "tracked. It is off by default on purpose.");

        ImGui.Separator();
        if (UiHelpers.SliderInt("Mark stale after", () => config.StaleAfterSeconds, v => config.StaleAfterSeconds = v,
                10, 600, "%d seconds"))
        {
            config.Save();
        }

        if (UiHelpers.SliderInt("Forget after", () => config.ForgetAfterSeconds, v => config.ForgetAfterSeconds = v,
                30, 3600, "%d seconds"))
        {
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("When you are both usually around");
        UiHelpers.HelpMarker(
            "Worked out from presence you have already received, kept on this machine and never sent " +
            "anywhere. It takes a week or two of playing before it says anything useful.");

        DrawOverlap();
    }

    /// <summary>The best times to catch each contact, which is the thing people actually want to know.</summary>
    private void DrawOverlap()
    {
        var any = false;

        foreach (var contact in config.SnapshotContacts())
        {
            var windows = hub.BestOverlap(contact.AccountId, count: 2);
            if (windows.Count == 0)
                continue;

            any = true;
            var name = string.IsNullOrWhiteSpace(contact.Alias) ? contact.DisplayName : contact.Alias!;
            var described = string.Join(
                ", ",
                windows.Select(w => $"{w.Day}s {w.StartHour:00}:00-{w.EndHourExclusive:00}:00"));

            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            UiHelpers.TextMuted(described);
        }

        if (!any)
            UiHelpers.TextMuted("Nothing yet - come back after a few sessions.");
    }
}
