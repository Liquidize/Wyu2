using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Wyu2.Game;

/// <summary>Which of the game's two maps a view was read from.</summary>
public enum NativeMapKind
{
    /// <summary>The full zone map, opened with the map key.</summary>
    AreaMap,

    /// <summary>The minimap in the corner of the screen.</summary>
    MiniMap,
}

/// <summary>
/// A game map addon that is on screen, showing the zone the player is standing in, and ready to be
/// drawn over.
/// </summary>
/// <param name="Kind">Which addon this came from.</param>
/// <param name="Projection">World to screen transform for that addon.</param>
/// <param name="ClipMin">Top left of the rectangle drawing must stay inside.</param>
/// <param name="ClipMax">Bottom right of that rectangle.</param>
/// <param name="Centre">Middle of the drawable area, which the minimap culls around.</param>
/// <param name="Radius">Radius of the round drawable area, or zero when the area is the rectangle.</param>
/// <param name="UiScale">Interface scale in force, so markers can be sized like the rest of the HUD.</param>
public readonly record struct NativeMapView(
    NativeMapKind Kind,
    NativeMapProjection Projection,
    Vector2 ClipMin,
    Vector2 ClipMax,
    Vector2 Centre,
    float Radius,
    float UiScale);

/// <summary>
/// Reads the game's map addons so they can be drawn over. Nothing here writes to the game: node fields
/// are only ever read, and no node is created, moved or reparented.
/// </summary>
public static unsafe class NativeMapReader
{
    /// <summary>A node tree this deep is broken; the cap only exists so a cycle cannot hang the frame.</summary>
    private const int MaxNodeDepth = 32;

    /// <summary>Keeps markers off the frame the minimap is drawn inside, before interface scaling.</summary>
    private const float MiniMapInset = 4f;

    /// <summary>
    /// The full map, but only while it is showing the map the player is actually standing on. Every
    /// contact the plugin plots is in the player's own zone and instance, so a map of anywhere else has
    /// nothing to draw and the sheet's own offsets would not apply to it either.
    /// </summary>
    public static bool TryReadAreaMap(out NativeMapView view)
    {
        view = default;

        var addon = (AddonAreaMap*)Service.GameGui.GetAddonByName("AreaMap", 1).Address;
        if (addon is null || !IsUsable(&addon->AtkUnitBase))
            return false;

        if (!TryGetCurrentMap(out var sizeFactor, out var offsetX, out var offsetY))
            return false;

        var component = addon->AreaMap.ComponentMap;
        if (component is null || component->BaseMapImage is null)
            return false;

        if (!TryGetNodeRect(&component->BaseMapImage->AtkResNode, out var sheetMin, out var sheetSize))
            return false;

        // The sheet node runs past the viewport whenever the map is zoomed in, and the game clips it to
        // the component the map is drawn inside. Markers have to be clipped to that same rectangle, or
        // they would spill over the window frame and out onto the rest of the screen.
        var viewport = component->OwnerNode is not null
            ? &component->OwnerNode->AtkResNode
            : addon->AtkUnitBase.RootNode;

        if (!TryGetNodeRect(viewport, out var windowMin, out var windowSize))
            return false;

        view = new NativeMapView(
            NativeMapKind.AreaMap,
            NativeMapProjection.ForSheet(sheetMin, sheetSize, sizeFactor, offsetX, offsetY),
            windowMin,
            windowMin + windowSize,
            windowMin + (windowSize / 2f),
            0f,
            addon->AtkUnitBase.Scale);

        return view.Projection.IsUsable;
    }

    /// <summary>
    /// The minimap. It is always centred on the player and always shows the current map, so the player's
    /// own position anchors it and only the scale and the rotation have to be worked out.
    /// </summary>
    public static bool TryReadMiniMap(Vector3 playerPosition, out NativeMapView view)
    {
        view = default;

        var addon = (AddonNaviMap*)Service.GameGui.GetAddonByName("_NaviMap", 1).Address;
        if (addon is null || !IsUsable(&addon->AtkUnitBase))
            return false;

        if (!TryGetCurrentMap(out var sizeFactor, out _, out _))
            return false;

        // The mask is the circle the minimap is drawn through: unrotated, concentric with the map, and
        // therefore both the anchor and the edge to cull against.
        var mask = addon->Mask is not null ? &addon->Mask->AtkResNode : null;
        if (mask is null && addon->MainCollision is not null)
            mask = &addon->MainCollision->AtkResNode;

        if (!TryGetNodeRect(mask, out var maskMin, out var maskSize))
            return false;

        var scaling = addon->NaviMap.MarkerPositionScaling > 0f
            ? addon->NaviMap.MarkerPositionScaling
            : addon->MarkerPositionScaling;

        var pixelsPerYalm = NativeMapProjection.PixelsPerYalmFromMarkerScaling(
            sizeFactor, scaling, AccumulatedScale(mask));

        if (pixelsPerYalm <= 0f)
            return false;

        var rotation = addon->NaviMap.NorthLockedUp ? 0f : CameraRotation();
        var centre = maskMin + (maskSize / 2f);

        view = new NativeMapView(
            NativeMapKind.MiniMap,
            NativeMapProjection.ForPlayer(centre, playerPosition, pixelsPerYalm, rotation),
            maskMin,
            maskMin + maskSize,
            centre,
            (MathF.Min(maskSize.X, maskSize.Y) / 2f) - (MiniMapInset * addon->AtkUnitBase.Scale),
            addon->AtkUnitBase.Scale);

        return view.Projection.IsUsable && view.Radius > 0f;
    }

    /// <summary>
    /// The sheet the game is displaying, refused unless it is the one the player is standing on. Read
    /// from the map agent rather than from Lumina so it always agrees with what is on screen.
    /// </summary>
    private static bool TryGetCurrentMap(out ushort sizeFactor, out short offsetX, out short offsetY)
    {
        sizeFactor = 0;
        offsetX = 0;
        offsetY = 0;

        var agent = AgentMap.Instance();
        if (agent is null || agent->CurrentMapId == 0)
            return false;

        if (agent->SelectedMapId != agent->CurrentMapId || agent->SelectedTerritoryId != agent->CurrentTerritoryId)
            return false;

        if (agent->CurrentTerritoryId != Service.ClientState.TerritoryType)
            return false;

        if (agent->CurrentMapSizeFactor <= 0)
            return false;

        sizeFactor = (ushort)agent->CurrentMapSizeFactor;
        offsetX = agent->CurrentOffsetX;
        offsetY = agent->CurrentOffsetY;
        return true;
    }

    /// <summary>How far the map has turned away from north, taken from the camera the minimap follows.</summary>
    private static float CameraRotation()
    {
        var manager = CameraManager.Instance();
        if (manager is null || manager->CurrentCamera is null)
            return 0f;

        var camera = manager->CurrentCamera;
        var forward = camera->LookAtVector - camera->Position;
        return NativeMapProjection.RotationForCamera(forward.X, forward.Z);
    }

    /// <summary>An addon worth drawing over: loaded, shown, and with a root node that has a size.</summary>
    private static bool IsUsable(AtkUnitBase* addon)
        => addon is not null &&
           addon->IsVisible &&
           addon->RootNode is not null &&
           addon->RootNode->IsVisible() &&
           addon->Scale > 0f;

    /// <summary>
    /// A node's rectangle in screen pixels. <c>ScreenX</c> and <c>ScreenY</c> are what the game itself
    /// hit tests against, so they already carry every ancestor's position; only the size still has to be
    /// scaled by the chain.
    /// </summary>
    private static bool TryGetNodeRect(AtkResNode* node, out Vector2 min, out Vector2 size)
    {
        min = Vector2.Zero;
        size = Vector2.Zero;

        if (node is null || !node->IsVisible())
            return false;

        var scale = AccumulatedScale(node);
        if (scale <= 0f)
            return false;

        min = new Vector2(node->ScreenX, node->ScreenY);
        size = new Vector2(node->Width * scale, node->Height * scale);
        return size is { X: > 0f, Y: > 0f };
    }

    /// <summary>Screen pixels per node unit for a node, i.e. every scale between it and the screen.</summary>
    private static float AccumulatedScale(AtkResNode* node)
    {
        var scale = 1f;
        var depth = 0;

        for (var current = node; current is not null && depth < MaxNodeDepth; current = current->ParentNode, depth++)
            scale *= current->ScaleX;

        return scale;
    }
}
