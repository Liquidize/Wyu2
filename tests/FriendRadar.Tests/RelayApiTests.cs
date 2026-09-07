using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FriendRadar.Protocol;
using FriendRadar.Relay;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FriendRadar.Tests;

/// <summary>
/// End to end over real HTTP: two clients register, swap share codes, and exchange an encrypted presence
/// update that the relay never sees the inside of.
/// </summary>
public class RelayApiTests : IClassFixture<RelayApiTests.RelayFactory>
{
    private readonly RelayFactory factory;

    public RelayApiTests(RelayFactory factory) => this.factory = factory;

    public sealed class RelayFactory : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "friendradar-api-tests", Guid.NewGuid().ToString("N"));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices(services =>
                services.Configure<RelayOptions>(o =>
                {
                    o.DataDirectory = DataDirectory;
                    o.SnapshotIntervalSeconds = 3600;
                }));

            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
    }

    private sealed record Client(HttpClient Http, AccountKeyPair Keys, string AccountId, string ShareCode);

    private async Task<Client> RegisterAsync(string displayName)
    {
        var http = factory.CreateClient();
        var keys = AccountKeyPair.Create();

        var response = await http.PostAsJsonAsync("/v1/accounts", new RegisterRequest
        {
            DisplayName = displayName,
            PublicKey = keys.ExportPublicKeyBase64(),
            ClientVersion = "tests",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(body);

        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(ProtocolConstants.AuthorizationScheme, body!.AccessToken);

        return new Client(http, keys, body.AccountId, body.ShareCode);
    }

    [Fact]
    public async Task InfoIsAvailableWithoutAnAccount()
    {
        var http = factory.CreateClient();

        var info = await http.GetFromJsonAsync<ServerInfo>("/v1/info");

        Assert.NotNull(info);
        Assert.Equal(ProtocolConstants.Version, info!.Protocol);
        Assert.True(info.EndToEndEncryptedOnly);
    }

    [Fact]
    public async Task PresenceRequiresAnAccessToken()
    {
        var http = factory.CreateClient();

        var response = await http.GetAsync("/v1/presence");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownShareCodeIsRejected()
    {
        var alice = await RegisterAsync("Alice");

        var response = await alice.Http.PostAsJsonAsync("/v1/contacts/requests",
            new CreateContactRequest { ShareCode = "FR-0000-0000-0000-0000" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("unknown_code", error?.Code);
    }

    [Fact]
    public async Task TwoClientsLinkAndExchangeEncryptedPresence()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        // Alice invites Bob; nothing flows until Bob accepts.
        var invite = await alice.Http.PostAsJsonAsync("/v1/contacts/requests",
            new CreateContactRequest { ShareCode = bob.ShareCode, Message = "raid night?" });
        invite.EnsureSuccessStatusCode();

        Assert.Empty(await alice.Http.GetFromJsonAsync<List<ContactDto>>("/v1/contacts") ?? []);

        var requests = await bob.Http.GetFromJsonAsync<List<ContactRequestDto>>("/v1/contacts/requests");
        var incoming = Assert.Single(requests!);
        Assert.Equal(ContactRequestDirection.Incoming, incoming.Direction);
        Assert.Equal("raid night?", incoming.Message);

        var accept = await bob.Http.PostAsync($"/v1/contacts/requests/{incoming.RequestId}/accept", null);
        accept.EnsureSuccessStatusCode();

        var contacts = await alice.Http.GetFromJsonAsync<List<ContactDto>>("/v1/contacts");
        var contact = Assert.Single(contacts!);
        Assert.Equal(bob.AccountId, contact.AccountId);
        Assert.Equal(bob.Keys.ExportPublicKeyBase64(), contact.PublicKey);

        // Alice publishes an encrypted snapshot addressed to Bob.
        var payload = new PresencePayload
        {
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CharacterName = "Alice Aliapoh",
            TerritoryTypeId = 129,
            Activity = ActivityKind.Fishing,
            ActivityDetail = "Fishing in Limsa Lominsa",
            X = 10f,
            Y = 20f,
            Z = 30f,
        };

        var outboundKey = PresenceCrypto.DeriveKey(alice.Keys, contact.PublicKey, alice.AccountId, bob.AccountId);
        var envelope = PresenceCrypto.Seal(outboundKey, payload, alice.AccountId, bob.AccountId);

        var publish = await alice.Http.PostAsJsonAsync("/v1/presence", new PublishPresenceRequest
        {
            TtlSeconds = 120,
            Envelopes = [envelope],
        });

        publish.EnsureSuccessStatusCode();
        var publishResult = await publish.Content.ReadFromJsonAsync<PublishPresenceResponse>();
        Assert.Equal(1, publishResult!.Accepted);

        // Bob reads it back and decrypts it.
        var fetched = await bob.Http.GetFromJsonAsync<FetchPresenceResponse>("/v1/presence");
        var received = Assert.Single(fetched!.Entries);
        Assert.Equal(alice.AccountId, received.SenderAccountId);
        Assert.Equal("Alice", received.SenderDisplayName);

        // Bob derives the key from Alice's public key exactly as the plugin would: out of his contact list.
        var bobsContacts = await bob.Http.GetFromJsonAsync<List<ContactDto>>("/v1/contacts");
        var alicesEntry = Assert.Single(bobsContacts!);
        Assert.Equal(alice.Keys.ExportPublicKeyBase64(), alicesEntry.PublicKey);

        var inboundKey = PresenceCrypto.DeriveKey(
            bob.Keys, alicesEntry.PublicKey, alice.AccountId, bob.AccountId);
        var decrypted = PresenceCrypto.Open(
            inboundKey, received.Nonce, received.Ciphertext, alice.AccountId, bob.AccountId);

        Assert.Equal(payload, decrypted);

        // And the relay itself is holding nothing but opaque base64.
        Assert.DoesNotContain("Alice Aliapoh", received.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("Fishing", received.Ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAnAccountRevokesItsToken()
    {
        var alice = await RegisterAsync("Alice");

        (await alice.Http.DeleteAsync("/v1/me")).EnsureSuccessStatusCode();

        var response = await alice.Http.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
