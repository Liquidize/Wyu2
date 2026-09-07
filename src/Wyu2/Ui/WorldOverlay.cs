using System.Numerics;
using Dalamud.Bindings.ImGui;
using Wyu2.Game;
using Wyu2.Tracking;

namespace Wyu2.Ui;

/// <summary>
/// Optional labels drawn over the game world above contacts you can see. Uses the background draw list,
/// so it costs nothing when it is switched off.
/// </summary>
public sealed class WorldOverlay(Configuration.Configuration config, PresenceHub hub)
{
    public void Draw()
    {
        if (!config.ShowWorldOverlay || !Service.ClientState.IsLoggedIn)
            return;

        var player = Service.Objects.LocalPlayer;
        if (player is null)
            return;

        var territory = (ushort)Service.ClientState.TerritoryType;
        var instance = Service.ClientState.Instance;
        var drawList = ImGui.GetBackgroundDrawList();
        var maxDistance = MathF.Max(5f, config.OverlayMaxDistance);

        foreach (var friend in hub.Friends)
        {
            if (!friend.Settings.ShowThem || friend.TerritoryTypeId != territory || friend.InstanceId != instance)
                continue;

            if (friend.Position is not { } position)
                continue;

            if (MapMath.FlatDistance(position, player.Position) > maxDistance)
                continue;

            // Lift the label to roughly head height so it does not sit inside the character model.
            var anchor = position with { Y = position.Y + 2.1f };
            if (!Service.GameGui.WorldToScreen(anchor, out var screen))
                continue;

            var staleness = UiHelpers.Staleness(friend, config.StaleAfterSeconds);
            var colour = UiHelpers.Color(UiHelpers.Fade(friend.Settings.Color, staleness));
            var shadow = UiHelpers.Color(new Vector4(0f, 0f, 0f, 0.85f));

            DrawCentred(drawList, screen, friend.Name, colour, shadow);

            if (!config.OverlayShowActivity || string.IsNullOrEmpty(friend.ActivityText))
                continue;

            var offset = screen + new Vector2(0f, ImGui.GetTextLineHeight());
            DrawCentred(drawList, offset, friend.ActivityText, UiHelpers.Color(UiHelpers.Muted), shadow);
        }
    }

    private static void DrawCentred(ImDrawListPtr drawList, Vector2 position, string text, uint colour, uint shadow)
    {
        var size = ImGui.CalcTextSize(text);
        var origin = position - new Vector2(size.X / 2f, size.Y);
        drawList.AddText(origin + new Vector2(1f, 1f), shadow, text);
        drawList.AddText(origin, colour, text);
    }
}
