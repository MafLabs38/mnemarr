using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Accounts;

/// <summary>Settings of the access control, from the <c>Auth</c> section.</summary>
public sealed class AuthOptions
{
    /// <summary>The first administrator, created at start when there is no account yet.</summary>
    public string? AdminUser { get; set; }

    /// <summary>The file that holds the password of the first administrator (a secret mounted as a file, never an environment variable).</summary>
    public string? AdminPasswordFile { get; set; }

    /// <summary><c>Always</c> (the default) or <c>SameAsRequest</c>, for a plain-HTTP sandbox: whether the session cookie is sent over HTTPS only.</summary>
    public string CookieSecurity { get; set; } = "Always";

    /// <summary>How long a session lasts without being used, in hours.</summary>
    public int SessionHours { get; set; } = 12;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        if (options.CookieSecurity is not ("Always" or "SameAsRequest"))
        {
            return ValidateOptionsResult.Fail("Auth:CookieSecurity (MNEMARR__AUTH__COOKIESECURITY) must be Always or SameAsRequest.");
        }

        if (options.SessionHours is < 1 or > 720)
        {
            return ValidateOptionsResult.Fail("Auth:SessionHours (MNEMARR__AUTH__SESSIONHOURS) must be between 1 and 720.");
        }

        return string.IsNullOrWhiteSpace(options.AdminUser) == string.IsNullOrWhiteSpace(options.AdminPasswordFile)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Auth:AdminUser and Auth:AdminPasswordFile (MNEMARR__AUTH__ADMINUSER, MNEMARR__AUTH__ADMINPASSWORDFILE) are set together or not at all.");
    }
}
