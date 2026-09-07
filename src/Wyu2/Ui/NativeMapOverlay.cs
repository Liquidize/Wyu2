using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Tracking;

namespace Wyu2.Ui;

/// <summary>
/// Draws contacts and beacons over the game's own map and minimap, so friends turn up on the map the
/// player already has open rather than only in the plugin's window.
/// </summary>
/// <remarks>
/// Everything is painted into the background draw list on top of the addon's own rectangle. The game's
/// node tree is only ever read: nothing is created, moved or reparented, so the worst a wrong reading
/// can do is put a dot in the wrong place for a frame.
/// </remarks>
public sealed class NativeMapOverlay(
    Configuration.Configuration config, PresenceHub hub, BeaconService beacons)
{
    /// <summary>Markers on the minimap are drawn smaller, because there is far less room for them.</summary>
    private const float MiniMapMarkerFactor = 0.72f;

    private readonly List<Marker> markers = [];

    /// <summary>Paints one frame of markers over whichever of the two maps is on screen.</summary>
    public void Draw()
    {
        var settings = config.NativeMap;
        if (!settings.OnAreaMap && !settings.OnMiniMap)
            return;

        if (!CanDrawOverGameUi())
            return;

        var player = Service.Objects.LocalPlayer;
        if (player is null)
            return;

        CollectMarkers();
        if (markers.Count == 0)
            return;

        var drawList = ImGui.GetBackgroundDrawList();

        if (settings.OnAreaMap && NativeMapReader.TryReadAreaMap(out var areaMap))
            DrawView(drawList, areaMap);

        if (settings.OnMiniMap && NativeMapReader.TryReadMiniMap(player.Position, out var miniMap))
            DrawView(drawList, miniMap);
    }

    /// <summary>
    /// Whether the game's interface is in a state where an overlay belongs at all. Drawing over hidden
    /// UI or through a cutscene would put the plugin on screen at exactly the moments the player asked
    /// for nothing to be.
    /// </summary>
    private static bool CanDrawOverGameUi()
    {
        if (!Service.ClientState.IsLoggedIn || Service.GameGui.GameUiHidden)
            return false;

        var condition = Service.Condition;
        return !condition[ConditionFlag.WatchingCutscene] &&
               !condition[ConditionFlag.WatchingCutscene78] &&
               !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
               !condition[ConditionFlag.BetweenAreas] &&
               !condition[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// Everybody worth plotting, gathered once for both maps. The test is the strict one the radar
    /// uses: the same zone, the same public instance and the same world, because coordinates from
    /// another copy of the map would be drawn as though they were here.
    /// </summary>
    private void CollectMarkers()
    {
        markers.Clear();

        var territory = (ushort)Service.ClientState.TerritoryType;
        var instance = Service.ClientState.Instance;
        var world = Service.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0;

        foreach (var friend in hub.Friends)
        {
            if (!config.CanSee(friend.Settings) || !IsHere(friend, territory, instance, world))
                continue;

            if (friend.Position is not { } position)
                continue;

            var staleness = UiHelpers.Staleness(friend, config.StaleAfterSeconds);
            var colour = staleness >= 1f
                ? config.Radar.StaleColor
                : UiHelpers.Fade(friend.Settings.Color, staleness);

            markers.Add(new Marker(position, colour, friend.Name, IsBeacon: false));
        }

        if (!config.ShowBeacons || !config.NativeMap.ShowBeacons)
            return;

        foreach (var beacon in beacons.Beacons)
        {
            if (!beacon.IsInSameInstance(territory, instance, world))
                continue;

            markers.Add(new Marker(
                beacon.Position, UiHelpers.BeaconColor(beacon.Kind), beacon.Label, IsBeacon: true));
        }
    }

    /// <summary>
    /// Two characters are in the same place only when the zone, the public instance and the world all
    /// line up. This is deliberately stricter than the plugin's own map window, which matches on the
    /// zone alone because it can be pointed at a zone the player is not standing in.
    /// </summary>
    private static bool IsHere(TrackedFriend friend, ushort territory, uint instance, uint world)
    {
        if (friend.TerritoryTypeId != territory || friend.InstanceId != instance)
            return false;

        var theirWorld = friend.Payload?.CurrentWorldId ?? 0;
        return theirWorld == 0 || world == 0 || theirWorld == world;
    }

    private void DrawView(ImDrawListPtr drawList, NativeMapView view)
    {
        var settings = config.NativeMap;
        var round = view.Radius > 0f;
        var clamp = round && settings.ClampToMiniMapEdge;

        var scale = MathF.Max(0.5f, view.UiScale);
        var radius = MathF.Max(2f, settings.MarkerSize) * scale;
        if (view.Kind == NativeMapKind.MiniMap)
            radius *= MiniMapMarkerFactor;

        drawList.PushClipRect(view.ClipMin, view.ClipMax, true);

        foreach (var marker in markers)
        {
            var point = view.Projection.Project(marker.Position);

            if (round)
            {
                if (!NativeMapProjection.TryFitInCircle(view.Centre, view.Radius, point, clamp, out point))
                    continue;
            }
            else if (point.X < view.ClipMin.X || point.Y < view.ClipMin.Y ||
                     point.X > view.ClipMax.X || point.Y > view.ClipMax.Y)
            {
                continue;
            }

            var colour = UiHelpers.Color(marker.Color);
            if (marker.IsBeacon)
                UiHelpers.Diamond(drawList, point, radius, colour);
            else
                DrawDot(drawList, point, radius, colour);

            // Names are left off the minimap: at that size they overlap each other and the game's own
            // markers long before they tell anybody anything.
            if (settings.ShowNames && view.Kind == NativeMapKind.AreaMap)
                DrawLabel(drawList, point, radius, marker.Name, colour);
        }

        drawList.PopClipRect();
    }

    private static void DrawDot(ImDrawListPtr drawList, Vector2 point, float radius, uint colour)
    {
        drawList.AddCircleFilled(point, radius, colour, 16);
        drawList.AddCircle(point, radius, UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.8f)), 16, 1.5f);
    }

    private static void DrawLabel(ImDrawListPtr drawList, Vector2 point, float radius, string text, uint colour)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var size = ImGui.CalcTextSize(text);
        var origin = point - new Vector2(size.X / 2f, size.Y + radius + 2f);

        // The map sheet runs from near white to near black, so the label needs its own shadow to stay
        // readable wherever it lands.
        drawList.AddText(origin + new Vector2(1f, 1f), UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.85f)), text);
        drawList.AddText(origin, colour, text);
    }

    private readonly record struct Marker(Vector3 Position, Vector4 Color, string Name, bool IsBeacon);
}
