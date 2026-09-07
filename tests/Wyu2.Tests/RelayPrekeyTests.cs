using Wyu2.Protocol;
using Wyu2.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Wyu2.Tests;

/// <summary>
/// The relay's half of forward secrecy: it takes a signing key, files the epoch bundles vouched for by
/// it, hands them on to everybody entitled to see them, and carries the epoch numbers on envelopes
/// through untouched.
/// </summary>
public class RelayPrekeyTests : IDisposable
{
    private readonly string dataDirectory =
        Path.Combine(Path.GetTempPath(), "wyu2-prekey-tests", Guid.NewGuid().ToString("N"));

    private readonly RelayStore store;
    private readonly RelayOptions options;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> keys = [];

    public RelayPrekeyTests()
    {
        options = new RelayOptions { DataDirectory = dataDirectory };
        store = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
    }

    /// <summary>Lets the tests fast forward past TTLs and epoch lifetimes.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    public void Dispose()
    {
        foreach (var key in keys)
            key.Dispose();

        if (Directory.Exists(dataDirectory))
            Directory.Delete(dataDirectory, recursive: true);

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- signing keys

    [Fact]
    public void AClientWithNoSigningKeyStillRegisters()
    {
        var account = Register("Ysayle", signing: null);

        Assert.Equal(string.Empty, account.SigningPublicKey);
        Assert.Null(account.Prekey);
        Assert.Equal(string.Empty, store.Describe(account).SigningPublicKey);
    }

    [Fact]
    public void RegisteringWithASigningKeyKeepsIt()
    {
        var signer = Signer();
        var account = Register("Ysayle", signer);

        Assert.Equal(signer.ExportPublicKeyBase64(), account.SigningPublicKey);
        Assert.Equal(signer.ExportPublicKeyBase64(), store.Describe(account).SigningPublicKey);
    }

    [Fact]
    public void ASigningKeyThatIsNotAKeyIsRefused()
    {
        var (result, _) = store.Register(
            new RegisterRequest
            {
                DisplayName = "Ysayle",
                PublicKey = FakePublicKey(),
                SigningPublicKey = "nope",
            },
            null);

        Assert.Equal(StoreResult.Invalid, result);
    }

    [Fact]
    public void AnOlderAccountCanGainASigningKeyWithoutLosingParkedPresence()
    {
        var alice = Register("Alice", signing: null);
        var bob = Register("Bob", signing: null);
        Link(alice, bob);
        Publish(alice, bob, "blob");

        var signer = Signer();
        Assert.Equal(
            StoreResult.Ok,
            store.UpdateAccount(bob, new UpdateAccountRequest { SigningPublicKey = signer.ExportPublicKeyBase64() }));

        Assert.Equal(signer.ExportPublicKeyBase64(), bob.SigningPublicKey);

        // Nothing was sealed to the signing key, so nothing it replaced has become unreadable.
        var entry = Assert.Single(store.FetchPresence(bob).Entries);
        Assert.Equal("blob", entry.Ciphertext);
    }

    [Fact]
    public void UpgradingWithRubbishIsRefused()
    {
        var account = Register("Ysayle", signing: null);

        Assert.Equal(
            StoreResult.Invalid,
            store.UpdateAccount(account, new UpdateAccountRequest { SigningPublicKey = "nope" }));

        Assert.Equal(string.Empty, account.SigningPublicKey);
    }

    // ---------------------------------------------------------------- prekey bundles

    [Fact]
    public void APrekeyNeedsASigningKeyToCheckItAgainst()
    {
        var account = Register("Ysayle", signing: null);
        var stranger = Signer();

        Assert.Equal(StoreResult.NotFound, Publish(account, Bundle(stranger, account.Id, epoch: 1)));
        Assert.Null(account.Prekey);
    }

    [Fact]
    public void ABundleWithABrokenSignatureIsRefused()
    {
        var signer = Signer();
        var account = Register("Ysayle", signer);

        // Everything the signature covers is still there; only the signature itself is somebody else's.
        var impostor = Signer();
        var forged = Bundle(signer, account.Id, epoch: 1) with
        {
            Signature = Bundle(impostor, account.Id, epoch: 1).Signature,
        };

        Assert.Equal(StoreResult.Invalid, Publish(account, forged));
        Assert.Null(account.Prekey);
    }

    [Fact]
    public void ABundleMintedForSomebodyElseIsRefused()
    {
        var signer = Signer();
        var alice = Register("Alice", signer);
        var bob = Register("Bob", Signer());

        Assert.Equal(StoreResult.Invalid, Publish(alice, Bundle(signer, bob.Id, epoch: 1)));
        Assert.Null(alice.Prekey);
    }

    [Fact]
    public void AnEmptyBundleIsRefused()
    {
        var account = Register("Ysayle", Signer());

        Assert.Equal(StoreResult.Invalid, store.PublishPrekey(account, new PublishPrekeyRequest()));
        Assert.Null(account.Prekey);
    }

    [Fact]
    public void AnOlderEpochCannotDisplaceANewerOne()
    {
        var signer = Signer();
        var account = Register("Ysayle", signer);

        Assert.Equal(StoreResult.Ok, Publish(account, Bundle(signer, account.Id, epoch: 4)));

        Assert.Equal(StoreResult.AlreadyExists, Publish(account, Bundle(signer, account.Id, epoch: 3)));
        Assert.Equal(StoreResult.AlreadyExists, Publish(account, Bundle(signer, account.Id, epoch: 4)));

        Assert.Equal(4, account.Prekey?.Epoch);
    }

    [Fact]
    public void ANewerEpochReplacesTheCurrentOne()
    {
        var signer = Signer();
        var account = Register("Ysayle", signer);
        var first = Bundle(signer, account.Id, epoch: 1);

        Assert.Equal(StoreResult.Ok, Publish(account, first));

        clock.Advance(TimeSpan.FromDays(1));
        var second = Bundle(signer, account.Id, epoch: 2);
        Assert.Equal(StoreResult.Ok, Publish(account, second));

        Assert.Equal(2, account.Prekey?.Epoch);
        Assert.Equal(second.EpochPublicKey, account.Prekey?.EpochPublicKey);
        Assert.NotEqual(first.EpochPublicKey, account.Prekey?.EpochPublicKey);
    }

    // ---------------------------------------------------------------- distribution

    [Fact]
    public void ContactsAreGivenTheSigningKeyAndTheCurrentBundle()
    {
        var signer = Signer();
        var alice = Register("Alice", signer);
        var bob = Register("Bob", Signer());
        Link(alice, bob);

        var bundle = Bundle(signer, alice.Id, epoch: 7);
        Assert.Equal(StoreResult.Ok, Publish(alice, bundle));

        var contact = Assert.Single(store.ListContacts(bob));
        Assert.Equal(alice.Id, contact.AccountId);
        Assert.Equal(signer.ExportPublicKeyBase64(), contact.SigningPublicKey);
        Assert.Equal(7, contact.Prekey?.Epoch);
        Assert.Equal(bundle.EpochPublicKey, contact.Prekey?.EpochPublicKey);

        // What a contact receives has to be enough to check on their own account.
        Assert.True(contact.Prekey!.Verify(contact.AccountId, contact.SigningPublicKey));
    }

    [Fact]
    public void AContactWhoHasNotPublishedOneComesBackWithoutABundle()
    {
        var alice = Register("Alice", signing: null);
        var bob = Register("Bob", Signer());
        Link(alice, bob);

        var contact = Assert.Single(store.ListContacts(bob));

        Assert.Equal(string.Empty, contact.SigningPublicKey);
        Assert.Null(contact.Prekey);
    }

    [Fact]
    public void GroupMembersAreGivenTheSigningKeyAndTheCurrentBundle()
    {
        var signer = Signer();
        var alice = Register("Alice", signer);
        var bob = Register("Bob", Signer());

        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Statics" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        var bundle = Bundle(signer, alice.Id, epoch: 3);
        Assert.Equal(StoreResult.Ok, Publish(alice, bundle));

        var member = Assert.Single(
            Assert.Single(store.ListGroups(bob)).Members,
            m => m.AccountId == alice.Id);

        Assert.Equal(signer.ExportPublicKeyBase64(), member.SigningPublicKey);
        Assert.Equal(3, member.Prekey?.Epoch);
        Assert.True(member.Prekey!.Verify(member.AccountId, member.SigningPublicKey));
    }

    [Fact]
    public void ThePublishedBundleSurvivesARestart()
    {
        var signer = Signer();
        var (account, token) = RegisterWithToken("Ysayle", signer);
        Assert.Equal(StoreResult.Ok, Publish(account, Bundle(signer, account.Id, epoch: 5)));

        store.Save(force: true);
        var reloaded = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
        var restored = reloaded.Authenticate(token, null);

        Assert.NotNull(restored);
        Assert.Equal(signer.ExportPublicKeyBase64(), restored!.SigningPublicKey);
        Assert.Equal(5, restored.Prekey?.Epoch);
    }

    // ---------------------------------------------------------------- envelope epochs

    [Fact]
    public void PresenceEpochsSurviveTheRoundTripUnchanged()
    {
        var alice = Register("Alice", Signer());
        var bob = Register("Bob", Signer());
        Link(alice, bob);

        store.PublishPresence(alice, new PublishPresenceRequest
        {
            TtlSeconds = 60,
            Envelopes =
            [
                new PresenceEnvelope
                {
                    RecipientAccountId = bob.Id,
                    Nonce = "n",
                    Ciphertext = "blob",
                    SenderEpoch = 9,
                    RecipientEpoch = 4,
                },
            ],
        });

        var entry = Assert.Single(store.FetchPresence(bob).Entries);
        Assert.Equal(9, entry.SenderEpoch);
        Assert.Equal(4, entry.RecipientEpoch);
    }

    [Fact]
    public void BeaconEpochsSurviveTheRoundTripUnchanged()
    {
        var alice = Register("Alice", Signer());
        var bob = Register("Bob", Signer());
        Link(alice, bob);

        store.PublishBeacon(alice, new PublishBeaconRequest
        {
            BeaconId = "marker",
            TtlSeconds = 60,
            Envelopes =
            [
                new BeaconEnvelope
                {
                    RecipientAccountId = bob.Id,
                    Nonce = "n",
                    Ciphertext = "blob",
                    SenderEpoch = 2,
                    RecipientEpoch = 11,
                },
            ],
        });

        var entry = Assert.Single(store.FetchBeacons(bob).Entries);
        Assert.Equal(2, entry.SenderEpoch);
        Assert.Equal(11, entry.RecipientEpoch);
    }

    [Fact]
    public void AnEnvelopeFromAClientWithNoEpochsComesBackAtZero()
    {
        var alice = Register("Alice", signing: null);
        var bob = Register("Bob", signing: null);
        Link(alice, bob);
        Publish(alice, bob, "blob");

        var entry = Assert.Single(store.FetchPresence(bob).Entries);
        Assert.Equal(0, entry.SenderEpoch);
        Assert.Equal(0, entry.RecipientEpoch);
    }

    // ---------------------------------------------------------------- helpers

    private Account Register(string name, SigningKeyPair? signing) => RegisterWithToken(name, signing).Account;

    private (Account Account, string Token) RegisterWithToken(string name, SigningKeyPair? signing)
    {
        var (result, response) = store.Register(
            new RegisterRequest
            {
                DisplayName = name,
                PublicKey = FakePublicKey(),
                SigningPublicKey = signing?.ExportPublicKeyBase64() ?? string.Empty,
            },
            "test");

        Assert.Equal(StoreResult.Ok, result);
        var account = store.Authenticate(response!.AccessToken, "test");
        Assert.NotNull(account);
        return (account!, response.AccessToken);
    }

    /// <summary>A keypair kept alive for the length of the test, since a disposed one cannot sign.</summary>
    private SigningKeyPair Signer()
    {
        var signer = SigningKeyPair.Create();
        keys.Add(signer);
        return signer;
    }

    private PrekeyBundle Bundle(SigningKeyPair signer, string accountId, int epoch)
    {
        var epochKey = AccountKeyPair.Create();
        keys.Add(epochKey);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return PrekeyBundle.Create(signer, accountId, epoch, epochKey, now, now + ProtocolConstants.EpochLifetimeMs);
    }

    private StoreResult Publish(Account account, PrekeyBundle bundle)
        => store.PublishPrekey(account, new PublishPrekeyRequest { Bundle = bundle });

    private void Publish(Account from, Account to, string ciphertext)
        => store.PublishPresence(from, new PublishPresenceRequest
        {
            TtlSeconds = 60,
            Envelopes = [new PresenceEnvelope { RecipientAccountId = to.Id, Nonce = "n", Ciphertext = ciphertext }],
        });

    private void Link(Account a, Account b)
    {
        var (_, _, request) = store.RequestContact(a, new CreateContactRequest { ShareCode = b.ShareCode });
        Assert.Equal(StoreResult.Ok, store.AcceptRequest(b, request!.RequestId));
    }

    /// <summary>A blob the size of a real P-256 SubjectPublicKeyInfo, which is all the store checks.</summary>
    private static string FakePublicKey() => Convert.ToBase64String(
        Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 75).Select(i => (byte)i)).ToArray());
}
