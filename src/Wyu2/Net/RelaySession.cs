using System.Collections.Concurrent;
using Wyu2.Protocol;

namespace Wyu2.Net;

/// <summary>
/// Owns the account identity: the keypair, the relay credentials, the contact list and the derived
/// per-contact encryption keys. All of the network calls are async and safe to call off the framework
/// thread; the collections it exposes are replaced wholesale so readers never see a half-built list.
/// </summary>
public sealed class RelaySession : IDisposable
{
    private readonly Configuration.Configuration config;
    private readonly RelayClient client;
    private readonly ConcurrentDictionary<string, byte[]> outboundKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> inboundKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> outboundBeaconKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> inboundBeaconKeys = new(StringComparer.Ordinal);
    private AccountKeyPair? keys;

    public RelaySession(Configuration.Configuration config, RelayClient client)
    {
        this.config = config;
        this.client = client;
        ReloadKeys();
    }

    /// <summary>Contacts as the relay last reported them.</summary>
    public IReadOnlyList<ContactDto> Contacts { get; private set; } = [];

    /// <summary>Invites waiting in either direction.</summary>
    public IReadOnlyList<ContactRequestDto> Requests { get; private set; } = [];

    /// <summary>Groups as the relay last reported them.</summary>
    public IReadOnlyList<GroupDto> Groups { get; private set; } = [];

    public DateTime? ContactsRefreshedAt { get; private set; }

    public bool HasAccount => config.HasRelayAccount && keys is not null;

    /// <summary>Re-reads the private key out of the configuration, e.g. after registering.</summary>
    public void ReloadKeys()
    {
        keys?.Dispose();
        keys = null;
        outboundKeys.Clear();
        inboundKeys.Clear();
        outboundBeaconKeys.Clear();
        inboundBeaconKeys.Clear();

        if (string.IsNullOrEmpty(config.PrivateKey))
            return;

        try
        {
            keys = AccountKeyPair.Import(config.PrivateKey);
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "Stored Wyu2 key could not be loaded; the account must be re-created.");
        }
    }

    /// <summary>
    /// Creates an account on the configured relay and stores the credentials. Returns null on success or
    /// a message to show the user.
    /// </summary>
    public async Task<string?> RegisterAsync(string displayName, string? inviteCode, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(config.RelayUrl))
            return "Set a relay URL first.";

        if (string.IsNullOrWhiteSpace(displayName))
            return "Pick a display name first.";

        using var fresh = AccountKeyPair.Create();
        var response = await client.RegisterAsync(new RegisterRequest
        {
            DisplayName = displayName.Trim(),
            PublicKey = fresh.ExportPublicKeyBase64(),
            ClientVersion = Plugin.Version,
            InviteCode = string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim(),
        }, token).ConfigureAwait(false);

        if (response is null)
            return client.LastError ?? "The relay refused the registration.";

        config.AccountId = response.AccountId;
        config.AccessToken = response.AccessToken;
        config.ShareCode = response.ShareCode;
        config.PrivateKey = fresh.ExportPrivateKeyBase64();
        config.DisplayName = displayName.Trim();
        config.Save();

        client.Invalidate();
        ReloadKeys();
        await RefreshContactsAsync(token).ConfigureAwait(false);
        return null;
    }

    /// <summary>Deletes the relay account and forgets every local trace of it.</summary>
    public async Task<bool> DeleteAccountAsync(CancellationToken token = default)
    {
        var deleted = await client.DeleteAccountAsync(token).ConfigureAwait(false);
        ForgetLocalAccount();
        return deleted;
    }

    /// <summary>Clears local credentials without touching the relay (used when the token is already dead).</summary>
    public void ForgetLocalAccount()
    {
        config.AccountId = string.Empty;
        config.AccessToken = string.Empty;
        config.PrivateKey = string.Empty;
        config.ShareCode = string.Empty;
        config.EditContacts(contacts =>
        {
            contacts.Clear();
            return true;
        });
        config.Groups.Clear();
        config.Save();

        Contacts = [];
        Requests = [];
        Groups = [];
        client.Invalidate();
        ReloadKeys();
    }

    /// <summary>Pulls the contact list and pending invites, and mirrors them into the configuration.</summary>
    public async Task<bool> RefreshContactsAsync(CancellationToken token = default)
    {
        if (!HasAccount)
            return false;

        var contacts = await client.GetContactsAsync(token).ConfigureAwait(false);
        if (contacts is null)
            return false;

        var requests = await client.GetContactRequestsAsync(token).ConfigureAwait(false) ?? [];
        var groups = await client.GetGroupsAsync(token).ConfigureAwait(false) ?? [];

        Contacts = contacts;
        Requests = requests;
        Groups = groups;
        ContactsRefreshedAt = DateTime.UtcNow;

        var changed = MergeIntoConfiguration(contacts, groups);

        if (changed)
            config.Save();

        return true;
    }

    /// <summary>
    /// Folds the relay's view of who you can see into the local settings. Somebody may be reachable
    /// directly, through a group, or both, and that provenance is recorded so removing one link does not
    /// silently drop the other.
    /// </summary>
    private bool MergeIntoConfiguration(List<ContactDto> contacts, List<GroupDto> groups)
    {
        // Everybody the relay says we can see, and why.
        var reachable = new Dictionary<string, (string DisplayName, string PublicKey, bool Direct, List<string> Groups)>(
            StringComparer.Ordinal);

        foreach (var contact in contacts)
            reachable[contact.AccountId] = (contact.DisplayName, contact.PublicKey, true, []);

        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                if (string.Equals(member.AccountId, config.AccountId, StringComparison.Ordinal))
                    continue;

                if (reachable.TryGetValue(member.AccountId, out var existing))
                {
                    existing.Groups.Add(group.GroupId);
                    reachable[member.AccountId] = existing;
                }
                else
                {
                    reachable[member.AccountId] = (member.DisplayName, member.PublicKey, false, [group.GroupId]);
                }
            }
        }

        return config.EditContacts(list =>
        {
            var dirty = false;

            foreach (var (accountId, info) in reachable)
            {
                var settings = list.FirstOrDefault(
                    c => string.Equals(c.AccountId, accountId, StringComparison.Ordinal));

                if (settings is null)
                {
                    list.Add(new Configuration.ContactSettings
                    {
                        AccountId = accountId,
                        DisplayName = info.DisplayName,
                        PublicKey = info.PublicKey,
                        IsDirectContact = info.Direct,
                        GroupIds = info.Groups,
                    });
                    dirty = true;
                    continue;
                }

                if (settings.DisplayName != info.DisplayName)
                {
                    settings.DisplayName = info.DisplayName;
                    dirty = true;
                }

                if (settings.PublicKey != info.PublicKey)
                {
                    // Derived keys are cached under the public key they came from, so a rotation simply
                    // misses the cache rather than serving a stale secret.
                    settings.PublicKey = info.PublicKey;
                    dirty = true;
                }

                if (settings.IsDirectContact != info.Direct)
                {
                    settings.IsDirectContact = info.Direct;
                    dirty = true;
                }

                if (!settings.GroupIds.SequenceEqual(info.Groups, StringComparer.Ordinal))
                {
                    settings.GroupIds = info.Groups;
                    dirty = true;
                }
            }

            // Anybody the relay no longer lists has unlinked or left every shared group.
            if (list.RemoveAll(c => !reachable.ContainsKey(c.AccountId)) > 0)
                dirty = true;

            return dirty;
        }) | MergeGroupSettings(groups);
    }

    /// <summary>Keeps the local group settings in step with the relay's list.</summary>
    private bool MergeGroupSettings(List<GroupDto> groups)
    {
        var dirty = false;

        foreach (var group in groups)
        {
            var settings = config.FindGroup(group.GroupId);
            if (settings is null)
            {
                config.Groups.Add(new Configuration.GroupSettings
                {
                    GroupId = group.GroupId,
                    Name = group.Name,
                });
                dirty = true;
            }
            else if (settings.Name != group.Name)
            {
                settings.Name = group.Name;
                dirty = true;
            }
        }

        var live = groups.Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
        if (config.Groups.RemoveAll(g => !live.Contains(g.GroupId)) > 0)
            dirty = true;

        return dirty;
    }

    /// <summary>Key used to encrypt presence we send to a contact.</summary>
    public byte[]? GetOutboundKey(string contactAccountId, string contactPublicKey)
        => GetKey(outboundKeys, contactAccountId, contactPublicKey, config.AccountId, contactAccountId,
            ProtocolConstants.KeyDerivationInfo);

    /// <summary>Key used to decrypt presence a contact sent us.</summary>
    public byte[]? GetInboundKey(string contactAccountId, string contactPublicKey)
        => GetKey(inboundKeys, contactAccountId, contactPublicKey, contactAccountId, config.AccountId,
            ProtocolConstants.KeyDerivationInfo);

    /// <summary>
    /// Key used to encrypt beacons we send to a contact. Beacons derive from the same shared secret as
    /// presence but under their own purpose, so neither kind of message can be passed off as the other.
    /// </summary>
    public byte[]? GetOutboundBeaconKey(string contactAccountId, string contactPublicKey)
        => GetKey(outboundBeaconKeys, contactAccountId, contactPublicKey, config.AccountId, contactAccountId,
            ProtocolConstants.BeaconKeyDerivationInfo);

    /// <summary>Key used to decrypt beacons a contact dropped for us.</summary>
    public byte[]? GetInboundBeaconKey(string contactAccountId, string contactPublicKey)
        => GetKey(inboundBeaconKeys, contactAccountId, contactPublicKey, contactAccountId, config.AccountId,
            ProtocolConstants.BeaconKeyDerivationInfo);

    private byte[]? GetKey(
        ConcurrentDictionary<string, byte[]> cache,
        string contactAccountId,
        string contactPublicKey,
        string senderId,
        string recipientId,
        string purpose)
    {
        if (keys is null || string.IsNullOrEmpty(contactPublicKey) || string.IsNullOrEmpty(config.AccountId))
            return null;

        // The cache key includes the public key so a rotation cannot be served from a stale entry.
        var cacheKey = contactAccountId + "|" + contactPublicKey;
        if (cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var derived = PresenceCrypto.DeriveKey(keys, contactPublicKey, senderId, recipientId, purpose);
            cache[cacheKey] = derived;
            return derived;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not derive a key for contact {Contact}", contactAccountId);
            return null;
        }
    }

    public void Dispose() => keys?.Dispose();
}
