using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace Wyu2.Net;

/// <summary>
/// Optional bridge to the Teleporter plugin. Wyu2 works fine without it: when Teleporter is not
/// installed the UI names the aetheryte instead of offering to fly you there.
/// </summary>
public sealed class TeleporterIpc
{
    private const string TeleportEndpoint = "Teleport";

    private readonly ICallGateSubscriber<uint, byte, bool> teleport =
        Service.PluginInterface.GetIpcSubscriber<uint, byte, bool>(TeleportEndpoint);

    /// <summary>Last failure, shown in a tooltip rather than thrown at the user.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Asks Teleporter to take you to an aetheryte. Returns false when the plugin is absent or refused,
    /// which callers treat as "tell them the name instead".
    /// </summary>
    public bool TryTeleport(uint aetheryteId, byte subIndex)
    {
        try
        {
            var accepted = teleport.InvokeFunc(aetheryteId, subIndex);
            LastError = accepted ? null : "Teleporter refused that destination.";
            return accepted;
        }
        catch (IpcNotReadyError)
        {
            LastError = "The Teleporter plugin is not installed.";
            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Teleporter IPC failed");
            LastError = "Teleporter could not be reached.";
            return false;
        }
    }
}
