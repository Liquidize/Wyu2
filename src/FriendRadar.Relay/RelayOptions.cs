namespace FriendRadar.Relay;

/// <summary>Operator facing knobs, bound from the <c>Relay</c> configuration section.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Name shown to clients before they register.</summary>
    public string Name { get; set; } = "FriendRadar relay";

    /// <summary>Who runs this instance, so users know whose terms they are agreeing to.</summary>
    public string? Operator { get; set; }

    /// <summary>Free-form notice shown in the plugin's relay screen.</summary>
    public string? Message { get; set; }

    /// <summary>When false, only holders of an invite code may register.</summary>
    public bool RegistrationOpen { get; set; } = true;

    /// <summary>Invite codes accepted while registration is closed.</summary>
    public List<string> InviteCodes { get; set; } = [];

    /// <summary>Where the account/contact graph is persisted. Presence blobs are never written to disk.</summary>
    public string DataDirectory { get; set; } = "relay-data";

    /// <summary>How often the state snapshot is flushed, in seconds.</summary>
    public int SnapshotIntervalSeconds { get; set; } = 60;

    /// <summary>Accounts untouched for this many days are deleted along with their links.</summary>
    public int AccountRetentionDays { get; set; } = 90;

    /// <summary>Pending contact requests expire after this many days.</summary>
    public int ContactRequestRetentionDays { get; set; } = 14;

    /// <summary>Hard cap on mutual contacts per account.</summary>
    public int MaxContacts { get; set; } = 200;

    /// <summary>Hard cap on outstanding contact requests per account, in each direction.</summary>
    public int MaxPendingRequests { get; set; } = 50;

    /// <summary>Publish interval the relay would like clients to use.</summary>
    public int SuggestedPublishIntervalSeconds { get; set; } = 10;

    /// <summary>Requests per minute allowed per account (or per IP before authentication).</summary>
    public int RequestsPerMinute { get; set; } = 240;

    /// <summary>Maximum accounts this instance will hold. Zero means unlimited.</summary>
    public int MaxAccounts { get; set; }
}
