namespace Mnemarr.Core.Accounts;

/// <summary>
/// A key of an account. The secret is shown once and only its hash is kept; <see cref="Prefix"/> is what a person recognises it by.
/// <see cref="Sources"/> narrows the key to those sources (null: all those its account's role allows).
/// </summary>
public sealed record ApiKey(
    Guid Id,
    Guid AccountId,
    string Name,
    string Prefix,
    IReadOnlyList<Guid>? Sources,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>A key that has just been created, with its secret: the only time it is available.</summary>
public sealed record CreatedApiKey(ApiKey Key, string Secret);

public interface IApiKeyStore
{
    void Add(ApiKey key, string secretHash);

    /// <summary>The key whose secret has this hash, revoked or not.</summary>
    ApiKey? FindByHash(string secretHash);

    IReadOnlyList<ApiKey> List(Guid accountId);

    /// <summary>Revokes a key of an account. False when there was none, or it was revoked already.</summary>
    bool Revoke(Guid accountId, Guid keyId, DateTimeOffset at);

    void Touch(Guid keyId, DateTimeOffset at);
}
