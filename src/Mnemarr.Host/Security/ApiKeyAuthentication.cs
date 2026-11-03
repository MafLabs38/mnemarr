using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

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
        if (header.StartsWith(ApiKeyService.SecretPrefix, StringComparison.Ordinal))
        {
            return header;
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith(Bearer + ApiKeyService.SecretPrefix, StringComparison.Ordinal) ? authorization[Bearer.Length..] : null;
    }
}

/// <summary>Authenticates a request that carries an API key. A key that is not valid is a 401: it never falls back to a cookie.</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiKeyService _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
        : base(options, logger, encoder)
    {
        _keys = keys;
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
            return Task.FromResult(AuthenticateResult.Fail("The key is not valid."));
        }

        var identity = new ClaimsIdentity(MnemarrClaims.For(principal), ApiKeyHeader.Scheme, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyHeader.Scheme)));
    }
}
