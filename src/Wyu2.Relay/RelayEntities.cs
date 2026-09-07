using System.Text.Json.Serialization;

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
    public long ReceivedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
}

/// <summary>Everything that survives a restart.</summary>
internal sealed class RelaySnapshot
{
    [JsonPropertyName("accounts")]
    public List<Account> Accounts { get; init; } = [];

    [JsonPropertyName("requests")]
    public List<ContactRequest> Requests { get; init; } = [];
}
