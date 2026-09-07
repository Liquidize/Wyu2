using Wyu2.Protocol;
using Wyu2.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Wyu2.Tests;

/// <summary>
/// Beacons behave like presence in who may be addressed and how long anything survives, and unlike
/// presence in that a sender can have several parked with the same person at once.
/// </summary>
public class RelayBeaconTests : IDisposable
{
    private readonly string dataDirectory =
        Path.Combine(Path.GetTempPath(), "wyu2-beacon-tests", Guid.NewGuid().ToString("N"));

    private readonly RelayStore store;
    private readonly RelayOptions options;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);

    public RelayBeaconTests()
    {
        options = new RelayOptions { DataDirectory = dataDirectory };
        store = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
    }

    /// <summary>Lets the tests fast forward past a beacon's lifetime.</summary>
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

    private static string FakePublicKey() => Convert.ToBase64String(
        Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 75).Select(i => (byte)i)).ToArray());

    private Account Register(string name)
    {
        var (result, response) = store.Register(
            new RegisterRequest { DisplayName = name, PublicKey = FakePublicKey() }, "test");
        Assert.Equal(StoreResult.Ok, result);

        var account = store.Authenticate(response!.AccessToken, null)!;
        tokens[account.Id] = response.AccessToken;
        return account;
    }

    private void Link(Account a, Account b)
    {
        store.RequestContact(a, new CreateContactRequest { ShareCode = b.ShareCode });
        var invite = store.ListRequests(b).Single(r => r.AccountId == a.Id);
        Assert.Equal(StoreResult.Ok, store.AcceptRequest(b, invite.RequestId));
    }

    private PublishBeaconResponse Drop(
        Account from, Account to, string beaconId, int ttlSeconds = ProtocolConstants.DefaultBeaconTtlSeconds)
        => store.PublishBeacon(from, new PublishBeaconRequest
        {
            BeaconId = beaconId,
            TtlSeconds = ttlSeconds,
            Envelopes = [new BeaconEnvelope { RecipientAccountId = to.Id, Nonce = "n", Ciphertext = beaconId }],
        });

    // ---------------------------------------------------------------- visibility

    [Fact]
    public void OnlyPeopleYouAreLinkedWithCanBeAddressed()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var stranger = Register("Stranger");
        Link(alice, bob);

        var response = store.PublishBeacon(alice, new PublishBeaconRequest
        {
            BeaconId = "b1",
            Envelopes =
            [
                new BeaconEnvelope { RecipientAccountId = bob.Id, Nonce = "n", Ciphertext = "ok" },
                new BeaconEnvelope { RecipientAccountId = stranger.Id, Nonce = "n", Ciphertext = "no" },
            ],
        });

        Assert.Equal(1, response.Accepted);
        Assert.Equal(1, response.Rejected);
        Assert.Single(store.FetchBeacons(bob).Entries);
        Assert.Empty(store.FetchBeacons(stranger).Entries);
    }

    [Fact]
    public void GroupMembersCanDropBeaconsForEachOther()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Drop(alice, bob, "portal");

        var entry = store.FetchBeacons(bob).Entries.Single();
        Assert.Equal("portal", entry.BeaconId);
        Assert.Equal("Alice", entry.SenderDisplayName);
    }

    // ---------------------------------------------------------------- several at once

    [Fact]
    public void SeveralBeaconsFromOneSenderCoexist()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "hunt");
        Drop(alice, bob, "portal");

        var entries = store.FetchBeacons(bob).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.BeaconId == "hunt");
        Assert.Contains(entries, e => e.BeaconId == "portal");
    }

    [Fact]
    public void PublishingTheSameIdAgainReplacesIt()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "hunt");
        store.PublishBeacon(alice, new PublishBeaconRequest
        {
            BeaconId = "hunt",
            Envelopes = [new BeaconEnvelope { RecipientAccountId = bob.Id, Nonce = "n", Ciphertext = "moved" }],
        });

        var entry = store.FetchBeacons(bob).Entries.Single();
        Assert.Equal("moved", entry.Ciphertext);
    }

    [Fact]
    public void ThePerSenderCapDropsTheOldest()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        for (var i = 0; i < ProtocolConstants.MaxBeaconsPerSender + 2; i++)
        {
            Drop(alice, bob, $"b{i}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var entries = store.FetchBeacons(bob).Entries;
        Assert.Equal(ProtocolConstants.MaxBeaconsPerSender, entries.Count);
        Assert.DoesNotContain(entries, e => e.BeaconId == "b0");
        Assert.DoesNotContain(entries, e => e.BeaconId == "b1");
        Assert.Contains(entries, e => e.BeaconId == $"b{ProtocolConstants.MaxBeaconsPerSender + 1}");
    }

    [Fact]
    public void OneSendersCapDoesNotTouchAnotherSenders()
    {
        var alice = Register("Alice");
        var carol = Register("Carol");
        var bob = Register("Bob");
        Link(alice, bob);
        Link(carol, bob);

        Drop(carol, bob, "carols");
        for (var i = 0; i < ProtocolConstants.MaxBeaconsPerSender + 2; i++)
        {
            Drop(alice, bob, $"b{i}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var entries = store.FetchBeacons(bob).Entries;
        Assert.Equal(ProtocolConstants.MaxBeaconsPerSender + 1, entries.Count);
        Assert.Contains(entries, e => e.BeaconId == "carols");
    }

    // ---------------------------------------------------------------- lifetime

    [Fact]
    public void ExpiredBeaconsAreNotHandedOut()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "brief", ttlSeconds: 60);
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    [Fact]
    public void TheTtlIsClampedToTheProtocolCeiling()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "forever", ttlSeconds: 10 * ProtocolConstants.MaxBeaconTtlSeconds);

        var entry = store.FetchBeacons(bob).Entries.Single();
        var life = TimeSpan.FromMilliseconds(entry.ExpiresAtUnixMs - entry.ReceivedAtUnixMs);
        Assert.Equal(ProtocolConstants.MaxBeaconTtlSeconds, life.TotalSeconds, 1);
    }

    [Fact]
    public void PruningCountsExpiredBeaconsSeparately()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "one", ttlSeconds: 60);
        Drop(alice, bob, "two", ttlSeconds: 60);
        clock.Advance(TimeSpan.FromSeconds(61));

        var (blobs, beacons, _, _) = store.Prune();

        Assert.Equal(0, blobs);
        Assert.Equal(2, beacons);
        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    // ---------------------------------------------------------------- withdrawal

    [Fact]
    public void WithdrawingRemovesItFromEveryRecipient()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var carol = Register("Carol");
        Link(alice, bob);
        Link(alice, carol);

        store.PublishBeacon(alice, new PublishBeaconRequest
        {
            BeaconId = "hunt",
            Envelopes =
            [
                new BeaconEnvelope { RecipientAccountId = bob.Id, Nonce = "n", Ciphertext = "c" },
                new BeaconEnvelope { RecipientAccountId = carol.Id, Nonce = "n", Ciphertext = "c" },
            ],
        });

        Drop(alice, bob, "keep");

        Assert.Equal(StoreResult.Ok, store.WithdrawBeacon(alice, "hunt"));

        Assert.Equal("keep", store.FetchBeacons(bob).Entries.Single().BeaconId);
        Assert.Empty(store.FetchBeacons(carol).Entries);
    }

    [Fact]
    public void YouCanOnlyWithdrawYourOwn()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var carol = Register("Carol");
        Link(alice, carol);
        Link(bob, carol);

        Drop(alice, carol, "hunt");

        // Bob names the same id, but the beacon is filed under whoever sent it.
        Assert.Equal(StoreResult.Ok, store.WithdrawBeacon(bob, "hunt"));

        Assert.Single(store.FetchBeacons(carol).Entries);
    }

    [Fact]
    public void AnUnusableBeaconIdIsRejected()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        var response = Drop(alice, bob, "  ");

        Assert.Equal(0, response.Accepted);
        Assert.Equal(1, response.Rejected);
        Assert.Equal(StoreResult.Invalid, store.WithdrawBeacon(alice, new string('x', 500)));
    }

    // ---------------------------------------------------------------- links coming apart

    [Fact]
    public void LeavingAGroupDropsBeaconsBothWays()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Drop(alice, bob, "alices");
        Drop(bob, alice, "bobs");

        store.LeaveGroup(bob, group.GroupId);

        Assert.Empty(store.FetchBeacons(alice).Entries);
        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    [Fact]
    public void UnlinkingDropsBeaconsBothWays()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "alices");
        Drop(bob, alice, "bobs");

        Assert.Equal(StoreResult.Ok, store.RemoveContact(alice, bob.Id));

        Assert.Empty(store.FetchBeacons(alice).Entries);
        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    [Fact]
    public void DeletingAnAccountTakesItsBeaconsWithIt()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "alices");
        Drop(bob, alice, "bobs");

        store.DeleteAccount(alice);

        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    [Fact]
    public void RotatingYourKeyDropsBeaconsSealedAgainstTheOldOne()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);

        Drop(alice, bob, "alices");
        Drop(bob, alice, "bobs");

        Assert.Equal(StoreResult.Ok,
            store.UpdateAccount(alice, new UpdateAccountRequest { PublicKey = FakePublicKey() }));

        Assert.Empty(store.FetchBeacons(alice).Entries);
        Assert.Empty(store.FetchBeacons(bob).Entries);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void BeaconsDoNotSurviveARestart()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        Link(alice, bob);
        Drop(alice, bob, "hunt");
        store.Save(force: true);

        var reloaded = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, clock);
        var restored = reloaded.Authenticate(tokens[bob.Id], null)!;

        // The link is still there; the beacon is not, because it was never written down.
        Assert.Single(reloaded.ListContacts(restored));
        Assert.Empty(reloaded.FetchBeacons(restored).Entries);
    }
}
