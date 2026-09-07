using Wyu2.Protocol;

namespace Wyu2.Relay;

/// <summary>
/// Resolves the bearer token into an <see cref="Account"/> and stashes it for the endpoint. Endpoints that
/// need a caller add <c>.RequireAccount()</c> and read it back with <see cref="HttpContextExtensions.Account"/>.
/// </summary>
public sealed class AccountEndpointFilter(RelayStore store) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = ReadBearerToken(http.Request);
        if (token is null)
            return Results.Json(new ApiError { Code = "unauthorized", Message = "Missing access token." }, statusCode: 401);

        var clientVersion = http.Request.Headers[ProtocolConstants.ClientVersionHeader].ToString();
        var account = store.Authenticate(token, string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion);
        if (account is null)
            return Results.Json(new ApiError { Code = "unauthorized", Message = "Unknown access token." }, statusCode: 401);

        http.Items[HttpContextExtensions.AccountKey] = account;
        return await next(context);
    }

    public static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string prefix = ProtocolConstants.AuthorizationScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[prefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}

public static class HttpContextExtensions
{
    internal const string AccountKey = "wyu2.account";

    /// <summary>The authenticated account. Only valid inside an endpoint guarded by the account filter.</summary>
    public static Account Account(this HttpContext context)
        => (Account)context.Items[AccountKey]!;

    public static RouteHandlerBuilder RequireAccount(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<AccountEndpointFilter>();
}
