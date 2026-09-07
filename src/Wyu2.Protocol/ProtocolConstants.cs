namespace Wyu2.Protocol;

/// <summary>
/// Values that the plugin and the relay have to agree on.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Wire protocol version. Bumped whenever a breaking change lands.</summary>
    public const int Version = 1;

    /// <summary>Header carrying the account access token.</summary>
    public const string AuthorizationScheme = "Bearer";

    /// <summary>Header the client uses to announce its protocol version.</summary>
    public const string ProtocolVersionHeader = "X-Wyu2-Protocol";

    /// <summary>Header the client uses to announce its plugin version (diagnostics only).</summary>
    public const string ClientVersionHeader = "X-Wyu2-Client";

    /// <summary>Longest a relay is allowed to retain a presence blob, in seconds.</summary>
    public const int MaxPresenceTtlSeconds = 900;

    /// <summary>Default presence lifetime, in seconds.</summary>
    public const int DefaultPresenceTtlSeconds = 120;

    /// <summary>Upper bound on a single encrypted presence blob, in bytes.</summary>
    public const int MaxEnvelopeBytes = 8 * 1024;

    /// <summary>Upper bound on recipients in a single publish call.</summary>
    public const int MaxRecipientsPerPublish = 200;

    /// <summary>Maximum length of a user supplied display label.</summary>
    public const int MaxDisplayNameLength = 48;

    /// <summary>Info string mixed into the key derivation so keys are unique to this application.</summary>
    public const string KeyDerivationInfo = "Wyu2/v1/presence";

    /// <summary>Maximum members in one group.</summary>
    public const int MaxGroupMembers = 64;

    /// <summary>Maximum groups one account may belong to.</summary>
    public const int MaxGroupsPerAccount = 16;

    /// <summary>Longest a group name may be.</summary>
    public const int MaxGroupNameLength = 40;
}
