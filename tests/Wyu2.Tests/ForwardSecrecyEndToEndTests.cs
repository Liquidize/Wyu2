using System.Net.Http.Json;
using Wyu2.Protocol;
using Wyu2.Tracking;

namespace Wyu2.Tests;

/// <summary>
/// The seam nothing else covers. The relay's epoch handling and the client's key management were built
/// separately; these drive the whole exchange over real HTTP exactly as the plugin does it - register,
/// publish an epoch key, link, fetch the other side's bundle, verify it, derive, seal, fetch, open - so
/// that a mismatch between the two halves shows up here rather than in somebody's game.
/// </summary>
public class ForwardSecrecyEndToEndTests : IClassFixture<RelayApiTests.RelayFactory>
{
    private readonly RelayApiTests.RelayFactory factory;

    public ForwardSecrecyEndToEndTests(RelayApiTests.RelayFactory factory) => this.factory = factory;

    private const long Day = 24 * 60 * 60 * 1000L;

    /// <summary>One side of the conversation, holding what the plugin would hold.</summary>
    private sealed record Party(
        HttpClient Http,
        AccountKeyPair Keys,
        SigningKeyPair Signer,
        EpochKeyRing Ring,
        string AccountId,
        string ShareCode);

    private async Task<Party> JoinAsync(string name, long now)
    {
        var http = factory.CreateClient();
        var keys = AccountKeyPair.Create();
        var signer = SigningKeyPair.Create();

        var registered = await http.PostAsJsonAsync("/v1/accounts", new RegisterRequest
        {
            DisplayName = name,
            PublicKey = keys.ExportPublicKeyBase64(),
            SigningPublicKey = signer.ExportPublicKeyBase64(),
            ClientVersion = "tests",
        });

        registered.EnsureSuccessStatusCode();
        var body = (await registered.Content.ReadFromJsonAsync<RegisterResponse>())!;

        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                ProtocolConstants.AuthorizationScheme, body.AccessToken);

        var ring = new EpochKeyRing();
        var bundle = ring.Rotate(signer, body.AccountId, now, Day);

        var published = await http.PostAsJsonAsync("/v1/me/prekey", new PublishPrekeyRequest { Bundle = bundle });
        published.EnsureSuccessStatusCode();

        return new Party(http, keys, signer, ring, body.AccountId, body.ShareCode);
    }

    private static async Task LinkAsync(Party a, Party b)
    {
        var invite = await a.Http.PostAsJsonAsync(
            "/v1/contacts/requests", new CreateContactRequest { ShareCode = b.ShareCode });
        invite.EnsureSuccessStatusCode();

        var requests = await b.Http.GetFromJsonAsync<List<ContactRequestDto>>("/v1/contacts/requests");
        var accept = await b.Http.PostAsync($"/v1/contacts/requests/{requests!.Single().RequestId}/accept", null);
        accept.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Fetches a contact and adopts their epoch key the way the plugin does, refusing to use one whose
    /// signature does not check out.
    /// </summary>
    private static async Task<ContactDto> VerifiedContactAsync(Party viewer, Party other)
    {
        var contacts = await viewer.Http.GetFromJsonAsync<List<ContactDto>>("/v1/contacts");
        var contact = contacts!.Single(c => c.AccountId == other.AccountId);

        Assert.NotNull(contact.Prekey);
        Assert.True(
            contact.Prekey!.Verify(other.AccountId, contact.SigningPublicKey),
            "the relay served a bundle that does not verify against the contact's identity key");

        return contact;
    }

    [Fact]
    public async Task PresenceRoundTripsUnderEpochKeys()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alice = await JoinAsync("Alice", now);
        var bob = await JoinAsync("Bob", now);
        await LinkAsync(alice, bob);

        var bobAsSeenByAlice = await VerifiedContactAsync(alice, bob);

        var payload = new PresencePayload
        {
            SentAtUnixMs = now,
            Sequence = 7,
            CharacterName = "Ysayle Dangoulain",
            TerritoryTypeId = 155,
            Activity = ActivityKind.InDuty,
            ActivityDetail = "The Aery",
        };

        var sealing = PresenceCrypto.DeriveEpochKey(
            alice.Ring.Current!, bobAsSeenByAlice.Prekey!.EpochPublicKey,
            alice.AccountId, bob.AccountId, alice.Ring.CurrentEpoch, bobAsSeenByAlice.Prekey.Epoch);

        var envelope = PresenceCrypto.Seal(sealing, payload, alice.AccountId, bob.AccountId);

        var publish = await alice.Http.PostAsJsonAsync("/v1/presence", new PublishPresenceRequest
        {
            TtlSeconds = 120,
            Envelopes =
            [
                envelope with
                {
                    SenderEpoch = alice.Ring.CurrentEpoch,
                    RecipientEpoch = bobAsSeenByAlice.Prekey.Epoch,
                },
            ],
        });

        publish.EnsureSuccessStatusCode();
        Assert.Equal(1, (await publish.Content.ReadFromJsonAsync<PublishPresenceResponse>())!.Accepted);

        var fetched = await bob.Http.GetFromJsonAsync<FetchPresenceResponse>("/v1/presence");
        var received = Assert.Single(fetched!.Entries);

        // The relay must hand the epochs back exactly as they were sent, or the recipient cannot tell
        // which of its keys to try.
        Assert.Equal(alice.Ring.CurrentEpoch, received.SenderEpoch);
        Assert.Equal(bob.Ring.CurrentEpoch, received.RecipientEpoch);

        var aliceAsSeenByBob = await VerifiedContactAsync(bob, alice);
        Assert.True(bob.Ring.TryGet(received.RecipientEpoch, out var mine));

        var opening = PresenceCrypto.DeriveEpochKey(
            mine, aliceAsSeenByBob.Prekey!.EpochPublicKey,
            alice.AccountId, bob.AccountId, received.SenderEpoch, received.RecipientEpoch);

        var opened = PresenceCrypto.Open<PresencePayload>(
            opening, received.Nonce, received.Ciphertext, alice.AccountId, bob.AccountId);

        Assert.Equal(payload, opened);
        Assert.Equal(7, opened!.Sequence);
    }

    [Fact]
    public async Task AReplayedEnvelopeIsRefusedOnArrival()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alice = await JoinAsync("Alice", now);
        var bob = await JoinAsync("Bob", now);
        await LinkAsync(alice, bob);

        var contact = await VerifiedContactAsync(alice, bob);
        var key = PresenceCrypto.DeriveEpochKey(
            alice.Ring.Current!, contact.Prekey!.EpochPublicKey,
            alice.AccountId, bob.AccountId, alice.Ring.CurrentEpoch, contact.Prekey.Epoch);

        var guard = new ReplayGuard();

        // Two genuine updates, then the first one served again.
        var first = PresenceCrypto.Seal(
            key, new PresencePayload { SentAtUnixMs = now, Sequence = 1 }, alice.AccountId, bob.AccountId);
        var second = PresenceCrypto.Seal(
            key, new PresencePayload { SentAtUnixMs = now + 10_000, Sequence = 2 },
            alice.AccountId, bob.AccountId);

        var opened1 = PresenceCrypto.Open<PresencePayload>(
            key, first.Nonce, first.Ciphertext, alice.AccountId, bob.AccountId)!;
        var opened2 = PresenceCrypto.Open<PresencePayload>(
            key, second.Nonce, second.Ciphertext, alice.AccountId, bob.AccountId)!;

        Assert.Equal(
            ReplayVerdict.Accepted,
            guard.Inspect(alice.AccountId, opened1.Sequence, opened1.SentAtUnixMs, now));
        Assert.Equal(
            ReplayVerdict.Accepted,
            guard.Inspect(alice.AccountId, opened2.Sequence, opened2.SentAtUnixMs, now + 10_000));

        // A relay re-serving the older blob: it still decrypts perfectly, which is exactly why the
        // counter has to be checked separately.
        var replayed = PresenceCrypto.Open<PresencePayload>(
            key, first.Nonce, first.Ciphertext, alice.AccountId, bob.AccountId)!;

        Assert.Equal(
            ReplayVerdict.RejectedReplay,
            guard.Inspect(alice.AccountId, replayed.Sequence, replayed.SentAtUnixMs, now + 20_000));
    }

    [Fact]
    public async Task ARotationLeavesTheOldEpochReadableButChangesTheKey()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alice = await JoinAsync("Alice", now);
        var bob = await JoinAsync("Bob", now);
        await LinkAsync(alice, bob);

        var beforeRotation = (await VerifiedContactAsync(alice, bob)).Prekey!;

        // Bob rotates and publishes, as the plugin does daily.
        var next = bob.Ring.Rotate(bob.Signer, bob.AccountId, now + Day, Day);
        (await bob.Http.PostAsJsonAsync("/v1/me/prekey", new PublishPrekeyRequest { Bundle = next }))
            .EnsureSuccessStatusCode();

        var afterRotation = (await VerifiedContactAsync(alice, bob)).Prekey!;

        Assert.Equal(beforeRotation.Epoch + 1, afterRotation.Epoch);
        Assert.NotEqual(beforeRotation.EpochPublicKey, afterRotation.EpochPublicKey);

        // Bob still holds the predecessor, so anything Alice sealed just before the rotation opens.
        Assert.True(bob.Ring.TryGet(beforeRotation.Epoch, out _));

        var oldKey = PresenceCrypto.DeriveEpochKey(
            alice.Ring.Current!, beforeRotation.EpochPublicKey,
            alice.AccountId, bob.AccountId, alice.Ring.CurrentEpoch, beforeRotation.Epoch);

        var newKey = PresenceCrypto.DeriveEpochKey(
            alice.Ring.Current!, afterRotation.EpochPublicKey,
            alice.AccountId, bob.AccountId, alice.Ring.CurrentEpoch, afterRotation.Epoch);

        Assert.NotEqual(oldKey, newKey);
    }

    [Fact]
    public async Task TheRelayWillNotAcceptAnEpochThatGoesBackwards()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alice = await JoinAsync("Alice", now);

        // Re-publishing the bundle already on file would let a captured one drag a contact back onto a
        // key whose private half may since have been destroyed or learned.
        var stale = PrekeyBundle.Create(
            alice.Signer, alice.AccountId, alice.Ring.CurrentEpoch, alice.Ring.Current!, now, now + Day);

        var response = await alice.Http.PostAsJsonAsync(
            "/v1/me/prekey", new PublishPrekeyRequest { Bundle = stale });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("stale_epoch", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }
}
