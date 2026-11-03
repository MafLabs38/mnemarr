using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness of the access: somebody can sign in. No account at all, accounts whose role this version does not know, and a break-glass
/// reset left set are degraded, each with its reason: an empty Mnemarr nobody can enter is not a working one. A database that cannot be
/// read is the database check's to report; this one says nothing then.
/// </summary>
public sealed class AccountsHealthCheck : IHealthCheck
{
    private readonly IAccountStore _accounts;
    private readonly IOptions<AuthOptions> _auth;

    public AccountsHealthCheck(IAccountStore accounts, IOptions<AuthOptions> auth)
    {
        _accounts = accounts;
        _auth = auth;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Check());
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The accounts cannot be read.", ex));
        }
    }

    private HealthCheckResult Check()
    {
        var byRole = _accounts.CountByRole();
        if (byRole.Count == 0)
        {
            return Degraded("no-account", "No account exists: the first administrator is not configured, nobody can sign in.");
        }

        // A role is a name in code and a word in the database: one that was renamed or removed gives its accounts no permission at all.
        var unknownRoles = byRole.Where(pair => Roles.Find(pair.Key) is null).Sum(pair => pair.Value);
        if (unknownRoles > 0)
        {
            return Degraded("unknown-role", $"{unknownRoles} account(s) have a role that this version does not know, so they can do nothing.");
        }

        // Left set, the break-glass reset puts the password back at every start: it is for one start only.
        return string.IsNullOrWhiteSpace(_auth.Value.ResetPasswordUser)
            ? HealthCheckResult.Healthy()
            : Degraded("break-glass-set", "The break-glass password reset is still set: remove it, it resets the password at every start.");
    }

    /// <summary>A degraded result with a stable reason code, which the health resource of the API will carry (F-HLT-01), next to the words.</summary>
    private static HealthCheckResult Degraded(string reason, string description) =>
        HealthCheckResult.Degraded(description, data: new Dictionary<string, object> { ["reason"] = reason });
}
