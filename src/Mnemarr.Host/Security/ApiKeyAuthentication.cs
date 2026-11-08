using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Chat;

namespace Mnemarr.Host.Security;

/// <summary>Where a request carries an API key: <c>Authorization: Bearer mnm_…</c> or <c>X-Api-Key: mnm_…</c>.</summary>
public static class ApiKeyHeader
{
    public const string Scheme = "Mnemarr.ApiKey";
    private const string HeaderName = "X-Api-Key";
    private const string Bearer = "Bearer ";

    /// <summary>The key of the request, or null when it carries none. Anything that does not look like one of ours is not read here.</summary>
    public static string? Read(HttpRequest request)
    {
        var header = request.Headers[HeaderName].ToString();
        if (IsOurs(header))
        {
            return header;
        }

        // The scheme name is case-insensitive (RFC 7235); the key itself is not.
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[Bearer.Length..];
        return IsOurs(token) ? token : null;
    }

    private static bool IsOurs(string secret) =>
        secret.StartsWith(ApiKeyService.SecretPrefix, StringComparison.Ordinal) || secret.StartsWith(ChatAccess.TokenPrefix, StringComparison.Ordinal);
}

/// <summary>Authenticates a request that carries an API key. A key that is not valid is a 401: it never falls back to a cookie.</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string McpPath = "/mcp";

    private readonly ApiKeyService _keys;
    private readonly ChatAccess _chat;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys, ChatAccess chat)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _chat = chat;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = ApiKeyHeader.Read(Request);
        if (secret is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = _keys.Authenticate(secret);
        if (principal is null)
        {
            // Not a key of Mnemarr: it may be the token that the chat's runner holds for a person, which a key never is.
            return Task.FromResult(AuthenticateChatToken(secret));
        }

        var identity = MnemarrClaims.Identity(principal, ApiKeyHeader.Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyHeader.Scheme)));
    }

    /// <summary>
    /// The token that the chat's runner presents for a person (ADR 0035): it opens the MCP server and nothing else, whatever the person may do elsewhere, and what it may do there is the person's
    /// role as it is now. The audit says the chat acted.
    /// </summary>
    private AuthenticateResult AuthenticateChatToken(string token)
    {
        if (!Request.Path.StartsWithSegments(McpPath, StringComparison.OrdinalIgnoreCase) || _chat.Authenticate(token) is not { } identity)
        {
            return AuthenticateResult.Fail("The key is not valid.");
        }

        var claims = MnemarrClaims.Identity(identity.Principal, ApiKeyHeader.Scheme, new Claim(MnemarrClaims.Via, $"chat:{identity.ConversationId}"));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(claims), ApiKeyHeader.Scheme));
    }
}
