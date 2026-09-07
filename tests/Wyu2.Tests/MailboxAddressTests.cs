using Wyu2.Protocol;

namespace Wyu2.Tests;

/// <summary>
/// A mailbox address is the only thing about an envelope the relay can read, so everything it must not
/// give away is pinned here: that both ends agree, that the two directions look unrelated, that it moves
/// on over time, and that nobody outside the pair can work it out.
/// </summary>
public class MailboxAddressTests
{
    private const string Purpose = ProtocolConstants.KeyDerivationInfo;
    private const long Now = 1_700_000_000_000L;

    private static (AccountKeyPair Keys, string Id) Party(string id) => (AccountKeyPair.Create(), id);

    [Fact]
    public void BothSidesLandOnTheSameAddress()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");

        var fromAlice = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());
        var fromBob = MailboxAddress.SharedSecret(bob.Keys, alice.Keys.ExportPublicKeyBase64());

        Assert.Equal(fromAlice, fromBob);

        Assert.Equal(
            MailboxAddress.Outbox(fromAlice, alice.Id, bob.Id, Purpose, Now),
            MailboxAddress.Outbox(fromBob, alice.Id, bob.Id, Purpose, Now));
    }

    [Fact]
    public void TheTwoDirectionsAreDifferentBuckets()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        // If these matched, an operator would see one bucket with two writers and know at a glance that
        // those two accounts are linked.
        Assert.NotEqual(
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now),
            MailboxAddress.Outbox(secret, bob.Id, alice.Id, Purpose, Now));
    }

    [Fact]
    public void AddressesMoveOnWithTime()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        var today = MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now);
        var tomorrow = MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now + MailboxAddress.WindowMs);

        Assert.NotEqual(today, tomorrow);
    }

    [Fact]
    public void AnAddressIsStableWithinItsWindow()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        // Anchored to the start of a window so that adding an hour cannot roll over.
        var start = MailboxAddress.WindowFor(Now) * MailboxAddress.WindowMs;

        Assert.Equal(
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, start),
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, start + (60 * 60 * 1000L)));
    }

    [Fact]
    public void DifferentKindsOfMessageGetDifferentBuckets()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        Assert.NotEqual(
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, ProtocolConstants.KeyDerivationInfo, Now),
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, ProtocolConstants.BeaconKeyDerivationInfo, Now));
    }

    [Fact]
    public void AStrangerCannotGuessTheAddress()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var mallory = Party("mmmm");

        var real = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        // Mallory knows both account ids - they are public - and can pick any window. Without the
        // agreement they still get nowhere.
        var guessed = MailboxAddress.SharedSecret(mallory.Keys, bob.Keys.ExportPublicKeyBase64());

        Assert.NotEqual(
            MailboxAddress.Outbox(real, alice.Id, bob.Id, Purpose, Now),
            MailboxAddress.Outbox(guessed, alice.Id, bob.Id, Purpose, Now));
    }

    [Fact]
    public void TheInboxCoversTheWindowsEitherSide()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        var inbox = MailboxAddress.Inbox(secret, alice.Id, bob.Id, Purpose, Now);

        Assert.Equal(3, inbox.Count);
        Assert.Equal(3, inbox.Distinct().Count());

        // What a sender posts now must be something the recipient is watching, and that has to hold on
        // either side of a rollover and for a clock that is out by a few minutes.
        foreach (var skew in new[] { -MailboxAddress.WindowMs / 2, -60_000L, 0L, 60_000L, MailboxAddress.WindowMs / 2 })
        {
            var posted = MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now + skew);
            Assert.Contains(posted, inbox);
        }
    }

    [Fact]
    public void ARolloverDoesNotStrandAnEnvelope()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        var boundary = MailboxAddress.WindowFor(Now) * MailboxAddress.WindowMs;

        // Posted a second before midnight, collected a second after.
        var posted = MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, boundary - 1_000);
        var inbox = MailboxAddress.Inbox(secret, alice.Id, bob.Id, Purpose, boundary + 1_000);

        Assert.Contains(posted, inbox);
    }

    [Fact]
    public void AddressesLookLikeAddresses()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());

        Assert.True(MailboxAddress.IsWellFormed(
            MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now)));

        Assert.False(MailboxAddress.IsWellFormed(null));
        Assert.False(MailboxAddress.IsWellFormed(string.Empty));
        Assert.False(MailboxAddress.IsWellFormed("short"));
        Assert.False(MailboxAddress.IsWellFormed(new string('z', MailboxAddress.AddressLength)));
        Assert.False(MailboxAddress.IsWellFormed(new string('A', MailboxAddress.AddressLength)));
    }

    [Fact]
    public void TheAddressDoesNotLeakTheAccountIds()
    {
        var alice = Party("aaaa");
        var bob = Party("bbbb");
        var secret = MailboxAddress.SharedSecret(alice.Keys, bob.Keys.ExportPublicKeyBase64());
        var address = MailboxAddress.Outbox(secret, alice.Id, bob.Id, Purpose, Now);

        // Obvious, but the whole point of the exercise, and a careless refactor to a formatted string
        // would sail through every other test here.
        Assert.DoesNotContain(alice.Id, address, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bob.Id, address, StringComparison.OrdinalIgnoreCase);
    }
}
