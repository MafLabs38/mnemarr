using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Creates the first administrator at start (ADR 0016): only when there is no account yet, from the name in the settings and the
/// password in the file they name. Without them, an empty Mnemarr has no way in, and says so. The break-glass setting resets the password
/// of a named user at start. A setting that is wrong stops the start with a message that says which.
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
            Reset();
            Seed();
        }
        catch (Exception ex) when (StorageErrors.IsUnavailable(ex))
        {
            // A data folder that cannot hold the database is what /health/ready reports; the host stays up so that it can say so.
            LogUnavailable(ex);
        }
    }

    private void Reset()
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ResetPasswordUser) || string.IsNullOrWhiteSpace(options.ResetPasswordFile))
        {
            return;
        }

        try
        {
            _service.Reset(options.ResetPasswordUser, ReadPassword(options.ResetPasswordFile, "reset"));
        }
        catch (AccountRejectedException ex)
        {
            throw new InvalidOperationException($"The password reset of '{options.ResetPasswordUser}' was refused: {ex.Message}", ex);
        }

        LogReset(options.ResetPasswordUser);
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

        try
        {
            _service.Create(options.AdminUser, AccountKind.User, Roles.Admin, ReadPassword(options.AdminPasswordFile, "first administrator"), "system");
        }
        catch (AccountRejectedException ex)
        {
            throw new InvalidOperationException($"The first administrator was refused: {ex.Message}", ex);
        }

        LogCreated(options.AdminUser);
    }

    private static string ReadPassword(string file, string purpose)
    {
        try
        {
            return File.ReadAllText(file).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The password file of the {purpose} cannot be read: {file}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "There is no account and no first administrator is configured: set MNEMARR__AUTH__ADMINUSER and MNEMARR__AUTH__ADMINPASSWORDFILE, or nobody can sign in.")]
    private partial void LogNoAdmin();

    [LoggerMessage(Level = LogLevel.Error, Message = "The database cannot be opened, so no account could be checked or created.")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first administrator {Name}.")]
    private partial void LogCreated(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The password of {Name} was reset from MNEMARR__AUTH__RESETPASSWORDFILE. Remove MNEMARR__AUTH__RESETPASSWORDUSER and MNEMARR__AUTH__RESETPASSWORDFILE now: while they are set, every start resets it again.")]
    private partial void LogReset(string name);
}
