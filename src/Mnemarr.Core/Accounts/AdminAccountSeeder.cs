using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Creates the first administrator at start (ADR 0016): only when there is no account yet, from the name in the settings and the
/// password in the file they name. Without them, an empty Mnemarr has no way in, and says so.
/// </summary>
public sealed partial class AdminAccountSeeder
{
    private readonly IAccountStore _accounts;
    private readonly AccountService _service;
    private readonly IOptions<AuthOptions> _options;
    private readonly ILogger<AdminAccountSeeder> _logger;

    public AdminAccountSeeder(IAccountStore accounts, AccountService service, IOptions<AuthOptions> options, ILogger<AdminAccountSeeder> logger)
    {
        _accounts = accounts;
        _service = service;
        _options = options;
        _logger = logger;
    }

    public void Run()
    {
        try
        {
            Seed();
        }
        catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
        {
            // A data folder that cannot hold the database is what /health/ready reports; the host stays up so that it can say so.
            LogUnavailable(ex);
        }
    }

    private void Seed()
    {
        if (_accounts.Count() > 0)
        {
            return;
        }

        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.AdminUser) || string.IsNullOrWhiteSpace(options.AdminPasswordFile))
        {
            LogNoAdmin();
            return;
        }

        var password = File.ReadAllText(options.AdminPasswordFile).Trim();
        _service.Create(options.AdminUser, AccountKind.User, Roles.Admin, password, "system");
        LogCreated(options.AdminUser);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "There is no account and no first administrator is configured: set MNEMARR__AUTH__ADMINUSER and MNEMARR__AUTH__ADMINPASSWORDFILE, or nobody can sign in.")]
    private partial void LogNoAdmin();

    [LoggerMessage(Level = LogLevel.Error, Message = "The database cannot be opened, so no account could be checked or created.")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first administrator {Name}.")]
    private partial void LogCreated(string name);
}
