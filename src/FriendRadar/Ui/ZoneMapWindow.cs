using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FriendRadar.Game;
using FriendRadar.Model;
using FriendRadar.Tracking;
using Lumina.Excel.Sheets;

namespace FriendRadar.Ui;

/// <summary>
/// Draws the actual zone map sheet with friend markers on it, so "where are they" has a picture rather
/// than a pair of numbers. Also links back into the game's own map.
/// </summary>
public sealed class ZoneMapWindow : Window
{
    private readonly Configuration.Configuration config;
    private readonly PresenceHub hub;
    private readonly GameDataCache data;

    private uint selectedMapId;
    private ushort selectedTerritory;
    private bool followSelf = true;
    private float zoom = 1f;
    private Vector2 pan = Vector2.Zero;

    public ZoneMapWindow(Configuration.Configuration config, PresenceHub hub, GameDataCache data)
        : base("Friend map##friendradar-map")
    {
        this.config = config;
        this.hub = hub;
        this.data = data;

        Size = new Vector2(560f, 620f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320f, 320f),
            MaximumSize = new Vector2(2000f, 2000f),
        };
    }

    /// <summary>Points the map at whichever zone a friend is in.</summary>
    public void FocusOn(TrackedFriend friend)
    {
        if (friend.TerritoryTypeId is not { } territory)
            return;

        followSelf = false;
        selectedTerritory = territory;
        selectedMapId = friend.MapId != 0 ? friend.MapId : data.GetMapForTerritory(territory)?.RowId ?? 0;
        pan = Vector2.Zero;
        zoom = 1f;
        IsOpen = true;
    }

    public override void Draw()
    {
        DrawToolbar();

        if (followSelf && Service.ClientState.IsLoggedIn)
        {
            selectedTerritory = (ushort)Service.ClientState.TerritoryType;
            selectedMapId = Service.ClientState.MapId;
        }

        var map = selectedMapId != 0 ? data.GetMap(selectedMapId) : data.GetMapForTerritory(selectedTerritory);
        if (map is null)
        {
            UiHelpers.TextMuted("No map to show yet. Pick a zone above, or wait for a friend to share one.");
            return;
        }

        var path = MapMath.GetMapTexturePath(map.Value);
        if (path is null)
        {
            UiHelpers.TextMuted("That zone has no map sheet in the game files.");
            return;
        }

        if (!Service.Textures.GetFromGame(path).TryGetWrap(out var texture, out _))
        {
            UiHelpers.TextMuted("Loading map...");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        var side = MathF.Max(160f, MathF.Min(available.X, available.Y));
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var half = 0.5f / zoom;
        var centre = new Vector2(0.5f, 0.5f) + pan;
        var uvMin = centre - new Vector2(half, half);
        var uvMax = centre + new Vector2(half, half);

        drawList.AddRectFilled(origin, origin + new Vector2(side, side), UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.6f)));
        drawList.AddImage(texture.Handle, origin, origin + new Vector2(side, side), uvMin, uvMax);

        ImGui.InvisibleButton("##mapsurface", new Vector2(side, side));
        HandleMouse();

        DrawMarkers(drawList, map.Value, origin, side, uvMin, uvMax);
        DrawLegend(map.Value);
    }

    private void DrawToolbar()
    {
        if (UiHelpers.Checkbox("Follow me", () => followSelf, v => followSelf = v))
        {
            pan = Vector2.Zero;
            zoom = 1f;
        }

        ImGui.SameLine();

        var zones = hub.Friends
            .Where(f => f.Settings.ShowThem && f.TerritoryTypeId is > 0)
            .Select(f => (Territory: f.TerritoryTypeId!.Value, Map: f.MapId, Name: f.ZoneName))
            .DistinctBy(z => z.Territory)
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preview = selectedTerritory == 0 ? "Pick a zone" : data.GetZoneName(selectedTerritory);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
        using (var combo = ImRaii.Combo("##zone", string.IsNullOrEmpty(preview) ? "Pick a zone" : preview))
        {
            if (combo.Success)
            {
                foreach (var zone in zones)
                {
                    if (!ImGui.Selectable(string.IsNullOrEmpty(zone.Name) ? $"Zone {zone.Territory}" : zone.Name))
                        continue;

                    followSelf = false;
                    selectedTerritory = zone.Territory;
                    selectedMapId = zone.Map != 0 ? zone.Map : data.GetMapForTerritory(zone.Territory)?.RowId ?? 0;
                    pan = Vector2.Zero;
                    zoom = 1f;
                }

                if (zones.Count == 0)
                    UiHelpers.TextMuted("No friends are sharing a zone right now.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset view"))
        {
            pan = Vector2.Zero;
            zoom = 1f;
        }
    }

    /// <summary>Drag to pan, wheel to zoom, clamped so the sheet cannot be dragged off screen.</summary>
    private void HandleMouse()
    {
        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (MathF.Abs(wheel) > 0.01f)
                zoom = Math.Clamp(zoom * (1f + (wheel * 0.15f)), 1f, 8f);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            var size = ImGui.GetItemRectSize();
            if (size.X > 0f)
                pan -= delta / size.X / zoom;
        }

        var limit = MathF.Max(0f, 0.5f - (0.5f / zoom));
        pan = new Vector2(Math.Clamp(pan.X, -limit, limit), Math.Clamp(pan.Y, -limit, limit));
    }

    private void DrawMarkers(ImDrawListPtr drawList, Map map, Vector2 origin, float side, Vector2 uvMin, Vector2 uvMax)
    {
        var span = uvMax - uvMin;
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();

        drawList.PushClipRect(origin, origin + new Vector2(side, side), true);

        foreach (var friend in hub.Friends)
        {
            if (!friend.Settings.ShowThem || friend.TerritoryTypeId != selectedTerritory)
                continue;

            if (friend.Position is not { } position)
                continue;

            var uv = MapMath.WorldToTextureUv(position, map);
            var point = origin + (((uv - uvMin) / span) * side);
            if (point.X < origin.X || point.Y < origin.Y || point.X > origin.X + side || point.Y > origin.Y + side)
                continue;

            var staleness = UiHelpers.Staleness(friend, config.StaleAfterSeconds);
            var colour = UiHelpers.Color(UiHelpers.Fade(friend.Settings.Color, staleness));

            drawList.AddCircleFilled(point, 6f, colour, 16);
            drawList.AddCircle(point, 6f, UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.8f)), 16, 1.5f);

            var label = friend.Name;
            var size = ImGui.CalcTextSize(label);
            drawList.AddText(point - new Vector2(size.X / 2f, size.Y + 8f), colour, label);

            if (!hovered || Vector2.Distance(mouse, point) > 9f)
                continue;

            using (var tooltip = ImRaii.Tooltip())
            {
                ImGui.TextUnformatted(friend.Name);
                UiHelpers.TextMuted(friend.ActivityText);
                if (friend.MapCoordinates is { } coordinates)
                    UiHelpers.TextMuted(MapMath.FormatCoordinates(coordinates));
                UiHelpers.TextMuted($"Updated {UiHelpers.FormatAge(friend.Age)}");
                UiHelpers.TextMuted("Click to open the in-game map here.");
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Service.GameGui.OpenMapWithMapLink(
                    new Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload(
                        friend.TerritoryTypeId ?? 0,
                        map.RowId,
                        friend.MapCoordinates?.X ?? 0f,
                        friend.MapCoordinates?.Y ?? 0f));
            }
        }

        DrawSelfMarker(drawList, map, origin, side, uvMin, span);
        drawList.PopClipRect();
    }

    private void DrawSelfMarker(ImDrawListPtr drawList, Map map, Vector2 origin, float side, Vector2 uvMin, Vector2 span)
    {
        var player = Service.Objects.LocalPlayer;
        if (player is null || Service.ClientState.TerritoryType != selectedTerritory)
            return;

        var uv = MapMath.WorldToTextureUv(player.Position, map);
        var point = origin + (((uv - uvMin) / span) * side);
        drawList.AddCircleFilled(point, 5f, UiHelpers.Color(config.Radar.SelfColor), 16);
        drawList.AddCircle(point, 8f, UiHelpers.Color(config.Radar.SelfColor), 16, 1.5f);
    }

    private void DrawLegend(Map map)
    {
        var name = map.PlaceName.ValueNullable?.Name.ExtractText();
        var here = hub.Friends.Count(f => f.Settings.ShowThem && f.TerritoryTypeId == selectedTerritory);
        UiHelpers.TextMuted($"{(string.IsNullOrWhiteSpace(name) ? "Unknown zone" : name)} - {here} friend(s) here");
    }
}
