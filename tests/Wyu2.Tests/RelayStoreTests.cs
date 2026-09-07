using Wyu2.Protocol;
using Wyu2.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Wyu2.Tests;

public class RelayStoreTests : IDisposable
{
    private readonly string dataDirectory =
        Path.Combine(Path.GetTempPath(), "wyu2-tests", Guid.NewGuid().ToString("N"));

    private readonly RelayStore store;
    private readonly RelayOptions options;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public RelayStoreTests()
    {
        options = new RelayOptions { DataDirectory = dataDirectory, MaxContacts = 5, MaxPendingRequests = 3 };
        store = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
    }

    /// <summary>Lets the tests fast forward past TTLs and retention windows.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    public void Dispose()
    {
        if (Directory.Exists(dataDirectory))
            Directory.Delete(dataDirectory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private (Account Account, string Token, string ShareCode) Register(string name = "Someone")
    {
        var (result, response) = store.Register(
            new RegisterRequest { DisplayName = name, PublicKey = FakePublicKey() }, "test");

        Assert.Equal(StoreResult.Ok, result);
        Assert.NotNull(response);

        var account = store.Authenticate(response!.AccessToken, "test");
        Assert.NotNull(account);
        tokensByAccount[account!.Id] = response.AccessToken;
        return (account, response.AccessToken, response.ShareCode);
    }

    /// <summary>A byte blob the size of a real P-256 SubjectPublicKeyInfo, which is all the store checks.</summary>
    private static string FakePublicKey() => Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(
        Enumerable.Range(0, 75).Select(i => (byte)i)).ToArray());

    // ---------------------------------------------------------------- accounts

    [Fact]
    public void RegistrationIssuesUsableCredentials()
    {
        var (account, token, shareCode) = Register("Ysayle");

        Assert.Equal("Ysayle", account.DisplayName);
        Assert.NotEmpty(shareCode);
        Assert.NotNull(store.Authenticate(token, null));
        Assert.Null(store.Authenticate("not-a-token", null));
    }

    [Fact]
    public void TheAccessTokenIsNotStoredInTheClear()
    {
        var (account, token, _) = Register();

        Assert.NotEqual(token, account.TokenHash);
        Assert.Equal(RelayStore.HashToken(token), account.TokenHash);
    }

    [Fact]
    public void RegistrationRejectsRubbish()
    {
        Assert.Equal(StoreResult.Invalid,
            store.Register(new RegisterRequest { DisplayName = "  ", PublicKey = FakePublicKey() }, null).Result);

        Assert.Equal(StoreResult.Invalid,
            store.Register(new RegisterRequest { DisplayName = "Someone", PublicKey = "nope" }, null).Result);
    }

    [Fact]
    public void ClosedRegistrationNeedsAnInviteCode()
    {
        options.RegistrationOpen = false;
        options.InviteCodes.Add("let-me-in");

        Assert.Equal(StoreResult.RegistrationClosed,
            store.Register(new RegisterRequest { DisplayName = "A", PublicKey = FakePublicKey() }, null).Result);

        Assert.Equal(StoreResult.Ok,
            store.Register(
                new RegisterRequest { DisplayName = "A", PublicKey = FakePublicKey(), InviteCode = "let-me-in" },
                null).Result);
    }

    [Fact]
    public void RotatingTheShareCodeInvalidatesTheOldOne()
    {
        var (alice, _, oldCode) = Register("Alice");
        var (bob, _, _) = Register("Bob");

        Assert.Equal(StoreResult.Ok, store.UpdateAccount(alice, new UpdateAccountRequest { RotateShareCode = true }));
        Assert.NotEqual(oldCode, alice.ShareCode);

        var (result, _, _) = store.RequestContact(bob, new CreateContactRequest { ShareCode = oldCode });
        Assert.Equal(StoreResult.NotFound, result);

        var (retry, _, _) = store.RequestContact(bob, new CreateContactRequest { ShareCode = alice.ShareCode });
        Assert.Equal(StoreResult.Ok, retry);
    }

    // ---------------------------------------------------------------- contacts

    [Fact]
    public void LinkingRequiresBothSidesToAgree()
    {
        var (alice, _, aliceCode) = Register("Alice");
        var (bob, _, _) = Register("Bob");

        var (result, linked, request) = store.RequestContact(bob, new CreateContactRequest { ShareCode = aliceCode });

        Assert.Equal(StoreResult.Ok, result);
        Assert.False(linked);
        Assert.NotNull(request);
        Assert.Empty(alice.Contacts);
        Assert.Empty(bob.Contacts);

        Assert.Equal(StoreResult.Ok, store.AcceptRequest(alice, request!.RequestId));
        Assert.Contains(bob.Id, alice.Contacts);
        Assert.Contains(alice.Id, bob.Contacts);
    }

    [Fact]
    public void TradingCodesBothWaysLinksImmediately()
    {
        var (alice, _, aliceCode) = Register("Alice");
        var (bob, _, bobCode) = Register("Bob");

        store.RequestContact(bob, new CreateContactRequest { ShareCode = aliceCode });
        var (result, linked, _) = store.RequestContact(alice, new CreateContactRequest { ShareCode = bobCode });

        Assert.Equal(StoreResult.Ok, result);
        Assert.True(linked);
        Assert.Contains(bob.Id, alice.Contacts);
        Assert.Empty(store.ListRequests(alice));
    }

    [Fact]
    public void YouCannotLinkWithYourself()
    {
        var (alice, _, aliceCode) = Register("Alice");

        var (result, _, _) = store.RequestContact(alice, new CreateContactRequest { ShareCode = aliceCode });

        Assert.Equal(StoreResult.Invalid, result);
    }

    [Fact]
    public void DuplicateInvitesAreRefused()
    {
        var (_, _, aliceCode) = Register("Alice");
        var (bob, _, _) = Register("Bob");

        store.RequestContact(bob, new CreateContactRequest { ShareCode = aliceCode });
        var (result, _, _) = store.RequestContact(bob, new CreateContactRequest { ShareCode = aliceCode });

        Assert.Equal(StoreResult.AlreadyExists, result);
    }

    [Fact]
    public void DecliningRemovesTheInviteWithoutLinking()
    {
        var (alice, _, aliceCode) = Register("Alice");
        var (bob, _, _) = Register("Bob");

        var (_, _, request) = store.RequestContact(bob, new CreateContactRequest { ShareCode = aliceCode });
        Assert.Equal(StoreResult.Ok, store.DeclineRequest(alice, request!.RequestId));

        Assert.Empty(alice.Contacts);
        Assert.Empty(store.ListRequests(alice));
    }

    [Fact]
    public void UnlinkingIsMutual()
    {
        var (alice, bob) = LinkedPair();

        Assert.Equal(StoreResult.Ok, store.RemoveContact(alice, bob.Id));
        Assert.Empty(alice.Contacts);
        Assert.Empty(bob.Contacts);
    }

    // ---------------------------------------------------------------- presence

    [Fact]
    public void PresenceReachesOnlyTheAddressedContact()
    {
        var (alice, bob) = LinkedPair();
        var (stranger, _, _) = Register("Stranger");

        var response = store.PublishPresence(alice, new PublishPresenceRequest
        {
            TtlSeconds = 60,
            Envelopes =
            [
                new PresenceEnvelope { RecipientAccountId = bob.Id, Nonce = "nonce", Ciphertext = "blob" },
                new PresenceEnvelope { RecipientAccountId = stranger.Id, Nonce = "nonce", Ciphertext = "blob" },
            ],
        });

        Assert.Equal(1, response.Accepted);
        Assert.Equal(1, response.Rejected);

        Assert.Single(store.FetchPresence(bob).Entries);
        Assert.Empty(store.FetchPresence(stranger).Entries);
    }

    [Fact]
    public void OversizedBlobsAreRejected()
    {
        var (alice, bob) = LinkedPair();

        var response = store.PublishPresence(alice, new PublishPresenceRequest
        {
            Envelopes =
            [
                new PresenceEnvelope
                {
                    RecipientAccountId = bob.Id,
                    Nonce = "nonce",
                    Ciphertext = new string('x', ProtocolConstants.MaxEnvelopeBytes + 1),
                },
            ],
        });

        Assert.Equal(0, response.Accepted);
        Assert.Equal(1, response.Rejected);
    }

    [Fact]
    public void PublishingReplacesTheEarlierBlob()
    {
        var (alice, bob) = LinkedPair();

        Publish(alice, bob, "first");
        Publish(alice, bob, "second");

        var entries = store.FetchPresence(bob).Entries;
        Assert.Single(entries);
        Assert.Equal("second", entries[0].Ciphertext);
        Assert.Equal(alice.Id, entries[0].SenderAccountId);
    }

    [Fact]
    public void ExpiredBlobsAreNotHandedOut()
    {
        var (alice, bob) = LinkedPair();
        Publish(alice, bob, "blob");

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Single(store.FetchPresence(bob).Entries);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(store.FetchPresence(bob).Entries);
    }

    [Fact]
    public void TimeToLiveIsClampedIntoRange()
    {
        var (alice, bob) = LinkedPair();

        store.PublishPresence(alice, new PublishPresenceRequest
        {
            TtlSeconds = 999_999,
            Envelopes = [new PresenceEnvelope { RecipientAccountId = bob.Id, Nonce = "n", Ciphertext = "blob" }],
        });

        var entry = Assert.Single(store.FetchPresence(bob).Entries);
        var ttl = TimeSpan.FromMilliseconds(entry.ExpiresAtUnixMs - entry.ReceivedAtUnixMs);

        Assert.Equal(ProtocolConstants.MaxPresenceTtlSeconds, ttl.TotalSeconds, 1);
    }

    [Fact]
    public void PruningClearsExpiredBlobsAndStaleInvites()
    {
        var (alice, bob) = LinkedPair();
        var (carol, _, _) = Register("Carol");
        Publish(alice, bob, "blob");
        store.RequestContact(carol, new CreateContactRequest { ShareCode = alice.ShareCode });

        clock.Advance(TimeSpan.FromDays(options.ContactRequestRetentionDays + 1));
        var (blobs, _, requests, accounts) = store.Prune();

        Assert.Equal(1, blobs);
        Assert.Equal(1, requests);
        Assert.Equal(0, accounts);
    }

    [Fact]
    public void DormantAccountsAreEventuallyForgotten()
    {
        var (alice, bob) = LinkedPair();

        clock.Advance(TimeSpan.FromDays(options.AccountRetentionDays + 1));

        // Bob checks in, so only Alice ages out.
        Assert.NotNull(store.Authenticate(BobToken(bob), null));

        var (_, _, _, accounts) = store.Prune();

        Assert.Equal(1, accounts);
        Assert.Equal(1, store.AccountCount);
        Assert.Empty(bob.Contacts);
        Assert.DoesNotContain(alice.Id, bob.Contacts);
    }

    [Fact]
    public void UnlinkingDropsAnythingAlreadyParked()
    {
        var (alice, bob) = LinkedPair();
        Publish(alice, bob, "blob");

        store.RemoveContact(bob, alice.Id);

        Assert.Empty(store.FetchPresence(bob).Entries);
    }

    [Fact]
    public void DeletingAnAccountRemovesItEverywhere()
    {
        var (alice, bob) = LinkedPair();
        Publish(alice, bob, "blob");

        store.DeleteAccount(alice);

        Assert.Empty(bob.Contacts);
        Assert.Empty(store.FetchPresence(bob).Entries);
        Assert.Equal(1, store.AccountCount);
    }

    [Fact]
    public void ChangingKeysDropsBlobsTheNewKeyCouldNotRead()
    {
        var (alice, bob) = LinkedPair();
        Publish(alice, bob, "blob");

        Assert.Equal(StoreResult.Ok,
            store.UpdateAccount(bob, new UpdateAccountRequest { PublicKey = FakePublicKey() }));

        Assert.Empty(store.FetchPresence(bob).Entries);
    }

    [Fact]
    public void ThePanicSwitchClearsEverythingWePublished()
    {
        var (alice, bob) = LinkedPair();
        Publish(alice, bob, "blob");

        store.ClearOwnPresence(alice);

        Assert.Empty(store.FetchPresence(bob).Entries);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void AccountsSurviveARestartButPresenceDoesNot()
    {
        var (alice, token, _) = Register("Alice");
        var (bob, _, _) = Register("Bob");
        Link(alice, bob);
        Publish(alice, bob, "blob");

        store.Save(force: true);

        var reloaded = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
        var restored = reloaded.Authenticate(token, null);

        Assert.NotNull(restored);
        Assert.Contains(bob.Id, restored!.Contacts);
        Assert.Empty(reloaded.FetchPresence(restored).Entries);
    }

    // ---------------------------------------------------------------- helpers

    private readonly Dictionary<string, string> tokensByAccount = new(StringComparer.Ordinal);

    private string BobToken(Account bob) => tokensByAccount[bob.Id];

    private (Account Alice, Account Bob) LinkedPair()
    {
        var (alice, _, _) = Register("Alice");
        var (bob, _, _) = Register("Bob");
        Link(alice, bob);
        return (alice, bob);
    }

    private void Link(Account a, Account b)
    {
        var (_, _, request) = store.RequestContact(a, new CreateContactRequest { ShareCode = b.ShareCode });
        Assert.Equal(StoreResult.Ok, store.AcceptRequest(b, request!.RequestId));
    }

    private void Publish(Account from, Account to, string ciphertext)
        => store.PublishPresence(from, new PublishPresenceRequest
        {
            TtlSeconds = 60,
            Envelopes = [new PresenceEnvelope { RecipientAccountId = to.Id, Nonce = "n", Ciphertext = ciphertext }],
        });
}
