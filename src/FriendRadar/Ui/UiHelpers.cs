using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FriendRadar.Model;
using FriendRadar.Protocol;

namespace FriendRadar.Ui;

/// <summary>Small drawing helpers shared by the windows.</summary>
public static class UiHelpers
{
    public static readonly Vector4 Muted = new(0.62f, 0.65f, 0.70f, 1f);
    public static readonly Vector4 Good = new(0.45f, 0.85f, 0.50f, 1f);
    public static readonly Vector4 Warn = new(0.98f, 0.76f, 0.32f, 1f);
    public static readonly Vector4 Bad = new(0.95f, 0.42f, 0.42f, 1f);

    /// <summary>Colour used for a friend's activity text.</summary>
    public static Vector4 ActivityColor(ActivityKind kind) => kind switch
    {
        ActivityKind.InCombat => new Vector4(0.96f, 0.51f, 0.42f, 1f),
        ActivityKind.InDuty => new Vector4(0.62f, 0.66f, 0.98f, 1f),
        ActivityKind.InPvp => new Vector4(0.98f, 0.45f, 0.65f, 1f),
        ActivityKind.Crafting => new Vector4(0.82f, 0.70f, 0.42f, 1f),
        ActivityKind.Gathering => new Vector4(0.55f, 0.85f, 0.55f, 1f),
        ActivityKind.Fishing => new Vector4(0.46f, 0.80f, 0.90f, 1f),
        ActivityKind.Cutscene => new Vector4(0.72f, 0.62f, 0.90f, 1f),
        ActivityKind.Waiting => new Vector4(0.80f, 0.80f, 0.55f, 1f),
        ActivityKind.Housing => new Vector4(0.85f, 0.72f, 0.60f, 1f),
        ActivityKind.GoldSaucer => new Vector4(0.98f, 0.83f, 0.40f, 1f),
        ActivityKind.Performing => new Vector4(0.90f, 0.62f, 0.88f, 1f),
        ActivityKind.Trading => new Vector4(0.75f, 0.80f, 0.62f, 1f),
        ActivityKind.Travelling => new Vector4(0.70f, 0.80f, 0.90f, 1f),
        ActivityKind.Offline => Muted,
        _ => new Vector4(0.85f, 0.87f, 0.90f, 1f),
    };

    /// <summary>Four character tag drawn in the friend list, so the eye can scan the column.</summary>
    public static string ActivityTag(ActivityKind kind) => kind switch
    {
        ActivityKind.InCombat => "FGHT",
        ActivityKind.InDuty => "DUTY",
        ActivityKind.InPvp => "PVP",
        ActivityKind.Crafting => "CRFT",
        ActivityKind.Gathering => "GATH",
        ActivityKind.Fishing => "FISH",
        ActivityKind.Cutscene => "CUTS",
        ActivityKind.Waiting => "WAIT",
        ActivityKind.Housing => "HOME",
        ActivityKind.GoldSaucer => "GATE",
        ActivityKind.Performing => "PERF",
        ActivityKind.Trading => "TRDE",
        ActivityKind.Travelling => "MOVE",
        ActivityKind.Idle => "IDLE",
        ActivityKind.Offline => "OFF",
        _ => "----",
    };

    public static uint Color(Vector4 color) => ImGui.GetColorU32(color);

    /// <summary>Fades a colour towards grey as an update gets older.</summary>
    public static Vector4 Fade(Vector4 color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        var grey = new Vector4(0.55f, 0.55f, 0.55f, color.W);
        return Vector4.Lerp(color, grey, amount);
    }

    public static string FormatAge(TimeSpan? age)
    {
        if (age is null)
            return "never";

        var seconds = age.Value.TotalSeconds;
        return seconds switch
        {
            < 5 => "now",
            < 60 => $"{(int)seconds}s ago",
            < 3600 => $"{(int)(seconds / 60)}m ago",
            < 86400 => $"{(int)(seconds / 3600)}h ago",
            _ => $"{(int)(seconds / 86400)}d ago",
        };
    }

    /// <summary>How stale a friend's data is, 0 (fresh) to 1 (about to be forgotten).</summary>
    public static float Staleness(TrackedFriend friend, int staleAfterSeconds)
    {
        if (friend.Age is not { } age)
            return 1f;

        return Math.Clamp((float)(age.TotalSeconds / Math.Max(5, staleAfterSeconds)), 0f, 1f);
    }

    public static void TextMuted(string text) => ImGui.TextColored(Color(Muted), text);

    /// <summary>A "(?)" that explains something on hover.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextColored(Color(Muted), "(?)");
        if (!ImGui.IsItemHovered())
            return;

        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 26f);
        ImGui.TextUnformatted(text);
    }

    /// <summary>Checkbox bound to a property, since ImGui needs a ref.</summary>
    public static bool Checkbox(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        var value = get();
        var changed = ImGui.Checkbox(label, ref value);
        if (changed)
            set(value);

        if (tooltip is not null)
            HelpMarker(tooltip);

        return changed;
    }

    public static bool SliderFloat(string label, Func<float> get, Action<float> set, float min, float max, string format)
    {
        var value = get();
        if (!ImGui.SliderFloat(label, ref value, min, max, format))
            return false;

        set(value);
        return true;
    }

    public static bool SliderInt(string label, Func<int> get, Action<int> set, int min, int max, string format)
    {
        var value = get();
        if (!ImGui.SliderInt(label, ref value, min, max, format))
            return false;

        set(value);
        return true;
    }

    public static bool ColorEdit(string label, Func<Vector4> get, Action<Vector4> set)
    {
        var value = get();
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
            return false;

        set(value);
        return true;
    }

    public static bool InputText(string label, Func<string> get, Action<string> set, int maxLength, string? hint = null)
    {
        var value = get();
        var changed = hint is null
            ? ImGui.InputText(label, ref value, maxLength)
            : ImGui.InputTextWithHint(label, hint, ref value, maxLength);

        if (changed)
            set(value);

        return changed;
    }

    /// <summary>Draws a filled dot at the current line, used as a status light.</summary>
    public static void StatusDot(Vector4 color, string? tooltip = null)
    {
        var radius = ImGui.GetFontSize() * 0.25f;
        var cursor = ImGui.GetCursorScreenPos();
        var centre = cursor + new Vector2(radius + 2f, ImGui.GetTextLineHeight() * 0.5f);
        ImGui.GetWindowDrawList().AddCircleFilled(centre, radius, Color(color));
        ImGui.Dummy(new Vector2((radius * 2f) + 4f, ImGui.GetTextLineHeight()));

        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>Copies text and tells the user it happened.</summary>
    public static void CopyButton(string label, string value, string tooltip)
    {
        if (ImGui.Button(label))
        {
            ImGui.SetClipboardText(value);
            Service.Chat.Print($"Copied: {value}", "FriendRadar");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
