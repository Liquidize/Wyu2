using System.Text.Json.Serialization;

namespace Wyu2.Protocol;

/// <summary>Public description of a relay, fetched before anyone registers with it.</summary>
public sealed record ServerInfo
{
    public string Name { get; init; } = "Wyu2 relay";
    public string? Operator { get; init; }
    public string? Message { get; init; }
    public string Version { get; init; } = "0.0.0";
    public int Protocol { get; init; } = ProtocolConstants.Version;
    public int MaxPresenceTtlSeconds { get; init; } = ProtocolConstants.MaxPresenceTtlSeconds;
    public int SuggestedPublishIntervalSeconds { get; init; } = 10;
    public int MaxContacts { get; init; } = 200;
    public bool RegistrationOpen { get; init; } = true;
    /// <summary>True when the relay never stores decryptable presence data.</summary>
    public bool EndToEndEncryptedOnly { get; init; } = true;
}

public sealed record RegisterRequest
{
    /// <summary>Label other users see next to your share code. Not necessarily a character name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Base64 SubjectPublicKeyInfo of the account's ECDH P-256 public key.</summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>Plugin version, for diagnostics.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>Invite code, when the relay is running in closed registration mode.</summary>
    public string? InviteCode { get; init; }
}

public sealed record RegisterResponse
{
    public string AccountId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string ShareCode { get; init; } = string.Empty;
}

public sealed record AccountInfo
{
    public string AccountId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ShareCode { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public long CreatedAtUnixMs { get; init; }
    public int ContactCount { get; init; }
    public int PendingRequestCount { get; init; }
}

public sealed record UpdateAccountRequest
{
    public string? DisplayName { get; init; }
    /// <summary>Set to true to mint a new share code and invalidate the old one.</summary>
    public bool RotateShareCode { get; init; }
    /// <summary>New public key, when the client rotated its keypair.</summary>
    public string? PublicKey { get; init; }
}

/// <summary>A mutually accepted contact.</summary>
public sealed record ContactDto
{
    public string AccountId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public long LinkedAtUnixMs { get; init; }
    /// <summary>When the relay last received a presence blob addressed to you from them.</summary>
    public long? LastPresenceAtUnixMs { get; init; }
}

public enum ContactRequestDirection
{
    Incoming = 0,
    Outgoing = 1,
}

public sealed record ContactRequestDto
{
    public string RequestId { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public string? Message { get; init; }
    public long CreatedAtUnixMs { get; init; }
    public ContactRequestDirection Direction { get; init; }
}

public sealed record CreateContactRequest
{
    /// <summary>Share code of the person you want to link with.</summary>
    public string ShareCode { get; init; } = string.Empty;

    /// <summary>Optional one line note shown with the request.</summary>
    public string? Message { get; init; }
}

/// <summary>One encrypted presence blob, addressed to exactly one recipient.</summary>
public sealed record PresenceEnvelope
{
    public string RecipientAccountId { get; init; } = string.Empty;
    /// <summary>Base64 AES-GCM nonce (12 bytes).</summary>
    public string Nonce { get; init; } = string.Empty;
    /// <summary>Base64 ciphertext with the 16 byte GCM tag appended.</summary>
    public string Ciphertext { get; init; } = string.Empty;
}

public sealed record PublishPresenceRequest
{
    public int TtlSeconds { get; init; } = ProtocolConstants.DefaultPresenceTtlSeconds;
    public List<PresenceEnvelope> Envelopes { get; init; } = [];
}

public sealed record PublishPresenceResponse
{
    public int Accepted { get; init; }
    public int Rejected { get; init; }
    public long ServerTimeUnixMs { get; init; }
    /// <summary>Contacts the relay wants blobs for; lets a client skip idle recipients.</summary>
    public List<string> ActiveRecipients { get; init; } = [];
}

public sealed record ReceivedPresence
{
    public string SenderAccountId { get; init; } = string.Empty;
    public string SenderDisplayName { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
    public long ReceivedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
}

public sealed record FetchPresenceResponse
{
    public List<ReceivedPresence> Entries { get; init; } = [];
    public long ServerTimeUnixMs { get; init; }
}

public sealed record ApiError
{
    public string Code { get; init; } = "error";
    public string Message { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Message);
}

// ---------------------------------------------------------------- groups

/// <summary>
/// A named set of accounts who can all see each other, so a static or a free company links once
/// instead of every pair swapping codes.
/// </summary>
public sealed record GroupDto
{
    public string GroupId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Code new members paste to join. Only returned to members.</summary>
    public string JoinCode { get; init; } = string.Empty;

    public string OwnerAccountId { get; init; } = string.Empty;
    public long CreatedAtUnixMs { get; init; }
    public List<GroupMemberDto> Members { get; init; } = [];
}

public sealed record GroupMemberDto
{
    public string AccountId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public long JoinedAtUnixMs { get; init; }
}

public sealed record CreateGroupRequest
{
    public string Name { get; init; } = string.Empty;
}

public sealed record JoinGroupRequest
{
    public string JoinCode { get; init; } = string.Empty;
}

public sealed record UpdateGroupRequest
{
    public string? Name { get; init; }

    /// <summary>Mint a new join code, invalidating the old one. Existing members are unaffected.</summary>
    public bool RotateJoinCode { get; init; }
}

// ---------------------------------------------------------------- beacons

/// <summary>One encrypted beacon, addressed to exactly one recipient.</summary>
public sealed record BeaconEnvelope
{
    public string RecipientAccountId { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
}

public sealed record PublishBeaconRequest
{
    /// <summary>Sender-chosen id, so the same beacon can be replaced or withdrawn later.</summary>
    public string BeaconId { get; init; } = string.Empty;

    public int TtlSeconds { get; init; } = ProtocolConstants.DefaultBeaconTtlSeconds;

    public List<BeaconEnvelope> Envelopes { get; init; } = [];
}

public sealed record PublishBeaconResponse
{
    public int Accepted { get; init; }
    public int Rejected { get; init; }
    public long ServerTimeUnixMs { get; init; }
}

public sealed record ReceivedBeacon
{
    public string BeaconId { get; init; } = string.Empty;
    public string SenderAccountId { get; init; } = string.Empty;
    public string SenderDisplayName { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
    public long ReceivedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
}

public sealed record FetchBeaconsResponse
{
    public List<ReceivedBeacon> Entries { get; init; } = [];
    public long ServerTimeUnixMs { get; init; }
}
