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

    /// <summary>An epoch key was offered before the account had a signing key to vouch for it.</summary>
    NoSigningKey,

    /// <summary>An epoch key that does not advance on the one already published.</summary>
    StaleEpoch,
}

/// <summary>
/// The whole data layer. Accounts and links live in memory and are snapshotted to a JSON file; presence
/// blobs and beacons live in memory only and expire on their own. A single lock is plenty for the scale
/// this runs at (a guild, a friend group, a small community), and it keeps the consistency rules easy to
/// check.
/// </summary>
public sealed class RelayStore
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, Account> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accountsByTokenHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accountsByShareCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContactRequest> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> groupsByJoinCode = new(StringComparer.Ordinal);

    /// <summary>recipient id -> sender id -> blob.</summary>
    private readonly Dictionary<string, Dictionary<string, StoredPresence>> presence = new(StringComparer.Ordinal);

    /// <summary>recipient id -> "sender id|beacon id" -> beacon. Several per sender, unlike presence.</summary>
    private readonly Dictionary<string, Dictionary<string, StoredBeacon>> beacons = new(StringComparer.Ordinal);

    /// <summary>Longest sender-chosen beacon id the relay will store. A GUID is well inside this.</summary>
    private const int MaxBeaconIdLength = 64;

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

        // A blank signing key is not an error: a client from before forward secrecy has none to send,
        // and refusing it here would lock those users out of a relay they already use.
        if (request.SigningPublicKey.Length > 0 && !IsPlausiblePublicKey(request.SigningPublicKey))
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
                SigningPublicKey = request.SigningPublicKey,
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
                SigningPublicKey = account.SigningPublicKey,
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

                beacons.Remove(account.Id);
                foreach (var bucket in beacons.Values)
                    DropBeaconsFromLocked(bucket, account.Id);
            }

            if (request.SigningPublicKey is not null)
            {
                if (!IsPlausiblePublicKey(request.SigningPublicKey))
                    return StoreResult.Invalid;

                // Deliberately no parked blob is dropped here, unlike a change of ECDH key. A signing
                // key seals nothing; it only vouches for epoch keys, so everything already parked stays
                // exactly as readable as it was.
                account.SigningPublicKey = request.SigningPublicKey;
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

    /// <summary>
    /// Records the epoch key an account wants contacts to encrypt to. Reports
    /// <see cref="StoreResult.NotFound"/> when the account has no signing key to check the bundle
    /// against, <see cref="StoreResult.Invalid"/> when it does not verify, and
    /// <see cref="StoreResult.AlreadyExists"/> when a newer epoch is already published.
    /// </summary>
    public StoreResult PublishPrekey(Account account, PublishPrekeyRequest request)
    {
        var bundle = request.Bundle;

        lock (sync)
        {
            if (account.SigningPublicKey.Length == 0)
                return StoreResult.NoSigningKey;

            if (!bundle.IsPresent || !IsPlausiblePublicKey(bundle.EpochPublicKey))
                return StoreResult.Invalid;

            // Checking the signature here is a cheap filter against garbage, not a security control.
            // The relay is not trusted, so it proves nothing to the people who matter: every client
            // verifies each bundle against the contact's identity key itself before encrypting to it.
            if (!bundle.Verify(account.Id, account.SigningPublicKey))
                return StoreResult.Invalid;

            // Refusing an epoch that does not advance stops anybody who captured an old bundle from
            // replaying it to drag a contact back onto a key whose private half may already be known.
            if (account.Prekey is not null && bundle.Epoch <= account.Prekey.Epoch)
                return StoreResult.StaleEpoch;

            account.Prekey = bundle;
            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>Removes an account and everything anybody else holds about it.</summary>
    public void DeleteAccount(Account account)
    {
        lock (sync)
        {
            RemoveFromGroupsLocked(account.Id);

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

            beacons.Remove(account.Id);
            foreach (var bucket in beacons.Values)
                DropBeaconsFromLocked(bucket, account.Id);

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
                    SigningPublicKey = contact.SigningPublicKey,
                    Prekey = contact.Prekey,
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

    /// <summary>
    /// Unlinks both directions and forgets anything already parked between the two, presence and beacons
    /// alike. Somebody you have just unlinked from should not still have a marker of yours on their map.
    /// </summary>
    public StoreResult RemoveContact(Account account, string contactId)
    {
        lock (sync)
        {
            if (!account.Contacts.Remove(contactId))
                return StoreResult.NotFound;

            if (accounts.TryGetValue(contactId, out var other))
                other.Contacts.Remove(account.Id);

            // A shared group can still link the two, and then neither of them has lost sight of the
            // other, so nothing is dropped.
            if (!VisibleToLocked(account).Contains(contactId))
                DropParkedBetweenLocked(account.Id, contactId);

            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>Forgets everything parked in either direction between two accounts.</summary>
    private void DropParkedBetweenLocked(string a, string b)
    {
        if (presence.TryGetValue(a, out var ourInbox))
            ourInbox.Remove(b);
        if (presence.TryGetValue(b, out var theirInbox))
            theirInbox.Remove(a);

        if (beacons.TryGetValue(a, out var ourBeacons))
            DropBeaconsFromLocked(ourBeacons, b);
        if (beacons.TryGetValue(b, out var theirBeacons))
            DropBeaconsFromLocked(theirBeacons, a);
    }

    /// <summary>Removes every beacon one sender has parked in a single inbox.</summary>
    private static void DropBeaconsFromLocked(Dictionary<string, StoredBeacon> inbox, string senderId)
    {
        foreach (var (key, beacon) in inbox.ToList())
        {
            if (string.Equals(beacon.SenderAccountId, senderId, StringComparison.Ordinal))
                inbox.Remove(key);
        }
    }

    private void Link(Account a, Account b)
    {
        a.Contacts.Add(b.Id);
        b.Contacts.Add(a.Id);
        dirty = true;
        log.LogInformation("Linked {A} and {B}", a.Id, b.Id);
    }

    // ---------------------------------------------------------------- groups

    /// <summary>
    /// Everyone this account may exchange presence with: mutual contacts plus everybody sharing a group
    /// with them. Built once per call rather than asked per recipient, since a publish can address a
    /// couple of hundred people.
    /// </summary>
    private HashSet<string> VisibleToLocked(Account account)
    {
        var visible = new HashSet<string>(account.Contacts, StringComparer.Ordinal);

        foreach (var group in groups.Values)
        {
            if (!group.Members.ContainsKey(account.Id))
                continue;

            foreach (var member in group.Members.Keys)
            {
                if (!string.Equals(member, account.Id, StringComparison.Ordinal))
                    visible.Add(member);
            }
        }

        return visible;
    }

    public List<GroupDto> ListGroups(Account account)
    {
        lock (sync)
        {
            return groups.Values
                .Where(g => g.Members.ContainsKey(account.Id))
                .Select(DescribeLocked)
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public (StoreResult Result, GroupDto? Group) CreateGroup(Account account, CreateGroupRequest request)
    {
        var name = SanitizeGroupName(request.Name);
        if (name is null)
            return (StoreResult.Invalid, null);

        lock (sync)
        {
            if (groups.Values.Count(g => g.Members.ContainsKey(account.Id)) >= options.MaxGroups)
                return (StoreResult.LimitReached, null);

            var group = new Group
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                OwnerAccountId = account.Id,
                JoinCode = NewUniqueJoinCode(),
                CreatedAtUnixMs = Now,
            };

            group.Members[account.Id] = Now;
            groups[group.Id] = group;
            groupsByJoinCode[ShareCode.Normalize(group.JoinCode)!] = group.Id;
            dirty = true;

            log.LogInformation("Account {AccountId} created group {GroupId}", account.Id, group.Id);
            return (StoreResult.Ok, DescribeLocked(group));
        }
    }

    public (StoreResult Result, GroupDto? Group) JoinGroup(Account account, JoinGroupRequest request)
    {
        var normalized = ShareCode.Normalize(request.JoinCode);
        if (normalized is null)
            return (StoreResult.Invalid, null);

        lock (sync)
        {
            if (!groupsByJoinCode.TryGetValue(normalized, out var groupId) ||
                !groups.TryGetValue(groupId, out var group))
            {
                return (StoreResult.NotFound, null);
            }

            if (group.Members.ContainsKey(account.Id))
                return (StoreResult.AlreadyExists, DescribeLocked(group));

            if (group.Members.Count >= options.MaxGroupMembers)
                return (StoreResult.LimitReached, null);

            if (groups.Values.Count(g => g.Members.ContainsKey(account.Id)) >= options.MaxGroups)
                return (StoreResult.LimitReached, null);

            group.Members[account.Id] = Now;
            dirty = true;

            log.LogInformation("Account {AccountId} joined group {GroupId}", account.Id, group.Id);
            return (StoreResult.Ok, DescribeLocked(group));
        }
    }

    public StoreResult UpdateGroup(Account account, string groupId, UpdateGroupRequest request)
    {
        lock (sync)
        {
            if (!groups.TryGetValue(groupId, out var group) || !group.Members.ContainsKey(account.Id))
                return StoreResult.NotFound;

            if (!string.Equals(group.OwnerAccountId, account.Id, StringComparison.Ordinal))
                return StoreResult.Invalid;

            if (request.Name is not null)
            {
                var name = SanitizeGroupName(request.Name);
                if (name is null)
                    return StoreResult.Invalid;

                group.Name = name;
            }

            if (request.RotateJoinCode)
            {
                groupsByJoinCode.Remove(ShareCode.Normalize(group.JoinCode)!);
                group.JoinCode = NewUniqueJoinCode();
                groupsByJoinCode[ShareCode.Normalize(group.JoinCode)!] = group.Id;
            }

            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>Leaves a group, or removes somebody else from it when the caller owns it.</summary>
    public StoreResult LeaveGroup(Account account, string groupId, string? memberId = null)
    {
        lock (sync)
        {
            if (!groups.TryGetValue(groupId, out var group) || !group.Members.ContainsKey(account.Id))
                return StoreResult.NotFound;

            var target = memberId ?? account.Id;
            var removingSomebodyElse = !string.Equals(target, account.Id, StringComparison.Ordinal);

            if (removingSomebodyElse && !string.Equals(group.OwnerAccountId, account.Id, StringComparison.Ordinal))
                return StoreResult.Invalid;

            if (!group.Members.Remove(target))
                return StoreResult.NotFound;

            DropParkedOnLeavingLocked(target, group);
            HandOverOrDisbandLocked(group, target);
            dirty = true;
            return StoreResult.Ok;
        }
    }

    /// <summary>
    /// Keeps a group with an owner. When the last member leaves the group goes with them rather than
    /// lingering with a join code nobody owns.
    /// </summary>
    private void HandOverOrDisbandLocked(Group group, string departedId)
    {
        if (group.Members.Count == 0)
        {
            groupsByJoinCode.Remove(ShareCode.Normalize(group.JoinCode)!);
            groups.Remove(group.Id);
            log.LogInformation("Group {GroupId} disbanded", group.Id);
            return;
        }

        if (!string.Equals(group.OwnerAccountId, departedId, StringComparison.Ordinal))
            return;

        // The longest standing member inherits it.
        group.OwnerAccountId = group.Members.OrderBy(m => m.Value).First().Key;
        log.LogInformation("Group {GroupId} handed to {AccountId}", group.Id, group.OwnerAccountId);
    }

    /// <summary>
    /// Drops whatever is parked between somebody leaving a group and the members they can no longer see,
    /// unless they are still linked some other way. Beacons go with presence: a marker you dropped for a
    /// static should not outlive your membership of it.
    /// </summary>
    private void DropParkedOnLeavingLocked(string departedId, Group group)
    {
        if (!accounts.TryGetValue(departedId, out var departed))
            return;

        var stillVisible = VisibleToLocked(departed);

        foreach (var memberId in group.Members.Keys)
        {
            if (!stillVisible.Contains(memberId))
                DropParkedBetweenLocked(departedId, memberId);
        }
    }

    private GroupDto DescribeLocked(Group group) => new()
    {
        GroupId = group.Id,
        Name = group.Name,
        JoinCode = group.JoinCode,
        OwnerAccountId = group.OwnerAccountId,
        CreatedAtUnixMs = group.CreatedAtUnixMs,
        Members = group.Members
            .Select(m =>
            {
                accounts.TryGetValue(m.Key, out var member);
                return new GroupMemberDto
                {
                    AccountId = m.Key,
                    DisplayName = member?.DisplayName ?? "(unknown)",
                    PublicKey = member?.PublicKey ?? string.Empty,
                    SigningPublicKey = member?.SigningPublicKey ?? string.Empty,
                    Prekey = member?.Prekey,
                    JoinedAtUnixMs = m.Value,
                };
            })
            .Where(m => m.PublicKey.Length > 0)
            .OrderBy(m => m.JoinedAtUnixMs)
            .ToList(),
    };

    private string NewUniqueJoinCode()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var code = Protocol.ShareCode.Generate();
            if (!groupsByJoinCode.ContainsKey(Protocol.ShareCode.Normalize(code)!))
                return code;
        }

        throw new InvalidOperationException("Could not mint a unique join code.");
    }

    private static string? SanitizeGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string(name.Trim()
            .Where(c => !char.IsControl(c))
            .Take(ProtocolConstants.MaxGroupNameLength)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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
            var visible = VisibleToLocked(account);

            foreach (var envelope in request.Envelopes.Take(ProtocolConstants.MaxRecipientsPerPublish))
            {
                // Only people you are linked with - a mutual contact or a fellow group member - may be
                // addressed, and only sane sized blobs are parked.
                if (!visible.Contains(envelope.RecipientAccountId) ||
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
                    SenderEpoch = envelope.SenderEpoch,
                    RecipientEpoch = envelope.RecipientEpoch,
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
                ActiveRecipients = visible
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

            var visible = VisibleToLocked(account);

            foreach (var (senderId, blob) in inbox.ToList())
            {
                if (blob.ExpiresAtUnixMs <= now || !visible.Contains(senderId))
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
                    SenderEpoch = blob.SenderEpoch,
                    RecipientEpoch = blob.RecipientEpoch,
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

    // ---------------------------------------------------------------- beacons

    /// <summary>
    /// Parks a beacon with everybody it is addressed to. The sender's own id for it decides whether this
    /// replaces one of their existing beacons or adds another, and each sender is held to
    /// <see cref="ProtocolConstants.MaxBeaconsPerSender"/> per recipient so nobody can paper somebody
    /// else's map with markers.
    /// </summary>
    public PublishBeaconResponse PublishBeacon(Account account, PublishBeaconRequest request)
    {
        var now = Now;
        var beaconId = SanitizeBeaconId(request.BeaconId);
        if (beaconId is null)
            return new PublishBeaconResponse { Rejected = request.Envelopes.Count, ServerTimeUnixMs = now };

        var ttl = Math.Clamp(
            request.TtlSeconds <= 0 ? ProtocolConstants.DefaultBeaconTtlSeconds : request.TtlSeconds,
            30,
            ProtocolConstants.MaxBeaconTtlSeconds);

        var expires = now + (ttl * 1000L);
        var accepted = 0;
        var rejected = 0;

        lock (sync)
        {
            var visible = VisibleToLocked(account);

            foreach (var envelope in request.Envelopes.Take(ProtocolConstants.MaxRecipientsPerPublish))
            {
                if (!visible.Contains(envelope.RecipientAccountId) ||
                    envelope.Ciphertext.Length > ProtocolConstants.MaxEnvelopeBytes ||
                    envelope.Nonce.Length > 32 ||
                    string.IsNullOrEmpty(envelope.Ciphertext))
                {
                    rejected++;
                    continue;
                }

                var inbox = beacons.TryGetValue(envelope.RecipientAccountId, out var existing)
                    ? existing
                    : beacons[envelope.RecipientAccountId] = new Dictionary<string, StoredBeacon>(StringComparer.Ordinal);

                inbox[BeaconKey(account.Id, beaconId)] = new StoredBeacon
                {
                    BeaconId = beaconId,
                    SenderAccountId = account.Id,
                    RecipientAccountId = envelope.RecipientAccountId,
                    Nonce = envelope.Nonce,
                    Ciphertext = envelope.Ciphertext,
                    SenderEpoch = envelope.SenderEpoch,
                    RecipientEpoch = envelope.RecipientEpoch,
                    ReceivedAtUnixMs = now,
                    ExpiresAtUnixMs = expires,
                };

                TrimToCapLocked(inbox, account.Id);
                accepted++;
            }

            return new PublishBeaconResponse
            {
                Accepted = accepted,
                Rejected = rejected,
                ServerTimeUnixMs = now,
            };
        }
    }

    /// <summary>Everything currently addressed to this account that has not expired.</summary>
    public FetchBeaconsResponse FetchBeacons(Account account)
    {
        var now = Now;
        lock (sync)
        {
            var response = new FetchBeaconsResponse { ServerTimeUnixMs = now };
            if (!beacons.TryGetValue(account.Id, out var inbox))
                return response;

            var visible = VisibleToLocked(account);

            foreach (var (key, beacon) in inbox.ToList())
            {
                if (beacon.ExpiresAtUnixMs <= now || !visible.Contains(beacon.SenderAccountId))
                {
                    inbox.Remove(key);
                    continue;
                }

                accounts.TryGetValue(beacon.SenderAccountId, out var sender);
                response.Entries.Add(new ReceivedBeacon
                {
                    BeaconId = beacon.BeaconId,
                    SenderAccountId = beacon.SenderAccountId,
                    SenderDisplayName = sender?.DisplayName ?? "(unknown)",
                    Nonce = beacon.Nonce,
                    Ciphertext = beacon.Ciphertext,
                    SenderEpoch = beacon.SenderEpoch,
                    RecipientEpoch = beacon.RecipientEpoch,
                    ReceivedAtUnixMs = beacon.ReceivedAtUnixMs,
                    ExpiresAtUnixMs = beacon.ExpiresAtUnixMs,
                });
            }

            return response;
        }
    }

    /// <summary>
    /// Takes one of your beacons back from every recipient it reached. Reports success as long as the id
    /// was well formed, since a beacon that has already expired everywhere is not a failure to withdraw.
    /// </summary>
    public StoreResult WithdrawBeacon(Account account, string beaconId)
    {
        var id = SanitizeBeaconId(beaconId);
        if (id is null)
            return StoreResult.Invalid;

        var key = BeaconKey(account.Id, id);
        lock (sync)
        {
            foreach (var inbox in beacons.Values)
                inbox.Remove(key);

            return StoreResult.Ok;
        }
    }

    private static string BeaconKey(string senderId, string beaconId) => senderId + "|" + beaconId;

    /// <summary>Keeps one sender to their share of an inbox, dropping their oldest beacon first.</summary>
    private static void TrimToCapLocked(Dictionary<string, StoredBeacon> inbox, string senderId)
    {
        while (true)
        {
            var mine = inbox
                .Where(entry => string.Equals(entry.Value.SenderAccountId, senderId, StringComparison.Ordinal))
                .ToList();

            if (mine.Count <= ProtocolConstants.MaxBeaconsPerSender)
                return;

            inbox.Remove(mine.OrderBy(entry => entry.Value.ReceivedAtUnixMs).First().Key);
        }
    }

    /// <summary>
    /// Beacon ids are chosen by the client, so they are treated as untrusted text: printable, bounded,
    /// and free of the separator the inbox keys are built from.
    /// </summary>
    private static string? SanitizeBeaconId(string? beaconId)
    {
        if (string.IsNullOrWhiteSpace(beaconId) || beaconId.Length > MaxBeaconIdLength)
            return null;

        return beaconId.Any(c => char.IsControl(c) || c == '|') ? null : beaconId;
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Drops expired blobs and beacons, stale requests and long dormant accounts.</summary>
    public (int Blobs, int Beacons, int Requests, int Accounts) Prune()
    {
        var now = Now;
        var blobCutoff = now;
        var requestCutoff = now - (options.ContactRequestRetentionDays * 86_400_000L);
        var accountCutoff = now - (options.AccountRetentionDays * 86_400_000L);
        var droppedBlobs = 0;
        var droppedBeacons = 0;
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

            foreach (var (_, inbox) in beacons)
            {
                foreach (var (key, beacon) in inbox.ToList())
                {
                    if (beacon.ExpiresAtUnixMs <= blobCutoff)
                    {
                        inbox.Remove(key);
                        droppedBeacons++;
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

        return (droppedBlobs, droppedBeacons, droppedRequests, droppedAccounts);
    }

    private void DeleteAccountLocked(Account account)
    {
        RemoveFromGroupsLocked(account.Id);

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

        beacons.Remove(account.Id);
        foreach (var bucket in beacons.Values)
            DropBeaconsFromLocked(bucket, account.Id);

        accountsByTokenHash.Remove(account.TokenHash);
        accountsByShareCode.Remove(ShareCode.Normalize(account.ShareCode)!);
        accounts.Remove(account.Id);
    }

    /// <summary>Takes an account out of every group it belongs to, handing over or disbanding as needed.</summary>
    private void RemoveFromGroupsLocked(string accountId)
    {
        foreach (var group in groups.Values.Where(g => g.Members.ContainsKey(accountId)).ToList())
        {
            group.Members.Remove(accountId);
            HandOverOrDisbandLocked(group, accountId);
        }
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>
    /// Writes the account graph to disk if anything changed. Presence and beacons are deliberately
    /// excluded: neither outlives the process that is holding them.
    /// </summary>
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
                Groups = groups.Values.ToList(),
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

            foreach (var group in snapshot.Groups)
            {
                groups[group.Id] = group;
                var normalized = ShareCode.Normalize(group.JoinCode);
                if (normalized is not null)
                    groupsByJoinCode[normalized] = group.Id;
            }

            log.LogInformation(
                "Loaded {Accounts} accounts, {Requests} pending requests and {Groups} groups",
                accounts.Count, requests.Count, groups.Count);
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
