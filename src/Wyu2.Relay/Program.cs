using System.Threading.RateLimiting;
using Wyu2.Protocol;
using Wyu2.Relay;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RelayStore>();
builder.Services.AddSingleton<AccountEndpointFilter>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RelayOptions>>().Value;

        // Authenticated callers get their own bucket keyed on the token hash, so one noisy client cannot
        // starve everybody behind the same NAT.
        var token = AccountEndpointFilter.ReadBearerToken(context.Request);
        var partition = token is not null
            ? "t:" + RelayStore.HashToken(token)
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(30, options.RequestsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();
var relayOptions = app.Services.GetRequiredService<IOptions<RelayOptions>>().Value;
var relayVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/info", (RelayStore store) => Results.Ok(new ServerInfo
{
    Name = relayOptions.Name,
    Operator = relayOptions.Operator,
    Message = relayOptions.Message,
    Version = relayVersion,
    Protocol = ProtocolConstants.Version,
    MaxPresenceTtlSeconds = ProtocolConstants.MaxPresenceTtlSeconds,
    SuggestedPublishIntervalSeconds = relayOptions.SuggestedPublishIntervalSeconds,
    MaxContacts = relayOptions.MaxContacts,
    RegistrationOpen = relayOptions.RegistrationOpen &&
                       (relayOptions.MaxAccounts <= 0 || store.AccountCount < relayOptions.MaxAccounts),
    EndToEndEncryptedOnly = true,
}));

// ---------------------------------------------------------------- accounts

app.MapPost("/v1/accounts", (RegisterRequest request, HttpContext http, RelayStore store) =>
{
    var clientVersion = http.Request.Headers[ProtocolConstants.ClientVersionHeader].ToString();
    var (result, response) = store.Register(request, string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion);

    return result switch
    {
        StoreResult.Ok => Results.Ok(response),
        StoreResult.RegistrationClosed => Fail(403, "registration_closed", "This relay is invite only."),
        StoreResult.LimitReached => Fail(503, "relay_full", "This relay is not accepting new accounts."),
        _ => Fail(400, "invalid_request", "Display name and public key are required."),
    };
});

app.MapGet("/v1/me", (HttpContext http, RelayStore store) => Results.Ok(store.Describe(http.Account())))
    .RequireAccount();

app.MapPatch("/v1/me", (UpdateAccountRequest request, HttpContext http, RelayStore store) =>
{
    var account = http.Account();
    return store.UpdateAccount(account, request) == StoreResult.Ok
        ? Results.Ok(store.Describe(account))
        : Fail(400, "invalid_request", "Display name or public key was rejected.");
}).RequireAccount();

app.MapDelete("/v1/me", (HttpContext http, RelayStore store) =>
{
    store.DeleteAccount(http.Account());
    store.Save(force: true);
    return Results.NoContent();
}).RequireAccount();

// ---------------------------------------------------------------- contacts

app.MapGet("/v1/contacts", (HttpContext http, RelayStore store) => Results.Ok(store.ListContacts(http.Account())))
    .RequireAccount();

app.MapDelete("/v1/contacts/{contactId}", (string contactId, HttpContext http, RelayStore store) =>
    store.RemoveContact(http.Account(), contactId) == StoreResult.Ok
        ? Results.NoContent()
        : Fail(404, "not_found", "You are not linked with that account."))
    .RequireAccount();

app.MapGet("/v1/contacts/requests", (HttpContext http, RelayStore store) =>
    Results.Ok(store.ListRequests(http.Account())))
    .RequireAccount();

app.MapPost("/v1/contacts/requests", (CreateContactRequest body, HttpContext http, RelayStore store) =>
{
    var (result, linked, request) = store.RequestContact(http.Account(), body);
    return result switch
    {
        StoreResult.Ok when linked => Results.Ok(new { linked = true }),
        StoreResult.Ok => Results.Ok(new { linked = false, request }),
        StoreResult.NotFound => Fail(404, "unknown_code", "No account uses that share code."),
        StoreResult.AlreadyExists => Fail(409, "already_linked", "You already sent that invite, or you are already linked."),
        StoreResult.LimitReached => Fail(429, "limit_reached", "Contact or invite limit reached."),
        _ => Fail(400, "invalid_code", "That share code is not valid."),
    };
}).RequireAccount();

app.MapPost("/v1/contacts/requests/{requestId}/accept", (string requestId, HttpContext http, RelayStore store) =>
    store.AcceptRequest(http.Account(), requestId) switch
    {
        StoreResult.Ok => Results.Ok(new { linked = true }),
        StoreResult.LimitReached => Fail(429, "limit_reached", "Contact limit reached."),
        _ => Fail(404, "not_found", "No such invite."),
    })
    .RequireAccount();

app.MapPost("/v1/contacts/requests/{requestId}/decline", (string requestId, HttpContext http, RelayStore store) =>
    store.DeclineRequest(http.Account(), requestId) == StoreResult.Ok
        ? Results.NoContent()
        : Fail(404, "not_found", "No such invite."))
    .RequireAccount();

// ---------------------------------------------------------------- groups

app.MapGet("/v1/groups", (HttpContext http, RelayStore store) => Results.Ok(store.ListGroups(http.Account())))
    .RequireAccount();

app.MapPost("/v1/groups", (CreateGroupRequest body, HttpContext http, RelayStore store) =>
{
    var (result, group) = store.CreateGroup(http.Account(), body);
    return result switch
    {
        StoreResult.Ok => Results.Ok(group),
        StoreResult.LimitReached => Fail(429, "limit_reached", "You are in as many groups as this relay allows."),
        _ => Fail(400, "invalid_request", "That group name was rejected."),
    };
}).RequireAccount();

app.MapPost("/v1/groups/join", (JoinGroupRequest body, HttpContext http, RelayStore store) =>
{
    var (result, group) = store.JoinGroup(http.Account(), body);
    return result switch
    {
        StoreResult.Ok or StoreResult.AlreadyExists => Results.Ok(group),
        StoreResult.NotFound => Fail(404, "unknown_code", "No group uses that join code."),
        StoreResult.LimitReached => Fail(429, "limit_reached", "That group is full, or you are in too many."),
        _ => Fail(400, "invalid_code", "That join code is not valid."),
    };
}).RequireAccount();

app.MapPatch("/v1/groups/{groupId}", (string groupId, UpdateGroupRequest body, HttpContext http, RelayStore store) =>
    store.UpdateGroup(http.Account(), groupId, body) switch
    {
        StoreResult.Ok => Results.NoContent(),
        StoreResult.Invalid => Fail(403, "not_owner", "Only the group's owner can change it."),
        _ => Fail(404, "not_found", "No such group."),
    })
    .RequireAccount();

app.MapDelete("/v1/groups/{groupId}", (string groupId, HttpContext http, RelayStore store) =>
    store.LeaveGroup(http.Account(), groupId) == StoreResult.Ok
        ? Results.NoContent()
        : Fail(404, "not_found", "You are not in that group."))
    .RequireAccount();

app.MapDelete("/v1/groups/{groupId}/members/{memberId}",
    (string groupId, string memberId, HttpContext http, RelayStore store) =>
        store.LeaveGroup(http.Account(), groupId, memberId) switch
        {
            StoreResult.Ok => Results.NoContent(),
            StoreResult.Invalid => Fail(403, "not_owner", "Only the group's owner can remove members."),
            _ => Fail(404, "not_found", "No such group or member."),
        })
    .RequireAccount();

// ---------------------------------------------------------------- presence

app.MapPost("/v1/presence", (PublishPresenceRequest request, HttpContext http, RelayStore store) =>
    Results.Ok(store.PublishPresence(http.Account(), request)))
    .RequireAccount();

app.MapGet("/v1/presence", (HttpContext http, RelayStore store) =>
    Results.Ok(store.FetchPresence(http.Account())))
    .RequireAccount();

app.MapDelete("/v1/presence", (HttpContext http, RelayStore store) =>
{
    store.ClearOwnPresence(http.Account());
    return Results.NoContent();
}).RequireAccount();

app.Run();

static IResult Fail(int status, string code, string message)
    => Results.Json(new ApiError { Code = code, Message = message }, statusCode: status);

/// <summary>Exposed so the test project can spin the relay up in-process.</summary>
public partial class Program;
