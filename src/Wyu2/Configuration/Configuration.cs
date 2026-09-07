using System.Numerics;
using Dalamud.Configuration;
using Wyu2.Protocol;

namespace Wyu2.Configuration;

/// <summary>Per-contact settings. Both directions are controlled locally, by you.</summary>
public sealed class ContactSettings
{
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Label the relay reported for them.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Base64 SubjectPublicKeyInfo used to encrypt payloads for them.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Your own name for them, shown everywhere in the UI when set.</summary>
    public string? Alias { get; set; }

    /// <summary>Publish your presence to this contact.</summary>
    public bool ShareWithThem { get; set; } = true;

    /// <summary>Show this contact on the radar, map and list.</summary>
    public bool ShowThem { get; set; } = true;

    /// <summary>Keep this contact at the top of the list.</summary>
    public bool Pinned { get; set; }

    /// <summary>Optional narrower profile just for this contact; null means use the default profile.</summary>
    public SharingProfile? ProfileOverride { get; set; }

    /// <summary>
    /// True when you are linked with this person directly, by swapping share codes. Somebody can be a
    /// direct contact, a fellow group member, or both, and unlinking one leaves the other standing.
    /// </summary>
    public bool IsDirectContact { get; set; } = true;

    /// <summary>Groups you share with this person, if any.</summary>
    public List<string> GroupIds { get; set; } = [];

    /// <summary>Their long-term signing key, used to check that a prekey really came from them.</summary>
    public string SigningPublicKey { get; set; } = string.Empty;

    /// <summary>Their current epoch key, once we have verified the bundle that carried it.</summary>
    public int PrekeyEpoch { get; set; }

    public string PrekeyPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The epoch before it. Kept because a payload sealed just before they rotated arrives after we have
    /// already seen the new bundle, and without this it could not be opened.
    /// </summary>
    public int PreviousPrekeyEpoch { get; set; }

    public string PreviousPrekeyPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Set once this contact has ever published a valid prekey, and never cleared. A relay that later
    /// withholds their bundle would otherwise silently push the pair back onto long-term keys and take
    /// forward secrecy away without either of them noticing.
    /// </summary>
    public bool SupportsForwardSecrecy { get; set; }

    /// <summary>Colour used for their radar blip.</summary>
    public Vector4 Color { get; set; } = new(0.35f, 0.78f, 1.00f, 1f);

    /// <summary>
    /// Superseded by <see cref="Alerts"/> in configuration version 2. Kept so an existing setting can be
    /// migrated rather than silently lost.
    /// </summary>
    public bool NotifyOnZoneChange { get; set; }

    /// <summary>Which of this contact's transitions are worth telling you about.</summary>
    public AlertTriggers Alerts { get; set; } = AlertTriggers.None;

    /// <summary>
    /// Where this contact was the last time they published anything, kept so a friend who logs out
    /// leaves "last seen 20 minutes ago in Eulmore" behind rather than an empty row. Written when they
    /// go quiet, so it survives a restart.
    /// </summary>
    public long LastSeenAtUnixMs { get; set; }

    public ushort LastSeenTerritoryId { get; set; }

    public string? LastSeenActivity { get; set; }
}

/// <summary>One of our own epoch keys as written to the configuration file.</summary>
public sealed class StoredEpochKey
{
    public int Epoch { get; set; }

    /// <summary>PKCS#8 private key, base64. Deleted when the epoch ages out.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    public long CreatedAtUnixMs { get; set; }

    public long ExpiresAtUnixMs { get; set; }
}

/// <summary>Local settings for one group. The group itself lives on the relay.</summary>
public sealed class GroupSettings
{
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Name as the relay last reported it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Publish your presence to this group's members.</summary>
    public bool ShareWithGroup { get; set; } = true;

    /// <summary>Show this group's members on the radar, map and list.</summary>
    public bool ShowGroup { get; set; } = true;

    /// <summary>Narrower profile for everybody reached only through this group.</summary>
    public SharingProfile? ProfileOverride { get; set; }
}

/// <summary>How the radar window draws.</summary>
public sealed class RadarSettings
{
    public bool Enabled { get; set; } = true;
    public float RangeYalms { get; set; } = 150f;
    public bool RotateWithCamera { get; set; } = true;
    public bool ShowDistanceRings { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool ShowJobIcons { get; set; } = true;
    public bool ShowOffscreenArrows { get; set; } = true;
    public bool ShowSelf { get; set; } = true;

    /// <summary>Master switch for the sweep and the ping animation.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Draw the rotating sweep wedge.</summary>
    public bool ShowSweep { get; set; } = true;

    /// <summary>Flash each blip as the sweep passes over it.</summary>
    public bool PingOnSweep { get; set; } = true;

    /// <summary>Seconds for one full rotation.</summary>
    public float SweepSeconds { get; set; } = 3f;

    /// <summary>Draw a fading breadcrumb trail behind each contact.</summary>
    public bool ShowTrails { get; set; } = true;

    /// <summary>How many seconds of movement a trail keeps.</summary>
    public float TrailSeconds { get; set; } = 25f;
    public float BlipSize { get; set; } = 6f;
    public float Opacity { get; set; } = 0.9f;
    public Vector4 BackgroundColor { get; set; } = new(0.03f, 0.05f, 0.08f, 0.72f);
    public Vector4 GridColor { get; set; } = new(0.35f, 0.45f, 0.6f, 0.45f);
    public Vector4 SelfColor { get; set; } = new(1f, 0.85f, 0.35f, 1f);
    public Vector4 SweepColor { get; set; } = new(0.40f, 0.92f, 0.76f, 1f);
    public Vector4 StaleColor { get; set; } = new(0.55f, 0.55f, 0.55f, 1f);
}

/// <summary>
/// Drawing contacts over the game's own map and minimap, rather than only in the plugin's window.
/// </summary>
public sealed class NativeMapSettings
{
    /// <summary>Draw over the full zone map the map key opens.</summary>
    public bool OnAreaMap { get; set; } = true;

    /// <summary>Draw over the minimap in the corner of the screen.</summary>
    public bool OnMiniMap { get; set; } = true;

    /// <summary>Write each contact's name beside their marker.</summary>
    public bool ShowNames { get; set; } = true;

    /// <summary>Draw beacons there too, subject to the master beacon switch.</summary>
    public bool ShowBeacons { get; set; } = true;

    /// <summary>
    /// Pin contacts who are past the edge of the minimap to its rim instead of dropping them. The
    /// minimap covers so little ground that most contacts are outside it most of the time.
    /// </summary>
    public bool ClampToMiniMapEdge { get; set; } = true;

    /// <summary>Marker radius in pixels at an interface scale of one.</summary>
    public float MarkerSize { get; set; } = 5f;
}

/// <summary>Everything the plugin remembers between sessions.</summary>
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    // ------------------------------------------------------------------ relay

    /// <summary>Base URL of the relay, e.g. <c>https://relay.example.com</c>. Empty means local-only mode.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>
    /// Account id, access token and private key for the relay. This file is plain JSON in your Dalamud
    /// config directory: anybody who can read it can impersonate you on the relay.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The long-term key we sign epoch keys with. Like <see cref="PrivateKey"/> this sits in plain JSON;
    /// anybody who can read the file can impersonate this account on its relay.
    /// </summary>
    public string SigningPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Our own epoch private keys, newest first. Only the ones still inside the retention window are
    /// written, so pruning survives a restart and a destroyed epoch cannot be resurrected.
    /// </summary>
    public List<StoredEpochKey> EpochKeys { get; set; } = [];

    /// <summary>
    /// Rises on every publish and never resets. Recipients refuse anything that does not advance it,
    /// which is what makes a replayed update detectable.
    /// </summary>
    public long PresenceSequence { get; set; }

    /// <summary>
    /// The highest epoch the relay has actually accepted. Payloads are sealed under this rather than the
    /// newest key we hold, because a key contacts have never seen is a key they cannot decrypt with.
    /// </summary>
    public int PublishedEpoch { get; set; }

    public string ShareCode { get; set; } = string.Empty;

    /// <summary>Label other people see next to your share code.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Seconds between presence publishes while the game is in the foreground.</summary>
    public int PublishIntervalSeconds { get; set; } = 10;

    /// <summary>Seconds between presence fetches.</summary>
    public int FetchIntervalSeconds { get; set; } = 10;

    // ------------------------------------------------------------------ sharing

    /// <summary>
    /// The master switch. Off by default: the plugin never publishes anything until you deliberately
    /// turn this on.
    /// </summary>
    public bool SharingEnabled { get; set; }

    /// <summary>Unix ms until which publishing is paused; 0 when not paused.</summary>
    public long PausedUntilUnixMs { get; set; }

    public SharingProfile DefaultProfile { get; set; } = new();

    public PrivacyRules Privacy { get; set; } = new();

    /// <summary>Free-form note published with your presence, e.g. "grinding tomes, ping me".</summary>
    public string StatusNote { get; set; } = string.Empty;

    // ------------------------------------------------------------------ contacts

    public List<ContactSettings> Contacts { get; set; } = [];

    /// <summary>Local settings for the groups you belong to.</summary>
    public List<GroupSettings> Groups { get; set; } = [];

    /// <summary>
    /// Guards <see cref="Contacts"/>. The relay sync runs on the thread pool while the windows and the
    /// hub read the same list from the framework thread, so every access goes through the helpers below.
    /// </summary>
    private readonly Lock contactsLock = new();

    // ------------------------------------------------------------------ local tracking

    /// <summary>
    /// Use the game's own object table to fill in exact positions for contacts standing next to you.
    /// This only ever applies to people already linked with you on the relay.
    /// </summary>
    public bool UseNearbyScanForContacts { get; set; } = true;

    /// <summary>
    /// Also plot in-game friends who never opted into Wyu2, using only what the game already
    /// renders on your screen. Off by default, because they did not agree to be tracked.
    /// </summary>
    public bool TrackNonConsentingGameFriends { get; set; }

    // ------------------------------------------------------------------ ui

    public RadarSettings Radar { get; set; } = new();

    /// <summary>Contacts drawn over the game's own map and minimap.</summary>
    public NativeMapSettings NativeMap { get; set; } = new();

    public bool ShowMainWindowOnStart { get; set; }
    public bool ShowDtrEntry { get; set; } = true;
    public bool ShowWorldOverlay { get; set; }

    /// <summary>Pulse a sonar ring out of each map marker.</summary>
    public bool AnimateMapMarkers { get; set; } = true;
    public bool OverlayShowActivity { get; set; } = true;
    public float OverlayMaxDistance { get; set; } = 200f;

    // ------------------------------------------------------------------ beacons

    /// <summary>Draw beacons on the radar and the zone map.</summary>
    public bool ShowBeacons { get; set; } = true;

    /// <summary>Say so in the chat log when somebody drops a beacon for you.</summary>
    public bool AnnounceBeaconsInChat { get; set; } = true;

    /// <summary>How long a beacon you drop stays up, in minutes. Clamped to what the protocol allows.</summary>
    public int BeaconMinutes { get; set; } = 15;

    /// <summary>The kind offered first the next time you drop one, so a hunt train is one click.</summary>
    public BeaconKind LastBeaconKind { get; set; } = BeaconKind.Marker;

    // ------------------------------------------------------------------ alerts

    /// <summary>Master switch for contact alerts. Individual triggers are per contact.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Print alerts to the chat log.</summary>
    public bool AlertInChat { get; set; } = true;

    /// <summary>Show alerts as Dalamud notifications.</summary>
    public bool AlertAsNotification { get; set; } = true;

    /// <summary>Play a sound with each alert.</summary>
    public bool AlertSound { get; set; }

    /// <summary>Which of the game's sixteen chat sound effects to play.</summary>
    public int AlertSoundId { get; set; } = 1;

    /// <summary>Seconds after which an entry is drawn as stale.</summary>
    public int StaleAfterSeconds { get; set; } = 60;

    /// <summary>Seconds after which an entry disappears entirely.</summary>
    public int ForgetAfterSeconds { get; set; } = 300;

    public ContactSettings? FindContact(string accountId)
    {
        lock (contactsLock)
            return Contacts.FirstOrDefault(c => string.Equals(c.AccountId, accountId, StringComparison.Ordinal));
    }

    /// <summary>A copy that is safe to iterate while the relay sync is running.</summary>
    public List<ContactSettings> SnapshotContacts()
    {
        lock (contactsLock)
            return [.. Contacts];
    }

    public int ContactCount
    {
        get
        {
            lock (contactsLock)
                return Contacts.Count;
        }
    }

    /// <summary>Mutates the contact list under the lock. Returns whatever the callback returns.</summary>
    public T EditContacts<T>(Func<List<ContactSettings>, T> edit)
    {
        lock (contactsLock)
            return edit(Contacts);
    }

    /// <summary>
    /// The profile that applies to one contact: their own override first, then the override of a group
    /// you reach them through, then the default. A direct contact is never narrowed by a group's profile,
    /// because you linked with them personally.
    /// </summary>
    public SharingProfile ProfileFor(ContactSettings contact)
    {
        if (contact.ProfileOverride is { } personal)
            return personal;

        if (contact.IsDirectContact)
            return DefaultProfile;

        foreach (var groupId in contact.GroupIds)
        {
            if (FindGroup(groupId) is { ShareWithGroup: true, ProfileOverride: { } shared })
                return shared;
        }

        return DefaultProfile;
    }

    public GroupSettings? FindGroup(string groupId)
    {
        lock (contactsLock)
            return Groups.FirstOrDefault(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal));
    }

    public List<GroupSettings> SnapshotGroups()
    {
        lock (contactsLock)
            return [.. Groups];
    }

    /// <summary>
    /// Whether this contact should appear at all. Somebody reached only through a group you have hidden
    /// stays hidden, without having to untick them one by one.
    /// </summary>
    public bool CanSee(ContactSettings contact)
        => contact.ShowThem && ReachableBy(contact, group => group.ShowGroup);

    /// <summary>Whether your presence should go to this contact.</summary>
    public bool CanShareWith(ContactSettings contact)
        => contact.ShareWithThem && ReachableBy(contact, group => group.ShareWithGroup);

    private bool ReachableBy(ContactSettings contact, Func<GroupSettings, bool> groupAllows)
    {
        if (contact.IsDirectContact)
            return true;

        foreach (var groupId in contact.GroupIds)
        {
            if (FindGroup(groupId) is { } group && groupAllows(group))
                return true;
        }

        return false;
    }

    /// <summary>True while a manual pause is in effect.</summary>
    public bool IsPaused => PausedUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>True when the plugin has a usable relay account.</summary>
    public bool HasRelayAccount =>
        !string.IsNullOrEmpty(RelayUrl) &&
        !string.IsNullOrEmpty(AccountId) &&
        !string.IsNullOrEmpty(AccessToken) &&
        !string.IsNullOrEmpty(PrivateKey);

    /// <summary>
    /// Brings an older configuration up to date. Returns true when something changed and the file should
    /// be written back.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 2)
        {
            // The single "announce zone changes" flag became a set of per-contact triggers.
            foreach (var contact in Contacts.Where(c => c.NotifyOnZoneChange))
                contact.Alerts |= AlertTriggers.ChangedZone;
        }

        Version = CurrentVersion;
        return true;
    }

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
