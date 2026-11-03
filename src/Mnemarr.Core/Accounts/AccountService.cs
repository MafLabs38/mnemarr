using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// The accounts: creating them, signing in, changing a role, a password or the state of an account. It keeps the rules of ADR 0016:
/// a role is one of the built-in ones and fits the kind of account, a password is long enough, repeated failures lock a name,
/// the last enabled administrator stays, and what changes is audited.
/// </summary>
public sealed partial class AccountService
{
    private readonly IAccountStore _accounts;
    private readonly IAuditLog _audit;
    private readonly LoginThrottle _throttle;
    private readonly TimeProvider _clock;
    private readonly PasswordHasher<Account> _hasher = new();
    private readonly string _decoyHash;

    public AccountService(IAccountStore accounts, IAuditLog audit, LoginThrottle throttle, TimeProvider clock)
    {
        _accounts = accounts;
        _audit = audit;
        _throttle = throttle;
        _clock = clock;

        // Checked when the name is unknown, so that a wrong name and a wrong password take the same time.
        _decoyHash = _hasher.HashPassword(new Account(Guid.Empty, "decoy", AccountKind.User, Roles.Reader, false, DateTimeOffset.UnixEpoch), Guid.NewGuid().ToString("N"));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")]
    private static partial Regex NamePattern();

    public Account Create(string name, AccountKind kind, string role, string? password, string actor)
    {
        name = name.Trim();
        if (!NamePattern().IsMatch(name))
        {
            throw Invalid("A name is 2 to 64 letters, digits, dots, dashes or underscores, and starts with a letter or a digit.");
        }

        CheckRole(role, kind);
        string? hash = null;
        if (kind == AccountKind.User)
        {
            hash = _hasher.HashPassword(Placeholder(name), CheckedPassword(password, name));
        }
        else if (password is not null)
        {
            throw Invalid("A service account has no password: it has keys.");
        }

        var account = new Account(Guid.NewGuid(), name, kind, role, Enabled: true, _clock.GetUtcNow());
        _accounts.Add(account, hash);
        _audit.Append(actor, "account.created", name, $"{kind} with role {role}");
        return account;
    }

    /// <summary>The user who signed in, or null. A locked name is refused without checking the password, and without extending the lock.</summary>
    public Account? SignIn(string name, string password)
    {
        name = name.Trim();
        if (_throttle.IsLocked(name))
        {
            _audit.Append(name, "login.locked", name);
            throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.");
        }

        var credentials = _accounts.GetCredentials(name);
        var hash = credentials is { Account.Kind: AccountKind.User, PasswordHash: not null } ? credentials.PasswordHash : _decoyHash;
        var result = _hasher.VerifyHashedPassword(credentials?.Account ?? Placeholder(name), hash, password);
        if (credentials is null || !ReferenceEquals(hash, credentials.PasswordHash) || result == PasswordVerificationResult.Failed || !credentials.Account.Enabled)
        {
            _throttle.Failed(name);
            _audit.Append(name, "login.failed", name);
            return null;
        }

        _throttle.Succeeded(name);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            _accounts.SetPasswordHash(credentials.Account.Id, _hasher.HashPassword(credentials.Account, password));
        }

        _audit.Append(credentials.Account.Name, "login.succeeded", credentials.Account.Name);
        return credentials.Account;
    }

    public Account ChangeRole(Guid id, string role, string actor)
    {
        var account = Existing(id);
        CheckRole(role, account.Kind);
        KeepOneAdmin(account, stillAdmin: role == Roles.Admin && account.Enabled);
        _accounts.Update(id, role, account.Enabled);
        _audit.Append(actor, "account.role-changed", account.Name, $"{account.Role} to {role}");
        return account with { Role = role };
    }

    public Account SetEnabled(Guid id, bool enabled, string actor)
    {
        var account = Existing(id);
        KeepOneAdmin(account, stillAdmin: account.Role == Roles.Admin && enabled);
        _accounts.Update(id, account.Role, enabled);
        _audit.Append(actor, enabled ? "account.enabled" : "account.disabled", account.Name);
        return account with { Enabled = enabled };
    }

    public void SetPassword(Guid id, string password, string actor)
    {
        var account = Existing(id);
        if (account.Kind != AccountKind.User)
        {
            throw Invalid("A service account has no password: it has keys.");
        }

        _accounts.SetPasswordHash(id, _hasher.HashPassword(account, CheckedPassword(password, account.Name)));
        _throttle.Succeeded(account.Name);
        _audit.Append(actor, "account.password-changed", account.Name);
    }

    public void Delete(Guid id, string actor)
    {
        var account = Existing(id);
        KeepOneAdmin(account, stillAdmin: false);
        _accounts.Remove(id);
        _audit.Append(actor, "account.deleted", account.Name);
    }

    private Account Existing(Guid id) =>
        _accounts.Get(id) ?? throw new AccountRejectedException(AccountProblem.NotFound, "There is no such account.");

    /// <summary>Refuses a change that would leave no enabled administrator.</summary>
    private void KeepOneAdmin(Account account, bool stillAdmin)
    {
        var isAdminNow = account.Enabled && account.Role == Roles.Admin;
        if (isAdminNow && !stillAdmin && _accounts.CountEnabledAdmins() <= 1)
        {
            throw new AccountRejectedException(AccountProblem.Conflict, "The last enabled administrator cannot be removed, disabled or demoted.");
        }
    }

    private static void CheckRole(string role, AccountKind kind)
    {
        var known = Roles.Find(role) ?? throw Invalid($"'{role}' is not a role.");
        if (kind == AccountKind.User ? !known.ForUsers : !known.ForServices)
        {
            throw Invalid($"The role {role} is not for a {(kind == AccountKind.User ? "user" : "service account")}.");
        }
    }

    private static string CheckedPassword(string? password, string name)
    {
        if (password is null)
        {
            throw Invalid("A user needs a password.");
        }

        return PasswordPolicy.Problem(password, name) is { } problem ? throw Invalid(problem) : password;
    }

    private static Account Placeholder(string name) => new(Guid.Empty, name, AccountKind.User, Roles.Reader, false, DateTimeOffset.UnixEpoch);

    private static AccountRejectedException Invalid(string message) => new(AccountProblem.Invalid, message);
}
