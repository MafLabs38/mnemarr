using System.Security.Cryptography;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// The keys of the accounts. A key is 256 random bits shown once and kept as a hash. It never goes beyond its account's role as it is
/// now, and may be narrowed to some sources (ADR 0016). Using one is recorded as its last use, at most once a minute.
/// </summary>
public sealed class ApiKeyService
{
    public const string SecretPrefix = "mnm_";
    private const int PrefixLength = 12;
    private const int MaxName = 100;
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
    private readonly IAccountStore _accounts;
    private readonly IApiKeyStore _keys;
    private readonly IAuditLog _audit;
    private readonly AuditSampler _sampler;
    private readonly TimeProvider _clock;

    public ApiKeyService(IAccountStore accounts, IApiKeyStore keys, IAuditLog audit, AuditSampler sampler, TimeProvider clock)
    {
        _accounts = accounts;
        _keys = keys;
        _audit = audit;
        _sampler = sampler;
        _clock = clock;
    }

    public CreatedApiKey Create(Guid accountId, string name, IReadOnlyList<Guid>? sources, DateTimeOffset? expiresAt, string actor)
    {
        var account = _accounts.Get(accountId) ?? throw new AccountRejectedException(AccountProblem.NotFound, "There is no such account.");
        if (account.Kind != AccountKind.Service)
        {
            // A person signs in with a password; a key on a user account would be a bearer credential that skips the lockout and the cookie's protections.
            throw new AccountRejectedException(AccountProblem.Invalid, "Only a service account has keys: a person signs in with a password.");
        }

        name = name.Trim();
        if (name.Length is 0 or > MaxName)
        {
            throw new AccountRejectedException(AccountProblem.Invalid, $"The name of a key is 1 to {MaxName} characters.");
        }

        if (sources is { Count: 0 })
        {
            throw new AccountRejectedException(AccountProblem.Invalid, "A key limited to sources needs at least one: leave the limit out for all of them.");
        }

        var now = _clock.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new AccountRejectedException(AccountProblem.Invalid, "A key cannot expire in the past.");
        }

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey(Guid.NewGuid(), accountId, name, secret[..PrefixLength], sources?.Distinct().ToList(), now, expiresAt, null, null);
        _keys.Add(key, SecretHash.Of(secret));
        _audit.Append(actor, "key.created", account.Name, $"{key.Name} ({key.Prefix})");
        return new CreatedApiKey(key, secret);
    }

    public IReadOnlyList<ApiKey> List(Guid accountId) => _keys.List(accountId);

    public void Revoke(Guid accountId, Guid keyId, string actor)
    {
        var account = _accounts.Get(accountId) ?? throw new AccountRejectedException(AccountProblem.NotFound, "There is no such account.");
        if (!_keys.Revoke(accountId, keyId, _clock.GetUtcNow()))
        {
            throw new AccountRejectedException(AccountProblem.NotFound, "There is no such key, or it is revoked already.");
        }

        _audit.Append(actor, "key.revoked", account.Name, keyId.ToString());
    }

    /// <summary>The principal behind a secret, or null when it is unknown, revoked, expired, or its account is disabled.</summary>
    public AccessPrincipal? Authenticate(string secret)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var key = _keys.FindByHash(SecretHash.Of(secret));
        var now = _clock.GetUtcNow();
        if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now)
        {
            var prefix = secret[..Math.Min(PrefixLength, secret.Length)];
            if (_sampler.Allow("key.refused", prefix))
            {
                _audit.Append(AuditActors.Unknown, "key.refused", prefix);
            }

            return null;
        }

        var account = _accounts.Get(key.AccountId);
        if (account is not { Enabled: true, Kind: AccountKind.Service })
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt >= TouchEvery)
        {
            _keys.Touch(key.Id, now);
        }

        return new AccessPrincipal(account, account.Permissions, key.Sources, key.Id);
    }


    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
