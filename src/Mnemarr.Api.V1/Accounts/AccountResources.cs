using System.Text.Json.Serialization;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

public sealed record AccountResource(Guid Id, string Name, string Kind, string Role, bool Enabled, DateTimeOffset CreatedAt)
{
    public static AccountResource From(Account account) =>
        new(account.Id, account.Name, account.Kind.ToString(), account.Role, account.Enabled, account.CreatedAt);
}

public sealed record RoleResource(string Name, string Description, IReadOnlyList<string> Permissions, bool ForUsers, bool ForServices)
{
    public static RoleResource From(Role role) =>
        new(role.Name, role.Description, [.. role.Permissions.Order(StringComparer.Ordinal)], role.ForUsers, role.ForServices);
}

public sealed record ApiKeyResource(
    Guid Id, string Name, string Prefix, IReadOnlyList<Guid>? Sources, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt)
{
    public static ApiKeyResource From(ApiKey key) =>
        new(key.Id, key.Name, key.Prefix, key.Sources, key.CreatedAt, key.ExpiresAt, key.LastUsedAt, key.RevokedAt);
}

/// <summary>A key that has just been created: the secret is in this answer and nowhere else, ever.</summary>
public sealed record CreatedApiKeyResource(ApiKeyResource Key, string Secret);

public sealed record SessionResource(Guid AccountId, string Name, string Kind, string Role, IReadOnlyList<string> Permissions);

public sealed record AuditEventResource(long Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail)
{
    public static AuditEventResource From(AuditEvent e) => new(e.Id, e.At, e.Actor, e.Action, e.Target, e.Detail);
}

public sealed record LoginRequest(string Name, string Password);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CreateAccountRequest(string Name, string Kind, string Role, string? Password);

public sealed record UpdateAccountRequest(string Role, [property: JsonRequired] bool Enabled);

public sealed record SetPasswordRequest(string Password);

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<Guid>? Sources, DateTimeOffset? ExpiresAt);
