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
    private readonly ConcurrentDictionary<string, byte[]> derived = new(StringComparer.Ordinal);
    private AccountKeyPair? keys;
    private SigningKeyPair? signing;
    private EpochKeyRing? epochs;

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
        signing?.Dispose();
        epochs?.Dispose();
        keys = null;
        signing = null;
        epochs = null;
        derived.Clear();

        if (!string.IsNullOrEmpty(config.SigningPrivateKey))
        {
            try
            {
                signing = SigningKeyPair.Import(config.SigningPrivateKey);
            }
            catch (Exception ex)
            {
                Service.Log.Error(ex, "Stored Wyu2 signing key could not be loaded; a new one will be issued.");
                config.SigningPrivateKey = string.Empty;
            }
        }

        epochs = EpochKeyRing.Import(config.EpochKeys.Select(
            k => new EpochKeyRecord(k.Epoch, k.PrivateKey, k.CreatedAtUnixMs, k.ExpiresAtUnixMs)));

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
        using var freshSigning = SigningKeyPair.Create();
        var response = await client.RegisterAsync(new RegisterRequest
        {
            DisplayName = displayName.Trim(),
            PublicKey = fresh.ExportPublicKeyBase64(),
            SigningPublicKey = freshSigning.ExportPublicKeyBase64(),
            ClientVersion = Plugin.Version,
            InviteCode = string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim(),
        }, token).ConfigureAwait(false);

        if (response is null)
            return client.LastError ?? "The relay refused the registration.";

        config.AccountId = response.AccountId;
        config.AccessToken = response.AccessToken;
        config.ShareCode = response.ShareCode;
        config.PrivateKey = fresh.ExportPrivateKeyBase64();
        config.SigningPrivateKey = freshSigning.ExportPrivateKeyBase64();
        config.EpochKeys = [];
        config.PublishedEpoch = 0;
        config.DisplayName = displayName.Trim();
        config.Save();

        client.Invalidate();
        ReloadKeys();
        await EnsureForwardSecrecyAsync(token).ConfigureAwait(false);
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
        config.SigningPrivateKey = string.Empty;
        config.EpochKeys = [];
        config.PublishedEpoch = 0;
        config.PresenceSequence = 0;
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
        var reachable = new Dictionary<string, Reachable>(StringComparer.Ordinal);

        foreach (var contact in contacts)
        {
            reachable[contact.AccountId] = new Reachable(
                contact.DisplayName, contact.PublicKey, contact.SigningPublicKey, contact.Prekey, true, []);
        }

        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                if (string.Equals(member.AccountId, config.AccountId, StringComparison.Ordinal))
                    continue;

                if (reachable.TryGetValue(member.AccountId, out var existing))
                {
                    existing.Groups.Add(group.GroupId);
                }
                else
                {
                    reachable[member.AccountId] = new Reachable(
                        member.DisplayName, member.PublicKey, member.SigningPublicKey, member.Prekey,
                        false, [group.GroupId]);
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
                    settings = new Configuration.ContactSettings
                    {
                        AccountId = accountId,
                        DisplayName = info.DisplayName,
                        PublicKey = info.PublicKey,
                        SigningPublicKey = info.SigningPublicKey,
                        IsDirectContact = info.Direct,
                        GroupIds = info.Groups,
                    };

                    AdoptPrekey(settings, info);
                    list.Add(settings);
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

                if (settings.SigningPublicKey != info.SigningPublicKey &&
                    !string.IsNullOrEmpty(info.SigningPublicKey))
                {
                    settings.SigningPublicKey = info.SigningPublicKey;
                    dirty = true;
                }

                if (AdoptPrekey(settings, info))
                    dirty = true;
            }

            // Anybody the relay no longer lists has unlinked or left every shared group.
            if (list.RemoveAll(c => !reachable.ContainsKey(c.AccountId)) > 0)
                dirty = true;

            return dirty;
        }) | MergeGroupSettings(groups);
    }

    /// <summary>Everything the relay says about somebody we can see, and why we can see them.</summary>
    private readonly record struct Reachable(
        string DisplayName,
        string PublicKey,
        string SigningPublicKey,
        PrekeyBundle? Prekey,
        bool Direct,
        List<string> Groups);

    /// <summary>
    /// Takes a contact's newly published epoch key, but only after checking it was signed by the identity
    /// key we already hold for them. The relay is the one handing this over, and an unchecked bundle
    /// would let it substitute a key of its own and read everything addressed to them.
    ///
    /// The outgoing epoch is kept alongside the new one: a payload sealed moments before they rotated
    /// arrives after we have already seen the replacement, and without the predecessor it could not be
    /// opened.
    /// </summary>
    private static bool AdoptPrekey(Configuration.ContactSettings settings, Reachable info)
    {
        if (info.Prekey is not { } bundle || !bundle.IsPresent)
            return false;

        if (bundle.Epoch <= settings.PrekeyEpoch)
            return false;

        if (!bundle.Verify(settings.AccountId, info.SigningPublicKey))
        {
            Service.Log.Warning(
                "Ignoring an epoch key for {Contact} that is not signed by their identity key.",
                settings.AccountId);
            return false;
        }

        settings.PreviousPrekeyEpoch = settings.PrekeyEpoch;
        settings.PreviousPrekeyPublicKey = settings.PrekeyPublicKey;
        settings.PrekeyEpoch = bundle.Epoch;
        settings.PrekeyPublicKey = bundle.EpochPublicKey;
        settings.SupportsForwardSecrecy = true;
        return true;
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

    // ---------------------------------------------------------------- key material

    /// <summary>
    /// Ensures this account has a signing key and a current epoch key, upgrading an account created
    /// before forward secrecy in place rather than making the user abandon it. Called from the periodic
    /// contact sync, so a failed publish is simply retried a minute later.
    /// </summary>
    public async Task EnsureForwardSecrecyAsync(CancellationToken token = default)
    {
        if (!HasAccount)
            return;

        var changed = false;

        if (signing is null)
        {
            var created = SigningKeyPair.Create();
            var updated = await client.UpdateAccountAsync(
                new UpdateAccountRequest { SigningPublicKey = created.ExportPublicKeyBase64() }, token)
                .ConfigureAwait(false);

            if (updated is null)
            {
                created.Dispose();
                return;
            }

            signing = created;
            config.SigningPrivateKey = created.ExportPrivateKeyBase64();
            changed = true;
        }

        epochs ??= new EpochKeyRing();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (epochs.NeedsRotation(now))
        {
            epochs.Rotate(signing, config.AccountId, now, ProtocolConstants.EpochLifetimeMs);
            StoreEpochs();
            derived.Clear();
            changed = true;
        }

        // Seal under the newest epoch the relay has actually taken. Rotating locally is cheap, but a key
        // contacts have never been offered is a key they cannot decrypt with, so the two are tracked
        // separately and publishing is retried until it lands.
        if (config.PublishedEpoch != epochs.CurrentEpoch && BundleFor(epochs.CurrentEpoch) is { } bundle)
        {
            if (await client.PublishPrekeyAsync(bundle, token).ConfigureAwait(false))
            {
                config.PublishedEpoch = bundle.Epoch;
                changed = true;
            }
        }

        if (changed)
            config.Save();
    }

    /// <summary>
    /// Rebuilds the published bundle for an epoch we still hold. Signatures are randomised, so a fresh
    /// one over the same canonical bytes is just as valid as the original; there is nothing to keep.
    /// </summary>
    private PrekeyBundle? BundleFor(int epoch)
    {
        if (signing is null || epochs is null || epoch <= 0 || !epochs.TryGet(epoch, out var key))
            return null;

        var record = epochs.Export().FirstOrDefault(r => r.Epoch == epoch);
        return record is null
            ? null
            : PrekeyBundle.Create(signing, config.AccountId, epoch, key, record.CreatedAtUnixMs, record.ExpiresAtUnixMs);
    }

    private void StoreEpochs()
    {
        config.EpochKeys = epochs is null
            ? []
            : epochs.Export()
                .Select(r => new Configuration.StoredEpochKey
                {
                    Epoch = r.Epoch,
                    PrivateKey = r.PrivateKeyBase64,
                    CreatedAtUnixMs = r.CreatedAtUnixMs,
                    ExpiresAtUnixMs = r.ExpiresAtUnixMs,
                })
                .ToList();
    }

    /// <summary>A key to seal with, and the epochs it was derived under so the envelope can say so.</summary>
    public readonly record struct SealingKey(byte[] Key, int SenderEpoch, int RecipientEpoch);

    /// <summary>
    /// The key for something we are sending to a contact. Prefers the rotating epoch keys and refuses to
    /// fall back for anybody who has ever published one: otherwise a relay that simply stopped serving
    /// bundles could push an established pair back onto long-term keys and quietly take forward secrecy
    /// away from both of them.
    /// </summary>
    public SealingKey? GetOutboundKey(Configuration.ContactSettings contact, string purpose)
    {
        if (keys is null || string.IsNullOrEmpty(config.AccountId))
            return null;

        var myEpoch = config.PublishedEpoch;
        if (myEpoch > 0 && contact.PrekeyEpoch > 0 && contact.PrekeyPublicKey.Length > 0 &&
            epochs is not null && epochs.TryGet(myEpoch, out var mine))
        {
            var key = Derive(
                mine, contact.PrekeyPublicKey, config.AccountId, contact.AccountId,
                myEpoch, contact.PrekeyEpoch, purpose, contact.AccountId);

            return key is null ? null : new SealingKey(key, myEpoch, contact.PrekeyEpoch);
        }

        if (contact.SupportsForwardSecrecy)
        {
            Service.Log.Warning(
                "Not sealing to {Contact} with long-term keys: they have published an epoch key before, " +
                "so falling back would be a downgrade.", contact.AccountId);
            return null;
        }

        var legacy = DeriveLegacy(contact.PublicKey, config.AccountId, contact.AccountId, purpose, contact.AccountId);
        return legacy is null ? null : new SealingKey(legacy, 0, 0);
    }

    /// <summary>
    /// The key for something a contact sent us. A miss because our epoch key is gone is the intended
    /// outcome for anything old, not a failure: it is forward secrecy working.
    /// </summary>
    public byte[]? GetInboundKey(
        Configuration.ContactSettings contact,
        int senderEpoch,
        int recipientEpoch,
        string purpose)
    {
        if (keys is null || string.IsNullOrEmpty(config.AccountId))
            return null;

        if (senderEpoch > 0 && recipientEpoch > 0)
        {
            if (epochs is null || !epochs.TryGet(recipientEpoch, out var mine))
                return null;

            var theirs = contact.PrekeyEpoch == senderEpoch
                ? contact.PrekeyPublicKey
                : contact.PreviousPrekeyEpoch == senderEpoch
                    ? contact.PreviousPrekeyPublicKey
                    : null;

            if (string.IsNullOrEmpty(theirs))
                return null;

            return Derive(
                mine, theirs, contact.AccountId, config.AccountId,
                senderEpoch, recipientEpoch, purpose, contact.AccountId);
        }

        if (contact.SupportsForwardSecrecy)
            return null;

        return DeriveLegacy(contact.PublicKey, contact.AccountId, config.AccountId, purpose, contact.AccountId);
    }

    private byte[]? Derive(
        AccountKeyPair mine,
        string theirEpochPublicKey,
        string senderId,
        string recipientId,
        int senderEpoch,
        int recipientEpoch,
        string purpose,
        string contactAccountId)
    {
        var cacheKey = $"{purpose}|{senderId}|{recipientId}|{senderEpoch}|{recipientEpoch}|{theirEpochPublicKey}";
        if (derived.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var key = PresenceCrypto.DeriveEpochKey(
                mine, theirEpochPublicKey, senderId, recipientId, senderEpoch, recipientEpoch, purpose);

            derived[cacheKey] = key;
            return key;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not derive an epoch key for contact {Contact}", contactAccountId);
            return null;
        }
    }

    private byte[]? DeriveLegacy(
        string contactPublicKey,
        string senderId,
        string recipientId,
        string purpose,
        string contactAccountId)
    {
        if (keys is null || string.IsNullOrEmpty(contactPublicKey))
            return null;

        // The cache key includes the public key so a rotation cannot be served from a stale entry.
        var cacheKey = $"legacy|{purpose}|{senderId}|{recipientId}|{contactPublicKey}";
        if (derived.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var key = PresenceCrypto.DeriveKey(keys, contactPublicKey, senderId, recipientId, purpose);
            derived[cacheKey] = key;
            return key;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not derive a key for contact {Contact}", contactAccountId);
            return null;
        }
    }

    public void Dispose()
    {
        keys?.Dispose();
        signing?.Dispose();
        epochs?.Dispose();
    }
}
