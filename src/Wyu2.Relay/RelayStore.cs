using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wyu2.Protocol;
using Microsoft.Extensions.Options;

namespace Wyu2.Relay;

public enum StoreResult
{
    Ok,
    NotFound,
    AlreadyExists,
    LimitReached,
    Invalid,
    RegistrationClosed,
}

/// <summary>
/// The whole data layer. Accounts and links live in memory and are snapshotted to a JSON file; presence
/// blobs live in memory only and expire on their own. A single lock is plenty for the scale this runs at
/// (a guild, a friend group, a small community), and it keeps the consistency rules easy to check.
/// </summary>
public sealed class RelayStore
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, Account> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accountsByTokenHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accountsByShareCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContactRequest> requests = new(StringComparer.Ordinal);

    /// <summary>recipient id -> sender id -> blob.</summary>
    private readonly Dictionary<string, Dictionary<string, StoredPresence>> presence = new(StringComparer.Ordinal);

    private readonly RelayOptions options;
    private readonly ILogger<RelayStore> log;
    private readonly TimeProvider time;
    private readonly string snapshotPath;
    private bool dirty;

    public RelayStore(IOptions<RelayOptions> options, ILogger<RelayStore> log, TimeProvider time)
    {
        this.options = options.Value;
        this.log = log;
        this.time = time;
        Directory.CreateDirectory(this.options.DataDirectory);
        snapshotPath = Path.Combine(this.options.DataDirectory, "state.json");
        Load();
    }

    public int AccountCount
    {
        get
        {
            lock (sync)
                return accounts.Count;
        }
    }

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---------------------------------------------------------------- accounts

    public (StoreResult Result, RegisterResponse? Response) Register(RegisterRequest request, string? clientVersion)
    {
        var displayName = SanitizeDisplayName(request.DisplayName);
        if (displayName is null || !IsPlausiblePublicKey(request.PublicKey))
            return (StoreResult.Invalid, null);

        lock (sync)
        {
            // Blank entries are ignored: an unset environment variable must not become a code that
            // opens a closed relay.
            if (!options.RegistrationOpen && !options.InviteCodes.Any(
                    code => !string.IsNullOrWhiteSpace(code) &&
                            string.Equals(code, request.InviteCode, StringComparison.Ordinal)))
            {
                return (StoreResult.RegistrationClosed, null);
            }

            if (options.MaxAccounts > 0 && accounts.Count >= options.MaxAccounts)
                return (StoreResult.LimitReached, null);

            var token = GenerateToken();
            var account = new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = displayName,
                PublicKey = request.PublicKey,
                ShareCode = NewUniqueShareCode(),
                TokenHash = HashToken(token),
                CreatedAtUnixMs = Now,
                LastSeenAtUnixMs = Now,
                ClientVersion = clientVersion ?? request.ClientVersion,
            };

            accounts[account.Id] = account;
            accountsByTokenHash[account.TokenHash] = account.Id;
            accountsByShareCode[ShareCode.Normalize(account.ShareCode)!] = account.Id;
            dirty = true;

            log.LogInformation("Registered account {AccountId}", account.Id);

            return (StoreResult.Ok, new RegisterResponse
            {
                AccountId = account.Id,
                AccessToken = token,
                ShareCode = account.ShareCode,
            });
        }
    }

    /// <summary>Resolves a bearer token and refreshes the account's last-seen stamp.</summary>
    public Account? Authenticate(string token, string? clientVersion)
    {
        var hash = HashToken(token);
        lock (sync)
        {
            if (!accountsByTokenHash.TryGetValue(hash, out var id) || !accounts.TryGetValue(id, out var account))
                return null;

            account.LastSeenAtUnixMs = Now;
            if (!string.IsNullOrEmpty(clientVersion) && account.ClientVersion != clientVersion)
            {
                account.ClientVersion = clientVersion;
                dirty = true;
            }

            return account;
        }
    }

    public AccountInfo Describe(Account account)
    {
        lock (sync)
        {
            return new AccountInfo
            {
                AccountId = account.Id,
                DisplayName = account.DisplayName,
                ShareCode = account.ShareCode,
                PublicKey = account.PublicKey,
                CreatedAtUnixMs = account.CreatedAtUnixMs,
                ContactCount = account.Contacts.Count,
                PendingRequestCount = requests.Values.Count(r => r.ToAccountId == account.Id),
            };
        }
    }

    public StoreResult UpdateAccount(Account account, UpdateAccountRequest request)
    {
        lock (sync)
        {
            if (request.DisplayName is not null)
            {
                var name = SanitizeDisplayName(request.DisplayName);
                if (name is null)
                    return StoreResult.Invalid;

                account.DisplayName = name;
            }

            if (request.PublicKey is not null)
            {
                if (!IsPlausiblePublicKey(request.PublicKey))
                    return StoreResult.Invalid;

                account.PublicKey = request.PublicKey;

                // Every parked blob was sealed against the old key, in both directions: what others sent
                // us can no longer be opened, and what we sent them no longer matches the key they will
                // derive. Drop the lot rather than let clients chew on garbage.
                presence.Remove(account.Id);
                foreach (var bucket in presence.Values)
                    bucket.Remove(account.Id);
            }

            if (request.RotateShareCode)
            {
                accountsByShareCode.Remove(ShareCode.Normalize(account.ShareCode)!);
                account.ShareCode = NewUniqueShareCode();
                accountsByShareCode[ShareCode.Normalize(account.ShareCode)!] = account.Id;
            }

            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>Removes an account and everything anybody else holds about it.</summary>
    public void DeleteAccount(Account account)
    {
        lock (sync)
        {
            foreach (var contactId in account.Contacts)
            {
                if (accounts.TryGetValue(contactId, out var contact))
                    contact.Contacts.Remove(account.Id);
            }

            foreach (var stale in requests.Values
                         .Where(r => r.FromAccountId == account.Id || r.ToAccountId == account.Id)
                         .Select(r => r.Id)
                         .ToList())
            {
                requests.Remove(stale);
            }

            presence.Remove(account.Id);
            foreach (var bucket in presence.Values)
                bucket.Remove(account.Id);

            accountsByTokenHash.Remove(account.TokenHash);
            accountsByShareCode.Remove(ShareCode.Normalize(account.ShareCode)!);
            accounts.Remove(account.Id);
            dirty = true;

            log.LogInformation("Deleted account {AccountId}", account.Id);
        }
    }

    // ---------------------------------------------------------------- contacts

    public List<ContactDto> ListContacts(Account account)
    {
        lock (sync)
        {
            var result = new List<ContactDto>(account.Contacts.Count);
            foreach (var id in account.Contacts)
            {
                if (!accounts.TryGetValue(id, out var contact))
                    continue;

                long? lastPresence = null;
                if (presence.TryGetValue(account.Id, out var inbox) && inbox.TryGetValue(id, out var blob))
                    lastPresence = blob.ReceivedAtUnixMs;

                result.Add(new ContactDto
                {
                    AccountId = contact.Id,
                    DisplayName = contact.DisplayName,
                    PublicKey = contact.PublicKey,
                    LinkedAtUnixMs = contact.CreatedAtUnixMs,
                    LastPresenceAtUnixMs = lastPresence,
                });
            }

            return result;
        }
    }

    public List<ContactRequestDto> ListRequests(Account account)
    {
        lock (sync)
        {
            return requests.Values
                .Where(r => r.ToAccountId == account.Id || r.FromAccountId == account.Id)
                .Select(r =>
                {
                    var incoming = r.ToAccountId == account.Id;
                    var otherId = incoming ? r.FromAccountId : r.ToAccountId;
                    accounts.TryGetValue(otherId, out var other);
                    return new ContactRequestDto
                    {
                        RequestId = r.Id,
                        AccountId = otherId,
                        DisplayName = other?.DisplayName ?? "(unknown)",
                        PublicKey = other?.PublicKey ?? string.Empty,
                        Message = r.Message,
                        CreatedAtUnixMs = r.CreatedAtUnixMs,
                        Direction = incoming ? ContactRequestDirection.Incoming : ContactRequestDirection.Outgoing,
                    };
                })
                .OrderByDescending(r => r.CreatedAtUnixMs)
                .ToList();
        }
    }

    /// <summary>
    /// Sends an invite. If the other side already invited us this links both accounts immediately, which is
    /// the "we both typed each other's code" case.
    /// </summary>
    public (StoreResult Result, bool Linked, ContactRequestDto? Request) RequestContact(
        Account account, CreateContactRequest body)
    {
        var normalized = ShareCode.Normalize(body.ShareCode);
        if (normalized is null)
            return (StoreResult.Invalid, false, null);

        lock (sync)
        {
            if (!accountsByShareCode.TryGetValue(normalized, out var targetId) ||
                !accounts.TryGetValue(targetId, out var target))
            {
                return (StoreResult.NotFound, false, null);
            }

            if (target.Id == account.Id)
                return (StoreResult.Invalid, false, null);

            if (account.Contacts.Contains(target.Id))
                return (StoreResult.AlreadyExists, false, null);

            // Their invite is already waiting for us: accept it instead of stacking a second one.
            var reciprocal = requests.Values.FirstOrDefault(
                r => r.FromAccountId == target.Id && r.ToAccountId == account.Id);
            if (reciprocal is not null)
            {
                requests.Remove(reciprocal.Id);
                Link(account, target);
                return (StoreResult.Ok, true, null);
            }

            if (requests.Values.Any(r => r.FromAccountId == account.Id && r.ToAccountId == target.Id))
                return (StoreResult.AlreadyExists, false, null);

            if (account.Contacts.Count >= options.MaxContacts || target.Contacts.Count >= options.MaxContacts)
                return (StoreResult.LimitReached, false, null);

            if (requests.Values.Count(r => r.FromAccountId == account.Id) >= options.MaxPendingRequests ||
                requests.Values.Count(r => r.ToAccountId == target.Id) >= options.MaxPendingRequests)
            {
                return (StoreResult.LimitReached, false, null);
            }

            var request = new ContactRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                FromAccountId = account.Id,
                ToAccountId = target.Id,
                Message = Truncate(body.Message, 140),
                CreatedAtUnixMs = Now,
            };

            requests[request.Id] = request;
            dirty = true;

            return (StoreResult.Ok, false, new ContactRequestDto
            {
                RequestId = request.Id,
                AccountId = target.Id,
                DisplayName = target.DisplayName,
                PublicKey = target.PublicKey,
                Message = request.Message,
                CreatedAtUnixMs = request.CreatedAtUnixMs,
                Direction = ContactRequestDirection.Outgoing,
            });
        }
    }

    public StoreResult AcceptRequest(Account account, string requestId)
    {
        lock (sync)
        {
            if (!requests.TryGetValue(requestId, out var request) || request.ToAccountId != account.Id)
                return StoreResult.NotFound;

            if (!accounts.TryGetValue(request.FromAccountId, out var other))
            {
                requests.Remove(requestId);
                return StoreResult.NotFound;
            }

            if (account.Contacts.Count >= options.MaxContacts || other.Contacts.Count >= options.MaxContacts)
                return StoreResult.LimitReached;

            requests.Remove(requestId);
            Link(account, other);
            return StoreResult.Ok;
        }
    }

    /// <summary>Declines an incoming request, or withdraws one we sent.</summary>
    public StoreResult DeclineRequest(Account account, string requestId)
    {
        lock (sync)
        {
            if (!requests.TryGetValue(requestId, out var request) ||
                (request.ToAccountId != account.Id && request.FromAccountId != account.Id))
            {
                return StoreResult.NotFound;
            }

            requests.Remove(requestId);
            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>Unlinks both directions and forgets any presence already parked between the two.</summary>
    public StoreResult RemoveContact(Account account, string contactId)
    {
        lock (sync)
        {
            if (!account.Contacts.Remove(contactId))
                return StoreResult.NotFound;

            if (accounts.TryGetValue(contactId, out var other))
                other.Contacts.Remove(account.Id);

            if (presence.TryGetValue(account.Id, out var inbox))
                inbox.Remove(contactId);
            if (presence.TryGetValue(contactId, out var theirInbox))
                theirInbox.Remove(account.Id);

            dirty = true;
            return StoreResult.Ok;
        }
    }

    private void Link(Account a, Account b)
    {
        a.Contacts.Add(b.Id);
        b.Contacts.Add(a.Id);
        dirty = true;
        log.LogInformation("Linked {A} and {B}", a.Id, b.Id);
    }

    // ---------------------------------------------------------------- presence

    public PublishPresenceResponse PublishPresence(Account account, PublishPresenceRequest request)
    {
        var ttl = Math.Clamp(
            request.TtlSeconds <= 0 ? ProtocolConstants.DefaultPresenceTtlSeconds : request.TtlSeconds,
            5,
            ProtocolConstants.MaxPresenceTtlSeconds);

        var now = Now;
        var expires = now + (ttl * 1000L);
        var accepted = 0;
        var rejected = 0;

        lock (sync)
        {
            foreach (var envelope in request.Envelopes.Take(ProtocolConstants.MaxRecipientsPerPublish))
            {
                // Only mutual contacts may be addressed, and only sane sized blobs are parked.
                if (!account.Contacts.Contains(envelope.RecipientAccountId) ||
                    envelope.Ciphertext.Length > ProtocolConstants.MaxEnvelopeBytes ||
                    envelope.Nonce.Length > 32 ||
                    string.IsNullOrEmpty(envelope.Ciphertext))
                {
                    rejected++;
                    continue;
                }

                var inbox = presence.TryGetValue(envelope.RecipientAccountId, out var existing)
                    ? existing
                    : presence[envelope.RecipientAccountId] = new Dictionary<string, StoredPresence>(StringComparer.Ordinal);

                inbox[account.Id] = new StoredPresence
                {
                    SenderAccountId = account.Id,
                    RecipientAccountId = envelope.RecipientAccountId,
                    Nonce = envelope.Nonce,
                    Ciphertext = envelope.Ciphertext,
                    ReceivedAtUnixMs = now,
                    ExpiresAtUnixMs = expires,
                };
                accepted++;
            }

            return new PublishPresenceResponse
            {
                Accepted = accepted,
                Rejected = rejected,
                ServerTimeUnixMs = now,
                ActiveRecipients = account.Contacts
                    .Where(id => accounts.TryGetValue(id, out var c) && now - c.LastSeenAtUnixMs < 5 * 60_000)
                    .ToList(),
            };
        }
    }

    public FetchPresenceResponse FetchPresence(Account account)
    {
        var now = Now;
        lock (sync)
        {
            var response = new FetchPresenceResponse { ServerTimeUnixMs = now };
            if (!presence.TryGetValue(account.Id, out var inbox))
                return response;

            foreach (var (senderId, blob) in inbox.ToList())
            {
                if (blob.ExpiresAtUnixMs <= now || !account.Contacts.Contains(senderId))
                {
                    inbox.Remove(senderId);
                    continue;
                }

                accounts.TryGetValue(senderId, out var sender);
                response.Entries.Add(new ReceivedPresence
                {
                    SenderAccountId = senderId,
                    SenderDisplayName = sender?.DisplayName ?? "(unknown)",
                    Nonce = blob.Nonce,
                    Ciphertext = blob.Ciphertext,
                    ReceivedAtUnixMs = blob.ReceivedAtUnixMs,
                    ExpiresAtUnixMs = blob.ExpiresAtUnixMs,
                });
            }

            return response;
        }
    }

    /// <summary>Stops publishing to everybody at once, used by the plugin's panic switch.</summary>
    public void ClearOwnPresence(Account account)
    {
        lock (sync)
        {
            foreach (var bucket in presence.Values)
                bucket.Remove(account.Id);
        }
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Drops expired blobs, stale requests and long dormant accounts.</summary>
    public (int Blobs, int Requests, int Accounts) Prune()
    {
        var now = Now;
        var blobCutoff = now;
        var requestCutoff = now - (options.ContactRequestRetentionDays * 86_400_000L);
        var accountCutoff = now - (options.AccountRetentionDays * 86_400_000L);
        var droppedBlobs = 0;
        var droppedRequests = 0;
        var droppedAccounts = 0;

        lock (sync)
        {
            foreach (var (_, inbox) in presence)
            {
                foreach (var (sender, blob) in inbox.ToList())
                {
                    if (blob.ExpiresAtUnixMs <= blobCutoff)
                    {
                        inbox.Remove(sender);
                        droppedBlobs++;
                    }
                }
            }

            foreach (var request in requests.Values.Where(r => r.CreatedAtUnixMs < requestCutoff).ToList())
            {
                requests.Remove(request.Id);
                droppedRequests++;
            }

            if (options.AccountRetentionDays > 0)
            {
                foreach (var account in accounts.Values.Where(a => a.LastSeenAtUnixMs < accountCutoff).ToList())
                {
                    DeleteAccountLocked(account);
                    droppedAccounts++;
                }
            }
        }

        if (droppedRequests > 0 || droppedAccounts > 0)
            dirty = true;

        return (droppedBlobs, droppedRequests, droppedAccounts);
    }

    private void DeleteAccountLocked(Account account)
    {
        foreach (var contactId in account.Contacts)
        {
            if (accounts.TryGetValue(contactId, out var contact))
                contact.Contacts.Remove(account.Id);
        }

        foreach (var stale in requests.Values
                     .Where(r => r.FromAccountId == account.Id || r.ToAccountId == account.Id)
                     .Select(r => r.Id).ToList())
        {
            requests.Remove(stale);
        }

        presence.Remove(account.Id);
        foreach (var bucket in presence.Values)
            bucket.Remove(account.Id);

        accountsByTokenHash.Remove(account.TokenHash);
        accountsByShareCode.Remove(ShareCode.Normalize(account.ShareCode)!);
        accounts.Remove(account.Id);
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>Writes the account graph to disk if anything changed. Presence is deliberately excluded.</summary>
    public void Save(bool force = false)
    {
        RelaySnapshot snapshot;
        lock (sync)
        {
            if (!dirty && !force)
                return;

            snapshot = new RelaySnapshot
            {
                Accounts = accounts.Values.ToList(),
                Requests = requests.Values.ToList(),
            };
            dirty = false;
        }

        try
        {
            var temp = snapshotPath + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot, new JsonSerializerOptions { WriteIndented = false });
            }

            File.Move(temp, snapshotPath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write relay snapshot");
            lock (sync)
                dirty = true;
        }
    }

    private void Load()
    {
        if (!File.Exists(snapshotPath))
            return;

        try
        {
            using var stream = File.OpenRead(snapshotPath);
            var snapshot = JsonSerializer.Deserialize<RelaySnapshot>(stream);
            if (snapshot is null)
                return;

            foreach (var account in snapshot.Accounts)
            {
                accounts[account.Id] = account;
                accountsByTokenHash[account.TokenHash] = account.Id;
                var normalized = ShareCode.Normalize(account.ShareCode);
                if (normalized is not null)
                    accountsByShareCode[normalized] = account.Id;
            }

            foreach (var request in snapshot.Requests)
                requests[request.Id] = request;

            log.LogInformation(
                "Loaded {Accounts} accounts and {Requests} pending requests", accounts.Count, requests.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to read relay snapshot; starting empty");
        }
    }

    // ---------------------------------------------------------------- helpers

    private string NewUniqueShareCode()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var code = Protocol.ShareCode.Generate();
            var normalized = Protocol.ShareCode.Normalize(code)!;
            if (!accountsByShareCode.ContainsKey(normalized))
                return code;
        }

        throw new InvalidOperationException("Could not mint a unique share code.");
    }

    private static string GenerateToken()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string(name.Trim()
            .Where(c => !char.IsControl(c))
            .Take(ProtocolConstants.MaxDisplayNameLength)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = new string(value.Trim().Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }

    /// <summary>Cheap shape check so junk keys never reach the contact list of a real user.</summary>
    private static bool IsPlausiblePublicKey(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 512)
            return false;

        Span<byte> buffer = stackalloc byte[512];
        if (!Convert.TryFromBase64String(publicKey, buffer, out var written))
            return false;

        // A P-256 SubjectPublicKeyInfo is 91 bytes; allow a little slack for encoders.
        return written is >= 64 and <= 256;
    }
}
