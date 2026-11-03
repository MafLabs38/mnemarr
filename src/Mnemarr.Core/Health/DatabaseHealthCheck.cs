using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Health;

/// <summary>
/// Readiness: the database opens and has the schema this version expects, and somebody can sign in. A database that cannot be opened is
/// unhealthy; one with no account at all, or with the break-glass reset left set, is degraded, with the reason. The
/// description never reveals a path.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabase _database;
    private readonly IAccountStore _accounts;
    private readonly IOptions<AuthOptions> _auth;

    public DatabaseHealthCheck(IDatabase database, IAccountStore accounts, IOptions<AuthOptions> auth)
    {
        _database = database;
        _accounts = accounts;
        _auth = auth;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != Schema.Version)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("The database does not have the schema of this version."));
            }

            if (_accounts.Count() == 0)
            {
                return Task.FromResult(HealthCheckResult.Degraded("No account exists: the first administrator is not configured, nobody can sign in."));
            }

            // Left set, the break-glass reset puts the password back at every start: it is for one start only.
            return Task.FromResult(string.IsNullOrWhiteSpace(_auth.Value.ResetPasswordUser)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("The break-glass password reset is still set: remove it, it resets the password at every start."));
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The database cannot be opened.", ex));
        }
    }
}
