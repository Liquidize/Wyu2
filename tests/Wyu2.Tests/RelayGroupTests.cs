using Wyu2.Protocol;
using Wyu2.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Wyu2.Tests;

public class RelayGroupTests : IDisposable
{
    private readonly string dataDirectory =
        Path.Combine(Path.GetTempPath(), "wyu2-group-tests", Guid.NewGuid().ToString("N"));

    private readonly RelayStore store;
    private readonly RelayOptions options;
    private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);

    public RelayGroupTests()
    {
        options = new RelayOptions { DataDirectory = dataDirectory, MaxGroups = 3, MaxGroupMembers = 4 };
        store = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, TimeProvider.System);
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

    private static void Publish(RelayStore store, Account from, Account to, string ciphertext = "blob")
        => store.PublishPresence(from, new PublishPresenceRequest
        {
            TtlSeconds = 120,
            Envelopes = [new PresenceEnvelope { RecipientAccountId = to.Id, Nonce = "n", Ciphertext = ciphertext }],
        });

    // ---------------------------------------------------------------- membership

    [Fact]
    public void CreatingAGroupMakesYouItsOwnerAndOnlyMember()
    {
        var alice = Register("Alice");

        var (result, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Tuesday static" });

        Assert.Equal(StoreResult.Ok, result);
        Assert.Equal("Tuesday static", group!.Name);
        Assert.Equal(alice.Id, group.OwnerAccountId);
        Assert.Single(group.Members);
        Assert.NotEmpty(group.JoinCode);
    }

    [Fact]
    public void TheJoinCodeAdmitsOtherPeople()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });

        var (result, joined) = store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Assert.Equal(StoreResult.Ok, result);
        Assert.Equal(2, joined!.Members.Count);
        Assert.Single(store.ListGroups(bob));
    }

    [Fact]
    public void JoiningTwiceIsHarmless()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });

        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });
        var (result, again) = store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group.JoinCode });

        Assert.Equal(StoreResult.AlreadyExists, result);
        Assert.Equal(2, again!.Members.Count);
    }

    [Fact]
    public void AnUnknownJoinCodeIsRejected()
    {
        var alice = Register("Alice");

        var (result, _) = store.JoinGroup(alice, new JoinGroupRequest { JoinCode = "WY-0000-0000-0000-0000" });

        Assert.Equal(StoreResult.NotFound, result);
    }

    [Fact]
    public void GroupsAreCappedBothWays()
    {
        var alice = Register("Alice");
        for (var i = 0; i < options.MaxGroups; i++)
            Assert.Equal(StoreResult.Ok, store.CreateGroup(alice, new CreateGroupRequest { Name = $"G{i}" }).Result);

        Assert.Equal(StoreResult.LimitReached,
            store.CreateGroup(alice, new CreateGroupRequest { Name = "One too many" }).Result);

        var owner = Register("Owner");
        var (_, group) = store.CreateGroup(owner, new CreateGroupRequest { Name = "Full" });
        for (var i = 1; i < options.MaxGroupMembers; i++)
            Assert.Equal(StoreResult.Ok, store.JoinGroup(Register($"M{i}"), new JoinGroupRequest { JoinCode = group!.JoinCode }).Result);

        Assert.Equal(StoreResult.LimitReached,
            store.JoinGroup(Register("Late"), new JoinGroupRequest { JoinCode = group!.JoinCode }).Result);
    }

    [Fact]
    public void RotatingTheJoinCodeLocksOutTheOldOne()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        var oldCode = group!.JoinCode;

        Assert.Equal(StoreResult.Ok,
            store.UpdateGroup(alice, group.GroupId, new UpdateGroupRequest { RotateJoinCode = true }));

        Assert.Equal(StoreResult.NotFound, store.JoinGroup(bob, new JoinGroupRequest { JoinCode = oldCode }).Result);

        var current = store.ListGroups(alice).Single();
        Assert.Equal(StoreResult.Ok, store.JoinGroup(bob, new JoinGroupRequest { JoinCode = current.JoinCode }).Result);
    }

    [Fact]
    public void OnlyTheOwnerCanRenameOrRemovePeople()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Assert.Equal(StoreResult.Invalid,
            store.UpdateGroup(bob, group.GroupId, new UpdateGroupRequest { Name = "Bob's now" }));
        Assert.Equal(StoreResult.Invalid, store.LeaveGroup(bob, group.GroupId, alice.Id));

        Assert.Equal(StoreResult.Ok, store.LeaveGroup(alice, group.GroupId, bob.Id));
        Assert.Empty(store.ListGroups(bob));
    }

    [Fact]
    public void TheOwnerLeavingHandsTheGroupToTheLongestStandingMember()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var carol = Register("Carol");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });
        store.JoinGroup(carol, new JoinGroupRequest { JoinCode = group.JoinCode });

        Assert.Equal(StoreResult.Ok, store.LeaveGroup(alice, group.GroupId));

        var remaining = store.ListGroups(bob).Single();
        Assert.Equal(bob.Id, remaining.OwnerAccountId);
        Assert.Equal(2, remaining.Members.Count);
    }

    [Fact]
    public void TheLastMemberLeavingDisbandsTheGroup()
    {
        var alice = Register("Alice");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        var code = group!.JoinCode;

        Assert.Equal(StoreResult.Ok, store.LeaveGroup(alice, group.GroupId));

        Assert.Empty(store.ListGroups(alice));

        // The join code goes with it rather than lingering for an ownerless group.
        Assert.Equal(StoreResult.NotFound,
            store.JoinGroup(Register("Bob"), new JoinGroupRequest { JoinCode = code }).Result);
    }

    // ---------------------------------------------------------------- visibility

    [Fact]
    public void GroupMembersCanExchangePresenceWithoutBeingContacts()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Assert.Empty(alice.Contacts);
        Publish(store, alice, bob);

        Assert.Single(store.FetchPresence(bob).Entries);
    }

    [Fact]
    public void PeopleOutsideTheGroupStillCannotBeAddressed()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var stranger = Register("Stranger");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        var response = store.PublishPresence(alice, new PublishPresenceRequest
        {
            Envelopes =
            [
                new PresenceEnvelope { RecipientAccountId = bob.Id, Nonce = "n", Ciphertext = "ok" },
                new PresenceEnvelope { RecipientAccountId = stranger.Id, Nonce = "n", Ciphertext = "no" },
            ],
        });

        Assert.Equal(1, response.Accepted);
        Assert.Equal(1, response.Rejected);
        Assert.Empty(store.FetchPresence(stranger).Entries);
    }

    [Fact]
    public void LeavingAGroupDropsPresenceBothWays()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        Publish(store, alice, bob);
        Publish(store, bob, alice);

        store.LeaveGroup(bob, group.GroupId);

        Assert.Empty(store.FetchPresence(bob).Entries);
        Assert.Empty(store.FetchPresence(alice).Entries);
    }

    [Fact]
    public void LeavingAGroupKeepsPresenceWithPeopleYouAreAlsoContactsWith()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");

        // Linked directly as well as through the group.
        store.RequestContact(alice, new CreateContactRequest { ShareCode = bob.ShareCode });
        var invite = store.ListRequests(bob).Single();
        store.AcceptRequest(bob, invite.RequestId);

        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });
        Publish(store, alice, bob);

        store.LeaveGroup(bob, group.GroupId);

        // The direct contact link survives, so the blob should too.
        Assert.Single(store.FetchPresence(bob).Entries);
    }

    [Fact]
    public void DeletingAnAccountTakesItOutOfEveryGroup()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });

        store.DeleteAccount(alice);

        var remaining = store.ListGroups(bob).Single();
        Assert.Single(remaining.Members);
        Assert.Equal(bob.Id, remaining.OwnerAccountId);
    }

    [Fact]
    public void GroupsSurviveARestart()
    {
        var alice = Register("Alice");
        var bob = Register("Bob");
        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = "Static" });
        store.JoinGroup(bob, new JoinGroupRequest { JoinCode = group!.JoinCode });
        store.Save(force: true);

        var reloaded = new RelayStore(Options.Create(options), NullLogger<RelayStore>.Instance, TimeProvider.System);
        var restored = reloaded.Authenticate(tokens[bob.Id], null)!;

        var loaded = reloaded.ListGroups(restored).Single();
        Assert.Equal("Static", loaded.Name);
        Assert.Equal(2, loaded.Members.Count);

        // And the join code still works after a reload.
        Assert.Equal(StoreResult.Ok,
            reloaded.JoinGroup(
                reloaded.Authenticate(
                    reloaded.Register(new RegisterRequest { DisplayName = "Late", PublicKey = FakePublicKey() }, null)
                        .Response!.AccessToken, null)!,
                new JoinGroupRequest { JoinCode = loaded.JoinCode }).Result);
    }

    [Fact]
    public void GroupNamesAreCleanedUp()
    {
        var alice = Register("Alice");

        Assert.Equal(StoreResult.Invalid, store.CreateGroup(alice, new CreateGroupRequest { Name = "   " }).Result);

        var (_, group) = store.CreateGroup(alice, new CreateGroupRequest { Name = new string('x', 200) });
        Assert.Equal(ProtocolConstants.MaxGroupNameLength, group!.Name.Length);
    }
}
