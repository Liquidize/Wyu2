using System.Text.Json.Serialization;
using Wyu2.Protocol;

namespace Wyu2.Relay;

/// <summary>A registered client. One per character or per player, whichever the user prefers.</summary>
public sealed class Account
{
    public required string Id { get; init; }
    public required string DisplayName { get; set; }
    public required string PublicKey { get; set; }
    public required string ShareCode { get; set; }

    /// <summary>Base64 SHA-256 of the access token. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the long-term ECDSA key that vouches for this account's epoch
    /// keys. Empty for an account registered before forward secrecy, which can be upgraded in place.
    /// </summary>
    public string SigningPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The epoch key the account last published, handed on to anyone who may see them. Null until they
    /// publish one. Kept in the snapshot so a relay restart does not leave contacts with nothing to
    /// encrypt to until the owner next comes online.
    /// </summary>
    public PrekeyBundle? Prekey { get; set; }

    public long CreatedAtUnixMs { get; init; }
    public long LastSeenAtUnixMs { get; set; }
    public string? ClientVersion { get; set; }

    /// <summary>Ids of mutually accepted contacts.</summary>
    public HashSet<string> Contacts { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A one-way invitation waiting for the other side to accept.</summary>
public sealed class ContactRequest
{
    public required string Id { get; init; }
    public required string FromAccountId { get; init; }
    public required string ToAccountId { get; init; }
    public string? Message { get; init; }
    public long CreatedAtUnixMs { get; init; }
}

/// <summary>
/// An encrypted blob parked for one recipient. Held in memory only: a relay restart drops every
/// presence update, which is the intended behaviour.
/// </summary>
public sealed class StoredPresence
{
    public required string SenderAccountId { get; init; }
    public required string RecipientAccountId { get; init; }
    public required string Nonce { get; init; }
    public required string Ciphertext { get; init; }

    /// <summary>
    /// Which epoch keys sealed this. Stored verbatim and handed back untouched: the relay cannot read
    /// the blob and has no business interpreting how it was sealed.
    /// </summary>
    public int SenderEpoch { get; init; }

    public int RecipientEpoch { get; init; }

    public long ReceivedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
}

/// <summary>
/// A labelled point one account dropped for another. Held in memory only, like presence: a relay restart
/// takes every beacon with it, and none of them are ever written to disk.
///
/// Unlike presence, a sender may have several of these parked with the same recipient at once, which is
/// why each one carries the sender's own id for it.
/// </summary>
public sealed class StoredBeacon
{
    /// <summary>Sender-chosen id, unique within that sender's beacons.</summary>
    public required string BeaconId { get; init; }

    public required string SenderAccountId { get; init; }
    public required string RecipientAccountId { get; init; }
    public required string Nonce { get; init; }
    public required string Ciphertext { get; init; }

    /// <summary>
    /// Which epoch keys sealed this. Stored verbatim and handed back untouched: the relay cannot read
    /// the blob and has no business interpreting how it was sealed.
    /// </summary>
    public int SenderEpoch { get; init; }

    public int RecipientEpoch { get; init; }

    public long ReceivedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
}

/// <summary>A named set of accounts who can all see each other.</summary>
public sealed class Group
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string OwnerAccountId { get; set; }
    public required string JoinCode { get; set; }
    public long CreatedAtUnixMs { get; init; }

    /// <summary>Account id to the time they joined.</summary>
    public Dictionary<string, long> Members { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Everything that survives a restart.</summary>
internal sealed class RelaySnapshot
{
    [JsonPropertyName("accounts")]
    public List<Account> Accounts { get; init; } = [];

    [JsonPropertyName("requests")]
    public List<ContactRequest> Requests { get; init; } = [];

    [JsonPropertyName("groups")]
    public List<Group> Groups { get; init; } = [];
}
