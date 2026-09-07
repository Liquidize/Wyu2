using System.Numerics;
using Dalamud.Configuration;

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

    /// <summary>Colour used for their radar blip.</summary>
    public Vector4 Color { get; set; } = new(0.35f, 0.78f, 1.00f, 1f);

    /// <summary>Announce in chat when they come online or change zone.</summary>
    public bool NotifyOnZoneChange { get; set; }

    /// <summary>
    /// Where this contact was the last time they published anything, kept so a friend who logs out
    /// leaves "last seen 20 minutes ago in Eulmore" behind rather than an empty row. Written when they
    /// go quiet, so it survives a restart.
    /// </summary>
    public long LastSeenAtUnixMs { get; set; }

    public ushort LastSeenTerritoryId { get; set; }

    public string? LastSeenActivity { get; set; }
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
    public float BlipSize { get; set; } = 6f;
    public float Opacity { get; set; } = 0.9f;
    public Vector4 BackgroundColor { get; set; } = new(0.03f, 0.05f, 0.08f, 0.72f);
    public Vector4 GridColor { get; set; } = new(0.35f, 0.45f, 0.6f, 0.45f);
    public Vector4 SelfColor { get; set; } = new(1f, 0.85f, 0.35f, 1f);
    public Vector4 SweepColor { get; set; } = new(0.40f, 0.92f, 0.76f, 1f);
    public Vector4 StaleColor { get; set; } = new(0.55f, 0.55f, 0.55f, 1f);
}

/// <summary>Everything the plugin remembers between sessions.</summary>
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

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
    public bool ShowMainWindowOnStart { get; set; }
    public bool ShowDtrEntry { get; set; } = true;
    public bool ShowWorldOverlay { get; set; }

    /// <summary>Pulse a sonar ring out of each map marker.</summary>
    public bool AnimateMapMarkers { get; set; } = true;
    public bool OverlayShowActivity { get; set; } = true;
    public float OverlayMaxDistance { get; set; } = 200f;
    public bool AnnounceZoneChangesInChat { get; set; }

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

    /// <summary>The profile that applies to one contact.</summary>
    public SharingProfile ProfileFor(ContactSettings contact)
        => contact.ProfileOverride ?? DefaultProfile;

    /// <summary>True while a manual pause is in effect.</summary>
    public bool IsPaused => PausedUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>True when the plugin has a usable relay account.</summary>
    public bool HasRelayAccount =>
        !string.IsNullOrEmpty(RelayUrl) &&
        !string.IsNullOrEmpty(AccountId) &&
        !string.IsNullOrEmpty(AccessToken) &&
        !string.IsNullOrEmpty(PrivateKey);

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
