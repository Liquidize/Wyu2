using FriendRadar.Protocol;

namespace FriendRadar.Tests;

public class PresenceCryptoTests
{
    private const string Alice = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Mallory = "cccccccccccccccccccccccccccccccc";

    private static PresencePayload SamplePayload() => new()
    {
        SentAtUnixMs = 1_700_000_000_000,
        CharacterName = "Ysayle Dangoulain",
        HomeWorldId = 73,
        CurrentWorldId = 73,
        TerritoryTypeId = 155,
        MapId = 24,
        InstanceId = 2,
        X = 12.5f,
        Y = -3.25f,
        Z = -88.75f,
        Rotation = 1.25f,
        JobId = 24,
        Level = 90,
        Activity = ActivityKind.InDuty,
        ActivityDetail = "The Aery",
        OnlineStatusId = 17,
        Flags = PresenceFlags.InCombat | PresenceFlags.Bound,
        PartySize = 4,
        FreeCompanyTag = "ISH",
        Note = "one more pull",
    };

    [Fact]
    public void PayloadSurvivesARoundTrip()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();

        var sealKey = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var openKey = PresenceCrypto.DeriveKey(bob, alice.ExportPublicKeyBase64(), Alice, Bob);

        var payload = SamplePayload();
        var envelope = PresenceCrypto.Seal(sealKey, payload, Alice, Bob);
        var opened = PresenceCrypto.Open(openKey, envelope.Nonce, envelope.Ciphertext, Alice, Bob);

        Assert.Equal(payload, opened);
        Assert.Equal(Bob, envelope.RecipientAccountId);
    }

    [Fact]
    public void BothSidesDeriveTheSameKey()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();

        Assert.Equal(
            PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob),
            PresenceCrypto.DeriveKey(bob, alice.ExportPublicKeyBase64(), Alice, Bob));
    }

    [Fact]
    public void EachDirectionUsesItsOwnKey()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();

        var aliceToBob = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var bobToAlice = PresenceCrypto.DeriveKey(bob, alice.ExportPublicKeyBase64(), Bob, Alice);

        Assert.NotEqual(aliceToBob, bobToAlice);
    }

    [Fact]
    public void AThirdPartyCannotRead()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();
        using var mallory = AccountKeyPair.Create();

        var sealKey = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var envelope = PresenceCrypto.Seal(sealKey, SamplePayload(), Alice, Bob);

        var malloryKey = PresenceCrypto.DeriveKey(mallory, alice.ExportPublicKeyBase64(), Alice, Mallory);

        Assert.Null(PresenceCrypto.Open(malloryKey, envelope.Nonce, envelope.Ciphertext, Alice, Mallory));
    }

    [Fact]
    public void ReplayingABlobAtADifferentRecipientFails()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();

        var key = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var envelope = PresenceCrypto.Seal(key, SamplePayload(), Alice, Bob);

        // Same key, but the associated data no longer matches the addressing.
        Assert.Null(PresenceCrypto.Open(key, envelope.Nonce, envelope.Ciphertext, Alice, Mallory));
        Assert.Null(PresenceCrypto.Open(key, envelope.Nonce, envelope.Ciphertext, Bob, Alice));
    }

    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();

        var key = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var envelope = PresenceCrypto.Seal(key, SamplePayload(), Alice, Bob);

        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 0xFF;

        Assert.Null(PresenceCrypto.Open(key, envelope.Nonce, Convert.ToBase64String(bytes), Alice, Bob));
    }

    [Fact]
    public void MalformedInputDoesNotThrow()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();
        var key = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);

        Assert.Null(PresenceCrypto.Open(key, "not base64!", "also not base64", Alice, Bob));
        Assert.Null(PresenceCrypto.Open(key, Convert.ToBase64String(new byte[4]), Convert.ToBase64String(new byte[8]), Alice, Bob));
    }

    [Fact]
    public void EnvelopesAreNotDeterministic()
    {
        using var alice = AccountKeyPair.Create();
        using var bob = AccountKeyPair.Create();
        var key = PresenceCrypto.DeriveKey(alice, bob.ExportPublicKeyBase64(), Alice, Bob);
        var payload = SamplePayload();

        var first = PresenceCrypto.Seal(key, payload, Alice, Bob);
        var second = PresenceCrypto.Seal(key, payload, Alice, Bob);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void KeypairsRoundTripThroughStorage()
    {
        using var original = AccountKeyPair.Create();
        using var restored = AccountKeyPair.Import(original.ExportPrivateKeyBase64());

        Assert.Equal(original.ExportPublicKeyBase64(), restored.ExportPublicKeyBase64());
    }
}
