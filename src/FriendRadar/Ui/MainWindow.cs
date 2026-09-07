using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FriendRadar.Model;
using FriendRadar.Net;
using FriendRadar.Protocol;
using FriendRadar.Tracking;

namespace FriendRadar.Ui;

/// <summary>The list view: who is online, where they are, and what they are doing.</summary>
public sealed class MainWindow : Window
{
    private readonly Configuration.Configuration config;
    private readonly PresenceHub hub;
    private readonly RelaySession session;
    private readonly RelayClient client;
    private readonly ZoneMapWindow mapWindow;
    private readonly Action openConfig;

    private string shareCodeInput = string.Empty;
    private string inviteMessageInput = string.Empty;
    private string displayNameInput = string.Empty;
    private string relayUrlInput = string.Empty;
    private string inviteCodeInput = string.Empty;
    private string statusMessage = string.Empty;
    private bool busy;

    public MainWindow(
        Configuration.Configuration config,
        PresenceHub hub,
        RelaySession session,
        RelayClient client,
        ZoneMapWindow mapWindow,
        Action openConfig)
        : base("FriendRadar##friendradar-main")
    {
        this.config = config;
        this.hub = hub;
        this.session = session;
        this.client = client;
        this.mapWindow = mapWindow;
        this.openConfig = openConfig;

        Size = new Vector2(720f, 460f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480f, 300f),
            MaximumSize = new Vector2(2400f, 1600f),
        };

        relayUrlInput = config.RelayUrl;
        displayNameInput = config.DisplayName;
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##friendradar-tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Friends"))
        {
            if (tab.Success)
                DrawFriends();
        }

        using (var tab = ImRaii.TabItem(session.Requests.Count > 0 ? $"Contacts ({session.Requests.Count})" : "Contacts"))
        {
            if (tab.Success)
                DrawContacts();
        }

        using (var tab = ImRaii.TabItem("Relay"))
        {
            if (tab.Success)
                DrawRelay();
        }
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader()
    {
        var sharing = config.SharingEnabled && !config.IsPaused;
        var reason = SelfSnapshotBuilder.Explain(hub.BlockReason);

        UiHelpers.StatusDot(
            sharing && hub.BlockReason == PublishBlockReason.None ? UiHelpers.Good : UiHelpers.Warn,
            reason);

        ImGui.SameLine();
        ImGui.TextUnformatted(sharing ? $"Sharing with {hub.PublishedRecipients} contact(s)" : "Not sharing");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{reason}. Last publish: {UiHelpers.FormatAge(hub.LastPublishAt is null ? null : DateTime.UtcNow - hub.LastPublishAt)}");

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f);

        if (ImGui.Button(config.SharingEnabled ? "Stop sharing" : "Start sharing"))
        {
            config.SharingEnabled = !config.SharingEnabled;
            config.PausedUntilUnixMs = 0;
            config.Save();
            if (config.SharingEnabled)
                hub.PublishNow();
            else
                hub.PanicStop();
        }

        ImGui.SameLine();
        if (ImGui.Button(config.IsPaused ? "Resume" : "Pause 15m"))
        {
            config.PausedUntilUnixMs = config.IsPaused
                ? 0
                : DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds();
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Temporarily stop publishing without changing any of your settings.");

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            openConfig();

        if (config.IsPaused)
        {
            var remaining = TimeSpan.FromMilliseconds(
                config.PausedUntilUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ImGui.SameLine();
            ImGui.TextColored(UiHelpers.Color(UiHelpers.Warn), $"paused for {remaining.Minutes}m {remaining.Seconds}s");
        }
    }

    // ---------------------------------------------------------------- friends

    private void DrawFriends()
    {
        if (hub.Friends.Count == 0)
        {
            ImGui.Spacing();
            UiHelpers.TextMuted(session.HasAccount
                ? "No friends are sharing with you yet. Swap share codes on the Contacts tab."
                : "Connect to a relay on the Relay tab, then swap share codes with a friend.");
            return;
        }

        using var table = ImRaii.Table("##friends", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable);

        if (!table.Success)
            return;

        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 18f);
        ImGui.TableSetupColumn("Friend", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Doing", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var friend in hub.Friends)
        {
            if (!friend.Settings.ShowThem)
                continue;

            DrawFriendRow(friend);
        }
    }

    private void DrawFriendRow(TrackedFriend friend)
    {
        using var id = ImRaii.PushId(friend.AccountId);
        var payload = friend.Payload;
        var staleness = UiHelpers.Staleness(friend, config.StaleAfterSeconds);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        UiHelpers.StatusDot(
            payload is null ? UiHelpers.Muted : UiHelpers.Fade(friend.Settings.Color, staleness),
            payload is null ? "No update yet" : "Sharing with you");

        ImGui.TableNextColumn();

        // A selectable rather than plain text: the row needs an ImGui id for the context menu to attach to.
        ImGui.Selectable(friend.Name, false, ImGuiSelectableFlags.SpanAllColumns);
        if (ImGui.IsItemHovered())
            DrawFriendTooltip(friend);

        DrawFriendContextMenu(friend);

        ImGui.TableNextColumn();
        if (payload?.JobId is { } job && job != 0)
            ImGui.TextUnformatted($"{friend.JobAbbreviation} {payload.Level}");
        else
            UiHelpers.TextMuted("-");

        ImGui.TableNextColumn();
        DrawWhere(friend);

        ImGui.TableNextColumn();
        if (payload is null)
        {
            UiHelpers.TextMuted("Waiting for an update");
        }
        else
        {
            ImGui.TextColored(
                UiHelpers.Color(UiHelpers.Fade(UiHelpers.ActivityColor(payload.Activity), staleness)),
                $"[{UiHelpers.ActivityTag(payload.Activity)}] {friend.ActivityText}");

            if (!string.IsNullOrWhiteSpace(payload.Note))
            {
                ImGui.SameLine();
                UiHelpers.TextMuted($"- {payload.Note}");
            }
        }

        ImGui.TableNextColumn();
        UiHelpers.TextMuted(UiHelpers.FormatAge(friend.Age));
    }

    private void DrawWhere(TrackedFriend friend)
    {
        var payload = friend.Payload;
        if (payload?.TerritoryTypeId is null)
        {
            UiHelpers.TextMuted("Not shared");
            return;
        }

        var zone = string.IsNullOrWhiteSpace(friend.ZoneName) ? "Unknown zone" : friend.ZoneName;
        var instance = payload.InstanceId is > 0 ? $" (i{payload.InstanceId})" : string.Empty;
        var coordinates = friend.MapCoordinates is { } c ? $" {MapCoordinateText(c)}" : string.Empty;

        ImGui.TextUnformatted($"{zone}{instance}");
        if (!string.IsNullOrEmpty(coordinates) && ImGui.IsItemHovered())
            ImGui.SetTooltip(coordinates.Trim());

        ImGui.SameLine();
        if (ImGui.SmallButton("Map"))
            mapWindow.FocusOn(friend);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show this zone on the friend map.");
    }

    private static string MapCoordinateText(Vector2 coordinates)
        => Game.MapMath.FormatCoordinates(coordinates);

    private void DrawFriendTooltip(TrackedFriend friend)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(friend.Name);
        UiHelpers.TextMuted($"Relay label: {friend.RelayDisplayName}");

        var payload = friend.Payload;
        if (payload is null)
        {
            UiHelpers.TextMuted("They have not sent an update yet.");
            return;
        }

        if (!string.IsNullOrEmpty(friend.WorldName))
            UiHelpers.TextMuted($"World: {friend.WorldName}");
        if (!string.IsNullOrEmpty(friend.RegionName))
            UiHelpers.TextMuted($"Region: {friend.RegionName}");
        if (!string.IsNullOrEmpty(friend.OnlineStatusName))
            UiHelpers.TextMuted($"Status: {friend.OnlineStatusName}");
        if (payload.PartySize is > 0)
            UiHelpers.TextMuted($"Party of {payload.PartySize}");
        if (!string.IsNullOrEmpty(payload.FreeCompanyTag))
            UiHelpers.TextMuted($"FC: {payload.FreeCompanyTag}");
        if (friend.MapCoordinates is { } coordinates)
            UiHelpers.TextMuted($"At {MapCoordinateText(coordinates)}");

        UiHelpers.TextMuted(friend.Sources.HasFlag(PresenceSource.Nearby)
            ? "Position is live from your own client."
            : "Position is from their last relay update.");
    }

    private void DrawFriendContextMenu(TrackedFriend friend)
    {
        using var popup = ImRaii.ContextPopupItem("friendradar-friend-menu");
        if (!popup.Success)
            return;

        var settings = friend.Settings;
        ImGui.TextUnformatted(friend.Name);
        ImGui.Separator();

        var alias = settings.Alias ?? string.Empty;
        if (ImGui.InputTextWithHint("Nickname", "shown instead of their name", ref alias, 40))
        {
            settings.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
            config.Save();
        }

        if (UiHelpers.ColorEdit("Radar colour", () => settings.Color, v => settings.Color = v))
            config.Save();

        if (UiHelpers.Checkbox("Pin to top", () => settings.Pinned, v => settings.Pinned = v))
            config.Save();

        if (UiHelpers.Checkbox("Share my presence with them", () => settings.ShareWithThem, v => settings.ShareWithThem = v))
        {
            config.Save();
            hub.PublishNow();
        }

        if (UiHelpers.Checkbox("Announce their zone changes", () => settings.NotifyOnZoneChange, v => settings.NotifyOnZoneChange = v))
            config.Save();

        if (ImGui.MenuItem("Hide from radar and list"))
        {
            settings.ShowThem = false;
            config.Save();
        }
    }

    // ---------------------------------------------------------------- contacts

    private void DrawContacts()
    {
        if (!session.HasAccount)
        {
            ImGui.Spacing();
            UiHelpers.TextMuted("Connect to a relay first - see the Relay tab.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Your share code");
        UiHelpers.HelpMarker(
            "Give this to somebody you want to share with. They paste it below, you accept, and only then " +
            "does anything start flowing - in either direction.");

        ImGui.SameLine();
        ImGui.TextColored(UiHelpers.Color(UiHelpers.Good), config.ShareCode);
        ImGui.SameLine();
        UiHelpers.CopyButton("Copy", config.ShareCode, "Copy your share code to the clipboard.");
        ImGui.SameLine();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("New code"))
                RunAsync(RotateShareCodeAsync);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mint a new code. Old invites stop working; existing contacts are unaffected.");

        ImGui.Separator();
        ImGui.TextUnformatted("Add a contact");
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##sharecode", "FR-XXXX-XXXX-XXXX-XXXX", ref shareCodeInput, 40);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##invitemessage", "optional note", ref inviteMessageInput, 140);
        ImGui.SameLine();

        using (ImRaii.Disabled(busy || string.IsNullOrWhiteSpace(shareCodeInput)))
        {
            if (ImGui.Button("Send invite"))
                RunAsync(SendInviteAsync);
        }

        DrawStatusMessage();

        if (session.Requests.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Invites");
            foreach (var request in session.Requests)
                DrawRequestRow(request);
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Contacts ({config.ContactCount})");
        DrawContactTable();
    }

    private void DrawRequestRow(ContactRequestDto request)
    {
        using var id = ImRaii.PushId(request.RequestId);

        if (request.Direction == ContactRequestDirection.Incoming)
        {
            ImGui.TextUnformatted($"{request.DisplayName} wants to share with you");
            if (!string.IsNullOrWhiteSpace(request.Message))
            {
                ImGui.SameLine();
                UiHelpers.TextMuted($"\"{request.Message}\"");
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.SmallButton("Accept"))
                    RunAsync(() => AcceptAsync(request.RequestId));

                ImGui.SameLine();
                if (ImGui.SmallButton("Decline"))
                    RunAsync(() => DeclineAsync(request.RequestId));
            }

            UiHelpers.TextMuted("Accepting links you both. You still choose what to share on the Settings screen.");
        }
        else
        {
            UiHelpers.TextMuted($"Waiting for {request.DisplayName} to accept");
            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.SmallButton("Withdraw"))
                    RunAsync(() => DeclineAsync(request.RequestId));
            }
        }
    }

    private void DrawContactTable()
    {
        var contacts = config.SnapshotContacts();
        if (contacts.Count == 0)
        {
            UiHelpers.TextMuted("Nobody yet.");
            return;
        }

        using var table = ImRaii.Table("##contacts", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);

        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Contact", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("I share", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("I see", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        foreach (var contact in contacts)
        {
            using var id = ImRaii.PushId(contact.AccountId);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(contact.Alias) ? contact.DisplayName : contact.Alias!);

            ImGui.TableNextColumn();
            if (UiHelpers.Checkbox("##share", () => contact.ShareWithThem, v => contact.ShareWithThem = v))
            {
                config.Save();
                hub.PublishNow();
            }

            ImGui.TableNextColumn();
            if (UiHelpers.Checkbox("##show", () => contact.ShowThem, v => contact.ShowThem = v))
                config.Save();

            ImGui.TableNextColumn();
            DrawProfileSelector(contact);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.SmallButton("Remove"))
                    RunAsync(() => RemoveContactAsync(contact.AccountId));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unlink completely. Neither of you can see the other afterwards.");
        }
    }

    private void DrawProfileSelector(Configuration.ContactSettings contact)
    {
        var current = contact.ProfileOverride is null ? "Default" : "Custom";
        ImGui.SetNextItemWidth(-1f);
        using var combo = ImRaii.Combo("##profile", current);
        if (!combo.Success)
            return;

        if (ImGui.Selectable("Default", contact.ProfileOverride is null))
        {
            contact.ProfileOverride = null;
            config.Save();
        }

        if (ImGui.Selectable("Everything"))
        {
            contact.ProfileOverride = Configuration.SharingProfile.Everything();
            config.Save();
        }

        if (ImGui.Selectable("Zone only"))
        {
            contact.ProfileOverride = Configuration.SharingProfile.ZoneOnly();
            config.Save();
        }

        if (ImGui.Selectable("Online only"))
        {
            contact.ProfileOverride = Configuration.SharingProfile.Minimal();
            config.Save();
        }
    }

    // ---------------------------------------------------------------- relay

    private void DrawRelay()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "FriendRadar needs a relay to pass updates between clients. It only ever sees encrypted blobs; " +
            "run your own from this repository, or use one a friend runs.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(340f);
        ImGui.InputTextWithHint("Relay URL", "https://relay.example.com", ref relayUrlInput, 200);
        ImGui.SameLine();
        using (ImRaii.Disabled(busy || string.IsNullOrWhiteSpace(relayUrlInput)))
        {
            if (ImGui.Button("Check"))
                RunAsync(CheckRelayAsync);
        }

        if (client.ServerInfo is { } info)
        {
            UiHelpers.TextMuted($"{info.Name} - protocol {info.Protocol}, version {info.Version}");
            if (!string.IsNullOrWhiteSpace(info.Operator))
                UiHelpers.TextMuted($"Run by {info.Operator}");
            if (!string.IsNullOrWhiteSpace(info.Message))
                ImGui.TextWrapped(info.Message);
            if (!info.RegistrationOpen)
                ImGui.TextColored(UiHelpers.Color(UiHelpers.Warn), "This relay is invite only.");
        }

        ImGui.Separator();

        if (session.HasAccount)
        {
            DrawAccount();
            return;
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("Display name", "how contacts see you", ref displayNameInput, 48);
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("Invite code", "only if the relay is closed", ref inviteCodeInput, 64);

        using (ImRaii.Disabled(busy || string.IsNullOrWhiteSpace(displayNameInput) || string.IsNullOrWhiteSpace(relayUrlInput)))
        {
            if (ImGui.Button("Create account"))
                RunAsync(RegisterAsync);
        }

        UiHelpers.TextMuted("Creating an account generates an encryption key that stays on this machine.");
        DrawStatusMessage();
    }

    private void DrawAccount()
    {
        ImGui.TextUnformatted($"Connected as {config.DisplayName}");
        UiHelpers.TextMuted($"Account {config.AccountId}");
        UiHelpers.TextMuted($"Relay state: {client.State}{(client.LastError is null ? string.Empty : $" - {client.LastError}")}");
        UiHelpers.TextMuted($"Contacts synced {UiHelpers.FormatAge(session.ContactsRefreshedAt is null ? null : DateTime.UtcNow - session.ContactsRefreshedAt)}");

        ImGui.Spacing();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Sync now"))
                hub.SyncContactsNow();

            ImGui.SameLine();
            if (ImGui.Button("Forget account on this PC"))
            {
                session.ForgetLocalAccount();
                statusMessage = "Local credentials cleared. The relay still holds the account until you delete it.";
            }

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f)))
            {
                if (ImGui.Button("Delete account"))
                    RunAsync(DeleteAccountAsync);
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes your account, your links and anything parked for you on the relay.");

        DrawStatusMessage();
    }

    private void DrawStatusMessage()
    {
        if (string.IsNullOrEmpty(statusMessage))
            return;

        ImGui.Spacing();
        ImGui.TextWrapped(statusMessage);
    }

    // ---------------------------------------------------------------- async plumbing

    private void RunAsync(Func<Task> action)
    {
        if (busy)
            return;

        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Service.Log.Error(ex, "FriendRadar UI action failed");
                statusMessage = ex.Message;
            }
            finally
            {
                busy = false;
            }
        });
    }

    private async Task CheckRelayAsync()
    {
        config.RelayUrl = relayUrlInput.Trim();
        config.Save();
        client.Invalidate();

        var info = await client.GetServerInfoAsync().ConfigureAwait(false);
        statusMessage = info is null
            ? client.LastError ?? "Could not reach that relay."
            : $"Reached {info.Name}.";
    }

    private async Task RegisterAsync()
    {
        config.RelayUrl = relayUrlInput.Trim();
        config.Save();
        client.Invalidate();

        var error = await session.RegisterAsync(displayNameInput, inviteCodeInput).ConfigureAwait(false);
        statusMessage = error ?? $"Registered. Your share code is {config.ShareCode}.";
        inviteCodeInput = string.Empty;
    }

    private async Task RotateShareCodeAsync()
    {
        var updated = await client.UpdateAccountAsync(new UpdateAccountRequest { RotateShareCode = true })
            .ConfigureAwait(false);

        if (updated is null)
        {
            statusMessage = client.LastError ?? "Could not change the share code.";
            return;
        }

        config.ShareCode = updated.ShareCode;
        config.Save();
        statusMessage = $"New share code: {updated.ShareCode}";
    }

    private async Task SendInviteAsync()
    {
        var ok = await client.SendContactRequestAsync(
            shareCodeInput.Trim(),
            string.IsNullOrWhiteSpace(inviteMessageInput) ? null : inviteMessageInput.Trim()).ConfigureAwait(false);

        statusMessage = ok
            ? "Invite sent. It starts working once they accept."
            : client.LastError ?? "The relay refused that invite.";

        if (ok)
        {
            shareCodeInput = string.Empty;
            inviteMessageInput = string.Empty;
            await session.RefreshContactsAsync().ConfigureAwait(false);
        }
    }

    private async Task AcceptAsync(string requestId)
    {
        var ok = await client.AcceptContactRequestAsync(requestId).ConfigureAwait(false);
        statusMessage = ok ? "Linked." : client.LastError ?? "Could not accept that invite.";
        await session.RefreshContactsAsync().ConfigureAwait(false);
    }

    private async Task DeclineAsync(string requestId)
    {
        await client.DeclineContactRequestAsync(requestId).ConfigureAwait(false);
        await session.RefreshContactsAsync().ConfigureAwait(false);
    }

    private async Task RemoveContactAsync(string accountId)
    {
        var ok = await client.RemoveContactAsync(accountId).ConfigureAwait(false);
        statusMessage = ok ? "Contact removed." : client.LastError ?? "Could not remove that contact.";
        await session.RefreshContactsAsync().ConfigureAwait(false);
    }

    private async Task DeleteAccountAsync()
    {
        await session.DeleteAccountAsync().ConfigureAwait(false);
        statusMessage = "Account deleted.";
    }
}
