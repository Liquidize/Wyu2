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
        if (UiHelpers.ColorEdit("Background", () => radar.BackgroundColor, v => radar.BackgroundColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Grid", () => radar.GridColor, v => radar.GridColor = v))
            config.Save();
        if (UiHelpers.ColorEdit("Me", () => radar.SelfColor, v => radar.SelfColor = v))
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

        if (UiHelpers.Checkbox("Announce zone changes in chat", () => config.AnnounceZoneChangesInChat,
                v => config.AnnounceZoneChangesInChat = v,
                "Only for contacts with the per-contact announcement toggle switched on."))
        {
            config.Save();
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
    }
}
