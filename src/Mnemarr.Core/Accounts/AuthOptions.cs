using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Accounts;

/// <summary>Whether the session cookie is sent over HTTPS only.</summary>
public enum CookieSecurityMode
{
    /// <summary>The default: the cookie is <c>Secure</c>.</summary>
    Always,

    /// <summary>For a plain-HTTP sandbox: the cookie is <c>Secure</c> only if the request was.</summary>
    SameAsRequest,
}

/// <summary>Settings of the access control, from the <c>Auth</c> section (see docs/configuration.md).</summary>
public sealed class AuthOptions
{
    public const int MaxSessionHours = 720;
    public const int MaxSessionDays = 365;
    public const int MaxRetentionDays = 3650;

    /// <summary>The first administrator, created at start when there is no account yet.</summary>
    public string? AdminUser { get; set; }

    /// <summary>The file that holds the password of the first administrator (a secret mounted as a file, never an environment variable).</summary>
    public string? AdminPasswordFile { get; set; }

    /// <summary>
    /// Break-glass: the account named here gets the password in <see cref="ResetPasswordFile"/>, is enabled, loses its lockout and its sessions, at
    /// every start while both are set. For the day the only administrator cannot sign in; remove both once used.
    /// </summary>
    public string? ResetPasswordUser { get; set; }

    public string? ResetPasswordFile { get; set; }

    public CookieSecurityMode CookieSecurity { get; set; } = CookieSecurityMode.Always;

    /// <summary>How long a session lasts without being used, in hours.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>How long a session lasts at most, used or not, in days.</summary>
    public int SessionMaxDays { get; set; } = 30;

    /// <summary>How long the audit log keeps its events, in days.</summary>
    public int AuditRetentionDays { get; set; } = 90;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        if (options.SessionHours is < 1 or > AuthOptions.MaxSessionHours)
        {
            return ValidateOptionsResult.Fail($"Auth:SessionHours (MNEMARR__AUTH__SESSIONHOURS) must be between 1 and {AuthOptions.MaxSessionHours}.");
        }

        if (options.SessionMaxDays is < 1 or > AuthOptions.MaxSessionDays)
        {
            return ValidateOptionsResult.Fail($"Auth:SessionMaxDays (MNEMARR__AUTH__SESSIONMAXDAYS) must be between 1 and {AuthOptions.MaxSessionDays}.");
        }

        if (options.AuditRetentionDays is < 1 or > AuthOptions.MaxRetentionDays)
        {
            return ValidateOptionsResult.Fail($"Auth:AuditRetentionDays (MNEMARR__AUTH__AUDITRETENTIONDAYS) must be between 1 and {AuthOptions.MaxRetentionDays}.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminUser) != string.IsNullOrWhiteSpace(options.AdminPasswordFile))
        {
            return ValidateOptionsResult.Fail("Auth:AdminUser and Auth:AdminPasswordFile (MNEMARR__AUTH__ADMINUSER, MNEMARR__AUTH__ADMINPASSWORDFILE) are set together or not at all.");
        }

        return string.IsNullOrWhiteSpace(options.ResetPasswordUser) == string.IsNullOrWhiteSpace(options.ResetPasswordFile)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Auth:ResetPasswordUser and Auth:ResetPasswordFile (MNEMARR__AUTH__RESETPASSWORDUSER, MNEMARR__AUTH__RESETPASSWORDFILE) are set together or not at all.");
    }
}
