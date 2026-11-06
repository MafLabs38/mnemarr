namespace Mnemarr.Core.Accounts;

/// <summary>What a password must be: long enough, and not the name of the account.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <summary>The reason a password is refused, or null when it is fine.</summary>
    public static string? Problem(string password, string name)
    {
        if (password.Length < MinLength)
        {
            return $"A password is at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"A password is at most {MaxLength} characters.";
        }

        return string.Equals(password, name, StringComparison.OrdinalIgnoreCase) ? "A password cannot be the name of the account." : null;
    }
}
