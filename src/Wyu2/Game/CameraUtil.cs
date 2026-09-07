using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Wyu2.Game;

/// <summary>Reads the active game camera so the radar can be drawn the way the player is looking.</summary>
public static unsafe class CameraUtil
{
    /// <summary>
    /// Yaw of the active camera in radians, where the returned angle is measured the same way world
    /// deltas are: <c>atan2(x, z)</c>. False when there is no camera, e.g. at the title screen.
    /// </summary>
    public static bool TryGetYaw(out float yaw)
    {
        yaw = 0f;

        var manager = CameraManager.Instance();
        if (manager is null)
            return false;

        var camera = manager->CurrentCamera;
        if (camera is null)
            return false;

        var forward = camera->LookAtVector - camera->Position;
        if (MathF.Abs(forward.X) < 0.0001f && MathF.Abs(forward.Z) < 0.0001f)
            return false;

        yaw = MathF.Atan2(forward.X, forward.Z);
        return true;
    }

    /// <summary>
    /// Turns a world space offset into radar space. The arithmetic lives in
    /// <see cref="RadarProjection"/> so it can be tested without the game.
    /// </summary>
    public static Vector2 WorldOffsetToRadar(Vector3 offset, float yaw)
        => RadarProjection.WorldOffsetToRadar(offset, yaw);
}
