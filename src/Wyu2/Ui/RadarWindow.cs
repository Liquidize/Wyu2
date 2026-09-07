using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Tracking;

namespace Wyu2.Ui;

/// <summary>
/// Top-down radar of everybody in your zone who is sharing a position, plus a count of the friends who
/// are somewhere else entirely.
/// </summary>
public sealed class RadarWindow : Window
{
    private readonly Configuration.Configuration config;
    private readonly PresenceHub hub;
    private readonly NearbyScanner scanner;
    private readonly List<Blip> blips = [];

    public RadarWindow(Configuration.Configuration config, PresenceHub hub, NearbyScanner scanner)
        : base("Wyu2 radar##wyu2-radar")
    {
        this.config = config;
        this.hub = hub;
        this.scanner = scanner;

        Size = new Vector2(320f, 380f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(220f, 240f),
            MaximumSize = new Vector2(1400f, 1400f),
        };

        // Same reasoning as the map window: the radar sizes itself to the space it is given, so a
        // scrollbar appearing would change that space and feed straight back into the size.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    /// <summary>Set when the user clicks a blip, so the map window can follow along.</summary>
    public string? FocusRequest { get; private set; }

    public string? ConsumeFocusRequest()
    {
        var request = FocusRequest;
        FocusRequest = null;
        return request;
    }

    public override void Draw()
    {
        var player = Service.Objects.LocalPlayer;
        if (player is null)
        {
            UiHelpers.TextMuted("Not logged in.");
            return;
        }

        DrawToolbar();

        var available = ImGui.GetContentRegionAvail();
        // Reserve the footer line, and let the radar shrink rather than overflow: a minimum size that
        // exceeds the content region is what makes a scrollbar appear and the layout oscillate.
        var side = MathF.Min(available.X, available.Y - ImGui.GetTextLineHeightWithSpacing());
        if (side < 32f)
        {
            UiHelpers.TextMuted("Not enough room to draw the radar.");
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var centre = origin + new Vector2(side / 2f, side / 2f);
        var radius = (side / 2f) - 4f;

        var drawList = ImGui.GetWindowDrawList();
        DrawBackground(drawList, centre, radius);

        var yaw = 0f;
        if (config.Radar.RotateWithCamera)
            CameraUtil.TryGetYaw(out yaw);

        // One shared clock for the sweep and every ping, so nothing can drift apart.
        var animate = config.Radar.Animate;
        var sweepAngle = animate
            ? RadarSweep.Angle(ImGui.GetTime(), config.Radar.SweepSeconds)
            : 0f;

        if (animate && config.Radar.ShowSweep)
            DrawSweep(drawList, centre, radius, sweepAngle);

        DrawCardinals(drawList, centre, radius, yaw);

        CollectBlips(player.Position, player.EntityId);
        DrawBlips(drawList, centre, radius, yaw, sweepAngle);

        if (config.Radar.ShowSelf)
            DrawSelf(drawList, centre, player.Rotation, yaw);

        ImGui.Dummy(new Vector2(side, side));
        DrawFooter();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
        if (UiHelpers.SliderFloat("##range", () => config.Radar.RangeYalms, v => config.Radar.RangeYalms = v,
                20f, 500f, "%.0f yalms"))
        {
            config.Save();
        }

        ImGui.SameLine();
        if (UiHelpers.Checkbox("Camera", () => config.Radar.RotateWithCamera, v => config.Radar.RotateWithCamera = v))
            config.Save();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rotate the radar with the camera instead of keeping north at the top.");
    }

    private void DrawBackground(ImDrawListPtr drawList, Vector2 centre, float radius)
    {
        var background = config.Radar.BackgroundColor;
        background.W *= config.Radar.Opacity;
        drawList.AddCircleFilled(centre, radius, UiHelpers.Color(background), 64);

        if (!config.Radar.ShowDistanceRings)
            return;

        var grid = UiHelpers.Color(config.Radar.GridColor);
        for (var i = 1; i <= 3; i++)
            drawList.AddCircle(centre, radius * (i / 3f), grid, 64, i == 3 ? 1.6f : 1f);
    }

    private void DrawCardinals(ImDrawListPtr drawList, Vector2 centre, float radius, float yaw)
    {
        var grid = UiHelpers.Color(config.Radar.GridColor);
        drawList.AddLine(centre - new Vector2(radius, 0f), centre + new Vector2(radius, 0f), grid);
        drawList.AddLine(centre - new Vector2(0f, radius), centre + new Vector2(0f, radius), grid);

        // North is -Z in world space; rotating it by the current yaw shows where north ended up.
        var north = CameraUtil.WorldOffsetToRadar(new Vector3(0f, 0f, -1f), yaw);
        var label = centre + (north * (radius - 10f));
        drawList.AddText(label - new Vector2(4f, 7f), UiHelpers.Color(UiHelpers.Warn), "N");
    }

    /// <summary>
    /// The rotating wedge: a fan of triangles trailing the leading edge, each a little more transparent
    /// than the last, which is what gives a scope its comet tail.
    /// </summary>
    private void DrawSweep(ImDrawListPtr drawList, Vector2 centre, float radius, float sweepAngle)
    {
        const int segments = 40;
        const float tailAngle = MathF.PI * 0.7f;

        var colour = config.Radar.SweepColor;
        var previous = centre + (Direction(sweepAngle) * radius);

        for (var i = 1; i <= segments; i++)
        {
            var t = i / (float)segments;
            var point = centre + (Direction(sweepAngle - (tailAngle * t)) * radius);

            var fade = 1f - t;
            var wedge = colour with { W = fade * fade * 0.33f * config.Radar.Opacity };
            drawList.AddTriangleFilled(centre, previous, point, UiHelpers.Color(wedge));

            previous = point;
        }

        var edge = colour with { W = 0.85f * config.Radar.Opacity };
        drawList.AddLine(centre, centre + (Direction(sweepAngle) * radius), UiHelpers.Color(edge), 1.6f);
    }

    /// <summary>Unit vector for a bearing, matching how blip bearings are measured.</summary>
    private static Vector2 Direction(float radians) => new(MathF.Cos(radians), MathF.Sin(radians));

    private void CollectBlips(Vector3 selfPosition, uint selfEntityId)
    {
        blips.Clear();

        var territory = (ushort)Service.ClientState.TerritoryType;
        var instance = Service.ClientState.Instance;
        var world = Service.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0;

        foreach (var friend in hub.Friends)
        {
            if (!friend.Settings.ShowThem)
                continue;

            if (!IsInSameInstance(friend, territory, instance, world))
                continue;

            if (friend.Position is not { } position)
                continue;

            var staleness = UiHelpers.Staleness(friend, config.StaleAfterSeconds);
            var colour = staleness >= 1f
                ? config.Radar.StaleColor
                : UiHelpers.Fade(friend.Settings.Color, staleness);

            blips.Add(new Blip(
                friend.AccountId,
                friend.Name,
                position - selfPosition,
                MapMath.FlatDistance(position, selfPosition),
                colour,
                friend.ActivityText,
                friend.JobAbbreviation,
                friend.Sources.HasFlag(PresenceSource.Nearby)));
        }

        if (!config.TrackNonConsentingGameFriends)
            return;

        foreach (var nearby in scanner.NearbyGameFriends)
        {
            if (nearby.EntityId == selfEntityId)
                continue;

            blips.Add(new Blip(
                $"game:{nearby.EntityId}",
                nearby.Name,
                nearby.Position - selfPosition,
                MapMath.FlatDistance(nearby.Position, selfPosition),
                UiHelpers.Muted,
                "In-game friend (not sharing)",
                string.Empty,
                true));
        }
    }

    /// <summary>
    /// Two characters are only in the same place when the zone, the public instance and the world all
    /// line up; otherwise plotting their coordinates would put them somewhere they are not.
    /// </summary>
    private static bool IsInSameInstance(TrackedFriend friend, ushort territory, uint instance, uint world)
    {
        if (friend.TerritoryTypeId != territory)
            return false;

        if (friend.InstanceId != instance)
            return false;

        var theirWorld = friend.Payload?.CurrentWorldId ?? 0;
        return theirWorld == 0 || world == 0 || theirWorld == world;
    }

    private void DrawBlips(ImDrawListPtr drawList, Vector2 centre, float radius, float yaw, float sweepAngle)
    {
        var range = MathF.Max(10f, config.Radar.RangeYalms);
        var scale = radius / range;
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsWindowHovered();
        var ping = config.Radar.Animate && config.Radar.PingOnSweep;
        var period = MathF.Max(0.5f, config.Radar.SweepSeconds);

        foreach (var blip in blips)
        {
            var offset = CameraUtil.WorldOffsetToRadar(blip.Offset, yaw) * scale;
            var clamped = false;

            if (offset.Length() > radius - 6f)
            {
                if (!config.Radar.ShowOffscreenArrows)
                    continue;

                offset = Vector2.Normalize(offset) * (radius - 6f);
                clamped = true;
            }

            var point = centre + offset;

            // How long since the sweep last crossed this blip's bearing. Derived from the bearing rather
            // than remembered per blip, so contacts coming and going never desynchronise.
            var strength = 0f;
            var sincePass = 0f;
            if (ping)
            {
                sincePass = RadarSweep.SecondsSincePass(
                    sweepAngle, MathF.Atan2(offset.Y, offset.X), period);
                strength = RadarSweep.PingStrength(sincePass, period * 0.45f);
            }

            // Blips stay legible between pings; the flash rides on top rather than replacing them.
            var tint = ping
                ? Vector4.Lerp(blip.Color, Vector4.One, strength * 0.55f) with
                {
                    W = blip.Color.W * (0.62f + (0.38f * strength)),
                }
                : blip.Color;

            var colour = UiHelpers.Color(tint);
            var size = config.Radar.BlipSize * (1f + (0.45f * strength));

            if (clamped)
            {
                DrawArrow(drawList, point, offset, colour);
            }
            else
            {
                if (ping)
                {
                    var progress = RadarSweep.RingProgress(sincePass, period * 0.35f);
                    if (progress > 0f)
                    {
                        var ringColour = tint with { W = (1f - progress) * 0.7f };
                        drawList.AddCircle(
                            point,
                            config.Radar.BlipSize + (progress * 16f),
                            UiHelpers.Color(ringColour),
                            20,
                            1.5f);
                    }
                }

                drawList.AddCircleFilled(point, size, colour, 16);
                drawList.AddCircle(point, size, UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.7f)), 16, 1.4f);
            }

            if (config.Radar.ShowNames && !clamped)
            {
                var label = config.Radar.ShowJobIcons && !string.IsNullOrEmpty(blip.Job)
                    ? $"{blip.Name} ({blip.Job})"
                    : blip.Name;

                // Text is left un-pinged: a label flashing once a second is just hard to read.
                var textSize = ImGui.CalcTextSize(label);
                drawList.AddText(
                    point - new Vector2(textSize.X / 2f, textSize.Y + config.Radar.BlipSize + 1f),
                    UiHelpers.Color(blip.Color),
                    label);
            }

            if (hovered && Vector2.Distance(mouse, point) <= config.Radar.BlipSize + 4f)
            {
                using (var tooltip = ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted(blip.Name);
                    UiHelpers.TextMuted(blip.Activity);
                    UiHelpers.TextMuted($"{blip.Distance:0.0} yalms away{(blip.Live ? ", live" : string.Empty)}");
                }

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    FocusRequest = blip.Key;
            }
        }
    }

    private static void DrawArrow(ImDrawListPtr drawList, Vector2 point, Vector2 offset, uint colour)
    {
        var direction = Vector2.Normalize(offset);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        drawList.AddTriangleFilled(
            point + (direction * 6f),
            point - (direction * 4f) + (perpendicular * 4f),
            point - (direction * 4f) - (perpendicular * 4f),
            colour);
    }

    private void DrawSelf(ImDrawListPtr drawList, Vector2 centre, float rotation, float yaw)
    {
        // Character rotation is measured from south, so the facing vector is (sin, cos) of it.
        var facing = CameraUtil.WorldOffsetToRadar(
            new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation)), yaw);

        if (facing.LengthSquared() < 0.0001f)
            facing = new Vector2(0f, -1f);

        facing = Vector2.Normalize(facing);
        var perpendicular = new Vector2(-facing.Y, facing.X);
        var colour = UiHelpers.Color(config.Radar.SelfColor);

        drawList.AddTriangleFilled(
            centre + (facing * 9f),
            centre - (facing * 5f) + (perpendicular * 5f),
            centre - (facing * 5f) - (perpendicular * 5f),
            colour);
    }

    private void DrawFooter()
    {
        var elsewhere = hub.Friends.Count(f => f.Settings.ShowThem && f.IsOnline && !blips.Any(b => b.Key == f.AccountId));
        var shown = blips.Count;

        UiHelpers.TextMuted($"{shown} in view, {elsewhere} elsewhere");
        if (elsewhere > 0 && ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            foreach (var friend in hub.Friends.Where(f => f.Settings.ShowThem && f.IsOnline))
            {
                if (blips.Any(b => b.Key == friend.AccountId))
                    continue;

                ImGui.TextUnformatted($"{friend.Name} - {(string.IsNullOrEmpty(friend.ZoneName) ? "somewhere" : friend.ZoneName)}");
            }
        }
    }

    private readonly record struct Blip(
        string Key,
        string Name,
        Vector3 Offset,
        float Distance,
        Vector4 Color,
        string Activity,
        string Job,
        bool Live);
}
