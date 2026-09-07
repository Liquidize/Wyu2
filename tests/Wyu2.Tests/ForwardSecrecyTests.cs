using Wyu2.Protocol;

namespace Wyu2.Tests;

public class ForwardSecrecyTests
{
    private const string Alice = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const long Now = 1_800_000_000_000;
    private const long Day = 24 * 60 * 60 * 1000L;

    // ---------------------------------------------------------------- signing

    [Fact]
    public void SignaturesVerifyAgainstThePublishedKey()
    {
        using var signer = SigningKeyPair.Create();
        var data = "the quick brown fox"u8.ToArray();

        var signature = Convert.ToBase64String(signer.Sign(data));

        Assert.True(SigningKeyPair.Verify(signer.ExportPublicKeyBase64(), data, signature));
    }

    [Fact]
    public void SomebodyElsesKeyDoesNotVerify()
    {
        using var signer = SigningKeyPair.Create();
        using var impostor = SigningKeyPair.Create();
        var data = "payload"u8.ToArray();

        var signature = Convert.ToBase64String(signer.Sign(data));

        Assert.False(SigningKeyPair.Verify(impostor.ExportPublicKeyBase64(), data, signature));
    }

    [Fact]
    public void AlteredDataDoesNotVerify()
    {
        using var signer = SigningKeyPair.Create();
        var signature = Convert.ToBase64String(signer.Sign("original"u8.ToArray()));

        Assert.False(SigningKeyPair.Verify(signer.ExportPublicKeyBase64(), "tampered"u8.ToArray(), signature));
    }

    [Theory]
    [InlineData(null, "sig")]
    [InlineData("key", null)]
    [InlineData("", "")]
    [InlineData("not base64!", "also not base64!")]
    public void MalformedSignatureInputIsRefusedRatherThanThrown(string? key, string? signature)
    {
        // All of this arrives over the network, so none of it may be allowed to throw.
        Assert.False(SigningKeyPair.Verify(key, "data"u8.ToArray(), signature));
    }

    [Fact]
    public void SigningKeysRoundTripThroughStorage()
    {
        using var original = SigningKeyPair.Create();
        using var restored = SigningKeyPair.Import(original.ExportPrivateKeyBase64());

        var data = "payload"u8.ToArray();
        Assert.True(SigningKeyPair.Verify(
            original.ExportPublicKeyBase64(), data, Convert.ToBase64String(restored.Sign(data))));
    }

    // ---------------------------------------------------------------- bundles

    [Fact]
    public void ABundleVouchesForItsEpochKey()
    {
        using var identity = SigningKeyPair.Create();
        using var epochKey = AccountKeyPair.Create();

        var bundle = PrekeyBundle.Create(identity, Alice, 3, epochKey, Now, Now + Day);

        Assert.True(bundle.Verify(Alice, identity.ExportPublicKeyBase64()));
        Assert.Equal(3, bundle.Epoch);
        Assert.Equal(epochKey.ExportPublicKeyBase64(), bundle.EpochPublicKey);
    }

    [Fact]
    public void ARelaySubstitutingItsOwnEpochKeyIsCaught()
    {
        using var identity = SigningKeyPair.Create();
        using var epochKey = AccountKeyPair.Create();
        using var relayKey = AccountKeyPair.Create();

        var genuine = PrekeyBundle.Create(identity, Alice, 1, epochKey, Now, Now + Day);

        // This is the attack the signature exists to stop: swap in a key the relay holds and read
        // everything addressed to Alice.
        var forged = genuine with { EpochPublicKey = relayKey.ExportPublicKeyBase64() };

        Assert.False(forged.Verify(Alice, identity.ExportPublicKeyBase64()));
    }

    [Fact]
    public void ABundleCannotBeLiftedOntoAnotherAccount()
    {
        using var identity = SigningKeyPair.Create();
        using var epochKey = AccountKeyPair.Create();

        var bundle = PrekeyBundle.Create(identity, Alice, 1, epochKey, Now, Now + Day);

        Assert.False(bundle.Verify(Bob, identity.ExportPublicKeyBase64()));
    }

    [Fact]
    public void AnOldBundleCannotBePresentedAsACurrentOne()
    {
        using var identity = SigningKeyPair.Create();
        using var epochKey = AccountKeyPair.Create();

        var bundle = PrekeyBundle.Create(identity, Alice, 1, epochKey, Now, Now + Day);

        Assert.False((bundle with { Epoch = 9 }).Verify(Alice, identity.ExportPublicKeyBase64()));
        Assert.False((bundle with { ExpiresAtUnixMs = Now + (Day * 400) }).Verify(
            Alice, identity.ExportPublicKeyBase64()));
    }

    [Fact]
    public void ExpiryIsReported()
    {
        using var identity = SigningKeyPair.Create();
        using var epochKey = AccountKeyPair.Create();
        var bundle = PrekeyBundle.Create(identity, Alice, 1, epochKey, Now, Now + Day);

        Assert.False(bundle.IsExpired(Now));
        Assert.True(bundle.IsExpired(Now + Day));
    }

    // ---------------------------------------------------------------- the ring

    [Fact]
    public void RotationStartsAtEpochOneAndClimbs()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing();

        Assert.Equal(0, ring.CurrentEpoch);
        Assert.Null(ring.Current);

        Assert.Equal(1, ring.Rotate(identity, Alice, Now, Day).Epoch);
        Assert.Equal(2, ring.Rotate(identity, Alice, Now + Day, Day).Epoch);
        Assert.Equal(2, ring.CurrentEpoch);
        Assert.NotNull(ring.Current);
    }

    [Fact]
    public void RotationIsDueOnceTheEpochRunsOut()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing();

        Assert.True(ring.NeedsRotation(Now));

        ring.Rotate(identity, Alice, Now, Day);

        Assert.False(ring.NeedsRotation(Now + (Day / 2)));
        Assert.True(ring.NeedsRotation(Now + Day));
    }

    [Fact]
    public void OldPrivateKeysAreDestroyed()
    {
        // The whole point: once an epoch ages out its key is gone, so captures from it stay unreadable.
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing(retainedEpochs: 2);

        for (var i = 0; i < 5; i++)
            ring.Rotate(identity, Alice, Now + (i * Day), Day);

        Assert.Equal([5, 4], ring.Epochs);
        Assert.True(ring.TryGet(5, out _));
        Assert.True(ring.TryGet(4, out _));
        Assert.False(ring.TryGet(3, out _));
        Assert.False(ring.TryGet(1, out _));
    }

    [Fact]
    public void ASingleRetainedEpochKeepsOnlyTheCurrentOne()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing(retainedEpochs: 1);

        ring.Rotate(identity, Alice, Now, Day);
        ring.Rotate(identity, Alice, Now + Day, Day);

        Assert.Equal([2], ring.Epochs);
    }

    [Fact]
    public void TheRingRoundTripsThroughStorage()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing();
        ring.Rotate(identity, Alice, Now, Day);
        var bundle = ring.Rotate(identity, Alice, Now + Day, Day);

        using var restored = EpochKeyRing.Import(ring.Export());

        Assert.Equal(ring.CurrentEpoch, restored.CurrentEpoch);
        Assert.True(restored.TryGet(bundle.Epoch, out var key));
        Assert.Equal(bundle.EpochPublicKey, key.ExportPublicKeyBase64());
    }

    [Fact]
    public void PruningIsDurableAcrossARestart()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing(retainedEpochs: 2);
        for (var i = 0; i < 4; i++)
            ring.Rotate(identity, Alice, Now + (i * Day), Day);

        // Only what survived pruning is written out, so a restart cannot resurrect a destroyed epoch.
        Assert.Equal(2, ring.Export().Count);

        using var restored = EpochKeyRing.Import(ring.Export(), retainedEpochs: 2);
        Assert.False(restored.TryGet(1, out _));
    }

    [Fact]
    public void ACorruptStoredKeyCostsOneEpochRatherThanTheWholeRing()
    {
        using var identity = SigningKeyPair.Create();
        using var ring = new EpochKeyRing();
        ring.Rotate(identity, Alice, Now, Day);
        var good = ring.Export().Single();

        var records = new List<EpochKeyRecord>
        {
            good,
            new(2, "this is not a key", Now, Now + Day),
        };

        using var restored = EpochKeyRing.Import(records);

        Assert.True(restored.TryGet(good.Epoch, out _));
        Assert.False(restored.TryGet(2, out _));
    }

    [Fact]
    public void ImportingNothingGivesAnEmptyRing()
    {
        using var ring = EpochKeyRing.Import(null);

        Assert.Equal(0, ring.CurrentEpoch);
        Assert.Null(ring.Current);
        Assert.True(ring.NeedsRotation(Now));
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void BothSidesAgreeOnAnEpochKey()
    {
        using var aliceIdentity = SigningKeyPair.Create();
        using var bobIdentity = SigningKeyPair.Create();
        using var aliceRing = new EpochKeyRing();
        using var bobRing = new EpochKeyRing();

        var aliceBundle = aliceRing.Rotate(aliceIdentity, Alice, Now, Day);
        var bobBundle = bobRing.Rotate(bobIdentity, Bob, Now, Day);

        var sending = PresenceCrypto.DeriveEpochKey(
            aliceRing.Current!, bobBundle.EpochPublicKey, Alice, Bob, aliceBundle.Epoch, bobBundle.Epoch);

        var receiving = PresenceCrypto.DeriveEpochKey(
            bobRing.Current!, aliceBundle.EpochPublicKey, Alice, Bob, aliceBundle.Epoch, bobBundle.Epoch);

        Assert.Equal(sending, receiving);
    }

    [Fact]
    public void EachRotationGivesThePairAFreshKey()
    {
        using var aliceIdentity = SigningKeyPair.Create();
        using var bobIdentity = SigningKeyPair.Create();
        using var aliceRing = new EpochKeyRing();
        using var bobRing = new EpochKeyRing();

        var a1 = aliceRing.Rotate(aliceIdentity, Alice, Now, Day);
        var b1 = bobRing.Rotate(bobIdentity, Bob, Now, Day);
        var first = PresenceCrypto.DeriveEpochKey(
            aliceRing.Current!, b1.EpochPublicKey, Alice, Bob, a1.Epoch, b1.Epoch);

        var a2 = aliceRing.Rotate(aliceIdentity, Alice, Now + Day, Day);
        var b2 = bobRing.Rotate(bobIdentity, Bob, Now + Day, Day);
        var second = PresenceCrypto.DeriveEpochKey(
            aliceRing.Current!, b2.EpochPublicKey, Alice, Bob, a2.Epoch, b2.Epoch);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CapturedTrafficStaysUnreadableOnceTheEpochIsGone()
    {
        // The property the whole scheme exists for. An attacker records an envelope now and steals both
        // long-term identity keys later; the epoch it was sealed under has since been destroyed.
        using var aliceIdentity = SigningKeyPair.Create();
        using var bobIdentity = SigningKeyPair.Create();
        using var aliceRing = new EpochKeyRing(retainedEpochs: 1);
        using var bobRing = new EpochKeyRing(retainedEpochs: 1);

        var a1 = aliceRing.Rotate(aliceIdentity, Alice, Now, Day);
        var b1 = bobRing.Rotate(bobIdentity, Bob, Now, Day);

        var key = PresenceCrypto.DeriveEpochKey(
            aliceRing.Current!, b1.EpochPublicKey, Alice, Bob, a1.Epoch, b1.Epoch);

        var captured = PresenceCrypto.Seal(
            key, new PresencePayload { CharacterName = "Ysayle", SentAtUnixMs = Now }, Alice, Bob);

        // A day passes and both sides rotate, destroying the epoch the capture was sealed under.
        aliceRing.Rotate(aliceIdentity, Alice, Now + Day, Day);
        bobRing.Rotate(bobIdentity, Bob, Now + Day, Day);

        Assert.False(bobRing.TryGet(b1.Epoch, out _));

        // Holding both identity keys is no longer enough, because neither is what sealed it.
        var withIdentityKeys = PresenceCrypto.DeriveEpochKey(
            bobRing.Current!, a1.EpochPublicKey, Alice, Bob, a1.Epoch, b1.Epoch);

        Assert.Null(PresenceCrypto.Open<PresencePayload>(
            withIdentityKeys, captured.Nonce, captured.Ciphertext, Alice, Bob));
    }

    [Fact]
    public void ARetainedPredecessorStillOpensMailSentDuringARotation()
    {
        // The reason for keeping one old epoch: a payload in flight while somebody rotates must not be
        // lost.
        using var aliceIdentity = SigningKeyPair.Create();
        using var bobIdentity = SigningKeyPair.Create();
        using var aliceRing = new EpochKeyRing(retainedEpochs: 2);
        using var bobRing = new EpochKeyRing(retainedEpochs: 2);

        var a1 = aliceRing.Rotate(aliceIdentity, Alice, Now, Day);
        var b1 = bobRing.Rotate(bobIdentity, Bob, Now, Day);

        var key = PresenceCrypto.DeriveEpochKey(
            aliceRing.Current!, b1.EpochPublicKey, Alice, Bob, a1.Epoch, b1.Epoch);
        var envelope = PresenceCrypto.Seal(
            key, new PresencePayload { CharacterName = "Ysayle" }, Alice, Bob);

        bobRing.Rotate(bobIdentity, Bob, Now + Day, Day);

        Assert.True(bobRing.TryGet(b1.Epoch, out var previous));

        var opening = PresenceCrypto.DeriveEpochKey(
            previous, a1.EpochPublicKey, Alice, Bob, a1.Epoch, b1.Epoch);

        Assert.Equal("Ysayle", PresenceCrypto.Open<PresencePayload>(
            opening, envelope.Nonce, envelope.Ciphertext, Alice, Bob)?.CharacterName);
    }
}
