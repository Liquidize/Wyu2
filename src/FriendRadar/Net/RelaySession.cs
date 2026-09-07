using System.Collections.Concurrent;
using FriendRadar.Protocol;

namespace FriendRadar.Net;

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

    public DateTime? ContactsRefreshedAt { get; private set; }

    public bool HasAccount => config.HasRelayAccount && keys is not null;

    /// <summary>Re-reads the private key out of the configuration, e.g. after registering.</summary>
    public void ReloadKeys()
    {
        keys?.Dispose();
        keys = null;
        outboundKeys.Clear();
        inboundKeys.Clear();

        if (string.IsNullOrEmpty(config.PrivateKey))
            return;

        try
        {
            keys = AccountKeyPair.Import(config.PrivateKey);
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "Stored FriendRadar key could not be loaded; the account must be re-created.");
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
        config.Save();

        Contacts = [];
        Requests = [];
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

        Contacts = contacts;
        Requests = requests;
        ContactsRefreshedAt = DateTime.UtcNow;

        var changed = config.EditContacts(list =>
        {
            var dirty = false;
            foreach (var contact in contacts)
            {
                var settings = list.FirstOrDefault(
                    c => string.Equals(c.AccountId, contact.AccountId, StringComparison.Ordinal));

                if (settings is null)
                {
                    list.Add(new Configuration.ContactSettings
                    {
                        AccountId = contact.AccountId,
                        DisplayName = contact.DisplayName,
                        PublicKey = contact.PublicKey,
                    });
                    dirty = true;
                    continue;
                }

                if (settings.DisplayName != contact.DisplayName)
                {
                    settings.DisplayName = contact.DisplayName;
                    dirty = true;
                }

                if (settings.PublicKey != contact.PublicKey)
                {
                    // Derived keys are cached under the public key they came from, so a rotation simply
                    // misses the cache rather than serving a stale secret.
                    settings.PublicKey = contact.PublicKey;
                    dirty = true;
                }
            }

            // Anybody the relay no longer lists has unlinked; drop their local settings too.
            var live = contacts.Select(c => c.AccountId).ToHashSet(StringComparer.Ordinal);
            return list.RemoveAll(c => !live.Contains(c.AccountId)) > 0 || dirty;
        });

        if (changed)
            config.Save();

        return true;
    }

    /// <summary>Key used to encrypt payloads we send to a contact.</summary>
    public byte[]? GetOutboundKey(string contactAccountId, string contactPublicKey)
        => GetKey(outboundKeys, contactAccountId, contactPublicKey, config.AccountId, contactAccountId);

    /// <summary>Key used to decrypt payloads a contact sent us.</summary>
    public byte[]? GetInboundKey(string contactAccountId, string contactPublicKey)
        => GetKey(inboundKeys, contactAccountId, contactPublicKey, contactAccountId, config.AccountId);

    private byte[]? GetKey(
        ConcurrentDictionary<string, byte[]> cache,
        string contactAccountId,
        string contactPublicKey,
        string senderId,
        string recipientId)
    {
        if (keys is null || string.IsNullOrEmpty(contactPublicKey) || string.IsNullOrEmpty(config.AccountId))
            return null;

        // The cache key includes the public key so a rotation cannot be served from a stale entry.
        var cacheKey = contactAccountId + "|" + contactPublicKey;
        if (cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var derived = PresenceCrypto.DeriveKey(keys, contactPublicKey, senderId, recipientId);
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
