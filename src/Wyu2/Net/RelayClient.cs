using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Wyu2.Protocol;

namespace Wyu2.Net;

public enum RelayState
{
    Disabled,
    NotRegistered,
    Connecting,
    Online,
    Error,
}

/// <summary>
/// Thin async wrapper over the relay's HTTP API. Everything here runs off the framework thread; callers
/// marshal results back themselves.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Configuration.Configuration config;
    private readonly string clientVersion;
    private readonly Lock httpLock = new();
    private HttpClient? http;
    private string? httpBaseUrl;

    public RelayClient(Configuration.Configuration config, string clientVersion)
    {
        this.config = config;
        this.clientVersion = clientVersion;
    }

    public RelayState State { get; private set; } = RelayState.Disabled;

    /// <summary>Last failure, shown in the UI. Null when the last call succeeded.</summary>
    public string? LastError { get; private set; }

    public DateTime? LastSuccessAt { get; private set; }

    /// <summary>Info reported by the relay, refreshed whenever the URL changes.</summary>
    public ServerInfo? ServerInfo { get; private set; }

    /// <summary>
    /// Drops the underlying client, e.g. after the relay URL or the credentials changed. Requests already
    /// in flight fail, which callers treat as an ordinary transport error.
    /// </summary>
    public void Invalidate()
    {
        lock (httpLock)
        {
            http?.Dispose();
            http = null;
            httpBaseUrl = null;
            ServerInfo = null;
        }
    }

    /// <summary>
    /// The client for the configured relay, created once per URL. Publishing, fetching and contact
    /// syncing all run concurrently, so this is locked and the auth header is set per request rather
    /// than on the shared instance.
    /// </summary>
    private HttpClient? GetClient(bool requireAuth)
    {
        if (string.IsNullOrWhiteSpace(config.RelayUrl))
        {
            State = RelayState.Disabled;
            return null;
        }

        if (requireAuth && string.IsNullOrEmpty(config.AccessToken))
        {
            State = RelayState.NotRegistered;
            return null;
        }

        var baseUrl = config.RelayUrl.TrimEnd('/');

        lock (httpLock)
        {
            if (http is not null && httpBaseUrl == baseUrl)
                return http;

            if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                State = RelayState.Error;
                LastError = "Relay URL must be an http(s) address.";
                return null;
            }

            http?.Dispose();
            http = new HttpClient
            {
                BaseAddress = uri,
                Timeout = TimeSpan.FromSeconds(15),
            };
            http.DefaultRequestHeaders.Add(ProtocolConstants.ProtocolVersionHeader, ProtocolConstants.Version.ToString());
            http.DefaultRequestHeaders.Add(ProtocolConstants.ClientVersionHeader, clientVersion);
            httpBaseUrl = baseUrl;
            return http;
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        var token = config.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                ProtocolConstants.AuthorizationScheme, token);
        }
    }

    // ---------------------------------------------------------------- calls

    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken token = default)
    {
        var info = await SendAsync<ServerInfo>(HttpMethod.Get, "v1/info", null, requireAuth: false, token)
            .ConfigureAwait(false);
        if (info is not null)
            ServerInfo = info;

        return info;
    }

    public Task<RegisterResponse?> RegisterAsync(RegisterRequest request, CancellationToken token = default)
        => SendAsync<RegisterResponse>(HttpMethod.Post, "v1/accounts", request, requireAuth: false, token);

    public Task<AccountInfo?> GetAccountAsync(CancellationToken token = default)
        => SendAsync<AccountInfo>(HttpMethod.Get, "v1/me", null, requireAuth: true, token);

    public Task<AccountInfo?> UpdateAccountAsync(UpdateAccountRequest request, CancellationToken token = default)
        => SendAsync<AccountInfo>(HttpMethod.Patch, "v1/me", request, requireAuth: true, token);

    public Task<bool> DeleteAccountAsync(CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, "v1/me", null, token);

    public Task<List<ContactDto>?> GetContactsAsync(CancellationToken token = default)
        => SendAsync<List<ContactDto>>(HttpMethod.Get, "v1/contacts", null, requireAuth: true, token);

    public Task<List<ContactRequestDto>?> GetContactRequestsAsync(CancellationToken token = default)
        => SendAsync<List<ContactRequestDto>>(HttpMethod.Get, "v1/contacts/requests", null, requireAuth: true, token);

    public Task<bool> SendContactRequestAsync(string shareCode, string? message, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Post, "v1/contacts/requests",
            new CreateContactRequest { ShareCode = shareCode, Message = message }, token);

    public Task<bool> AcceptContactRequestAsync(string requestId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Post, $"v1/contacts/requests/{requestId}/accept", null, token);

    public Task<bool> DeclineContactRequestAsync(string requestId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Post, $"v1/contacts/requests/{requestId}/decline", null, token);

    public Task<bool> RemoveContactAsync(string accountId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, $"v1/contacts/{accountId}", null, token);

    /// <summary>Publishes the current epoch key so contacts can encrypt to it.</summary>
    public Task<bool> PublishPrekeyAsync(PrekeyBundle bundle, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Post, "v1/me/prekey", new PublishPrekeyRequest { Bundle = bundle }, token);

    public Task<List<GroupDto>?> GetGroupsAsync(CancellationToken token = default)
        => SendAsync<List<GroupDto>>(HttpMethod.Get, "v1/groups", null, requireAuth: true, token);

    public Task<GroupDto?> CreateGroupAsync(string name, CancellationToken token = default)
        => SendAsync<GroupDto>(HttpMethod.Post, "v1/groups", new CreateGroupRequest { Name = name },
            requireAuth: true, token);

    public Task<GroupDto?> JoinGroupAsync(string joinCode, CancellationToken token = default)
        => SendAsync<GroupDto>(HttpMethod.Post, "v1/groups/join", new JoinGroupRequest { JoinCode = joinCode },
            requireAuth: true, token);

    public Task<bool> UpdateGroupAsync(string groupId, UpdateGroupRequest request, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Patch, $"v1/groups/{groupId}", request, token);

    public Task<bool> LeaveGroupAsync(string groupId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, $"v1/groups/{groupId}", null, token);

    public Task<bool> RemoveGroupMemberAsync(string groupId, string accountId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, $"v1/groups/{groupId}/members/{accountId}", null, token);

    public Task<PublishPresenceResponse?> PublishPresenceAsync(
        PublishPresenceRequest request, CancellationToken token = default)
        => SendAsync<PublishPresenceResponse>(HttpMethod.Post, "v1/presence", request, requireAuth: true, token);

    public Task<FetchPresenceResponse?> FetchPresenceAsync(CancellationToken token = default)
        => SendAsync<FetchPresenceResponse>(HttpMethod.Get, "v1/presence", null, requireAuth: true, token);

    /// <summary>Wipes anything the relay is still holding for us, used by the panic switch.</summary>
    public Task<bool> ClearPresenceAsync(CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, "v1/presence", null, token);

    public Task<PublishBeaconResponse?> PublishBeaconAsync(
        PublishBeaconRequest request, CancellationToken token = default)
        => SendAsync<PublishBeaconResponse>(HttpMethod.Post, "v1/beacons", request, requireAuth: true, token);

    public Task<FetchBeaconsResponse?> FetchBeaconsAsync(CancellationToken token = default)
        => SendAsync<FetchBeaconsResponse>(HttpMethod.Get, "v1/beacons", null, requireAuth: true, token);

    /// <summary>Takes one of our beacons back from everybody it was sent to.</summary>
    public Task<bool> WithdrawBeaconAsync(string beaconId, CancellationToken token = default)
        => SendVoidAsync(HttpMethod.Delete, $"v1/beacons/{Uri.EscapeDataString(beaconId)}", null, token);

    // ---------------------------------------------------------------- plumbing

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? body, bool requireAuth, CancellationToken token)
        where T : class
    {
        var client = GetClient(requireAuth);
        if (client is null)
            return null;

        try
        {
            State = RelayState.Connecting;
            using var request = new HttpRequestMessage(method, path);
            Authorize(request);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);

            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordFailureAsync(response, token).ConfigureAwait(false);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false);
            MarkSuccess();
            return result;
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            State = RelayState.Error;
            LastError = Describe(ex);
            return null;
        }
    }

    private async Task<bool> SendVoidAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        var client = GetClient(requireAuth: true);
        if (client is null)
            return false;

        try
        {
            State = RelayState.Connecting;
            using var request = new HttpRequestMessage(method, path);
            Authorize(request);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);

            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordFailureAsync(response, token).ConfigureAwait(false);
                return false;
            }

            MarkSuccess();
            return true;
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            State = RelayState.Error;
            LastError = Describe(ex);
            return false;
        }
    }

    private void MarkSuccess()
    {
        State = RelayState.Online;
        LastError = null;
        LastSuccessAt = DateTime.UtcNow;
    }

    private async Task RecordFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        State = response.StatusCode == HttpStatusCode.Unauthorized ? RelayState.NotRegistered : RelayState.Error;

        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json, token).ConfigureAwait(false);
            LastError = error is null || error.IsEmpty
                ? $"Relay returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : error.Message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            LastError = $"Relay returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
    }

    /// <summary>
    /// Failures worth turning into a status line rather than letting escape. ObjectDisposedException
    /// happens when the URL changes while a request is in flight, which is not an error worth shouting
    /// about.
    /// </summary>
    private static bool IsTransport(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or JsonException
            or ObjectDisposedException or OperationCanceledException or NotSupportedException;

    private static string Describe(Exception ex) => ex switch
    {
        ObjectDisposedException => "The connection was replaced; retrying shortly.",
        TaskCanceledException => "The relay did not answer in time.",
        HttpRequestException when IsTlsFailure(ex) =>
            "The TLS handshake with the relay failed. If nothing is terminating HTTPS in front of it, " +
            "use an http:// URL; if something is, its certificate is missing, expired, or issued for a " +
            "different name.",
        HttpRequestException http => $"Could not reach the relay: {http.Message}",
        JsonException => "The relay sent a response this plugin could not read.",
        _ => ex.Message,
    };

    /// <summary>
    /// True when the failure was the TLS handshake rather than the request. Worth separating: the usual
    /// cause is an https:// URL pointed at a relay that is only listening on plain HTTP, and the raw
    /// exception text for that is famously unhelpful.
    /// </summary>
    private static bool IsTlsFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException)
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        lock (httpLock)
        {
            http?.Dispose();
            http = null;
        }
    }
}
