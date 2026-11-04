using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// The accounts: creating them, changing a role, a password or the state of an account. It keeps the rules of ADR 0016: a role is one of
/// the built-in ones and fits the kind of account, a password is long enough, the last enabled administrator stays, a new password ends
/// the other sessions, and what changes is audited. Signing in is <see cref="SignInService"/>.
/// </summary>
public sealed class AccountService
{
    private readonly IAccountStore _accounts;
    private readonly IAuditLog _audit;
    private readonly LoginThrottle _throttle;
    private readonly SessionService _sessions;
    private readonly IPasswordHasher<Account> _hasher;
    private readonly TimeProvider _clock;
    private readonly Lock _administrators = new();

    public AccountService(
        IAccountStore accounts, IAuditLog audit, LoginThrottle throttle, SessionService sessions, IPasswordHasher<Account> hasher, TimeProvider clock)
    {
        _accounts = accounts;
        _audit = audit;
        _throttle = throttle;
        _sessions = sessions;
        _hasher = hasher;
        _clock = clock;
    }

    public Account Create(string name, AccountKind kind, string role, string? password, string actor)
    {
        name = name.Trim();
        if (!AccountNames.IsValid(name))
        {
            throw Invalid($"A name is 2 to {AccountNames.MaxLength} letters, digits, dots, dashes or underscores, and starts with a letter or a digit.");
        }

        CheckRole(role, kind);
        string? hash = null;
        if (kind == AccountKind.User)
        {
            hash = _hasher.HashPassword(Account.Placeholder(name), CheckedPassword(password, name));
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

    /// <summary>Changes the role and the state of an account in one step: both are checked first, and nothing is written when one is refused.</summary>
    public Account Update(Guid id, string role, bool enabled, string actor)
    {
        lock (_administrators)
        {
            var account = Existing(id);
            CheckRole(role, account.Kind);
            KeepOneAdmin(account, stillAdmin: enabled && role == Roles.Admin);
            if (!_accounts.Update(id, role, enabled))
            {
                throw NoSuchAccount();
            }

            if (role != account.Role)
            {
                _audit.Append(actor, "account.role-changed", account.Name, $"{account.Role} to {role}");
            }

            if (enabled != account.Enabled)
            {
                if (!enabled)
                {
                    // A disabled account is the answer to a stolen cookie: enabling it again must not bring the cookie back.
                    _sessions.EndAll(id);
                }

                _audit.Append(actor, enabled ? "account.enabled" : "account.disabled", account.Name);
            }

            return account with { Role = role, Enabled = enabled };
        }
    }

    /// <summary>Sets a password and ends the sessions of the account, except the one given (the person who changes their own keeps theirs).</summary>
    public void SetPassword(Guid id, string password, string actor, string? keepSession = null)
    {
        var account = Existing(id);
        if (account.Kind != AccountKind.User)
        {
            throw Invalid("A service account has no password: it has keys.");
        }

        var hash = _hasher.HashPassword(account, CheckedPassword(password, account.Name));
        // The sessions end before the password changes (if the process stopped between the two, nobody keeps a session) and after (a sign-in
        // that checked the old password and started its session in between does not keep it).
        _sessions.EndAll(id, keepSession);
        if (!_accounts.SetPasswordHash(id, hash))
        {
            throw NoSuchAccount();
        }

        _sessions.EndAll(id, keepSession);
        _throttle.Succeeded(account.Name);
        _throttle.Succeeded(SignInService.PasswordCheckKey(account.Name));
        _audit.Append(actor, "account.password-changed", account.Name);
    }

    /// <summary>
    /// Break-glass, for the day nobody can sign in: sets the password of a user, enables the account, lifts its lockout and ends its sessions.
    /// </summary>
    public void Reset(string name, string password)
    {
        var credentials = _accounts.GetCredentials(name.Trim()) ?? throw new RejectedException(Problem.NotFound, $"There is no account named '{name}'.");
        var account = credentials.Account;
        if (account.Kind != AccountKind.User)
        {
            throw Invalid("Only a user has a password to reset.");
        }

        var hash = _hasher.HashPassword(account, CheckedPassword(password, account.Name));
        lock (_administrators)
        {
            // The sessions go first: a reset is for the day a cookie may be stolen, and it must not leave one if it stops half way.
            var current = _accounts.Existing(account.Id);
            _sessions.EndAll(account.Id);
            _accounts.SetPasswordHash(account.Id, hash);
            _accounts.Update(account.Id, current.Role, enabled: true);
            _sessions.EndAll(account.Id);
        }

        _throttle.Succeeded(account.Name);
        _throttle.Succeeded(SignInService.PasswordCheckKey(account.Name));
        _audit.Append(AuditActors.System, "account.reset", account.Name);
    }

    public void Delete(Guid id, string actor)
    {
        lock (_administrators)
        {
            var account = Existing(id);
            KeepOneAdmin(account, stillAdmin: false);
            _accounts.Remove(id);
            _audit.Append(actor, "account.deleted", account.Name);
        }
    }

    internal Account Existing(Guid id) => _accounts.Existing(id);

    /// <summary>Refuses a change that would leave no enabled administrator. Called with the administrators lock held.</summary>
    private void KeepOneAdmin(Account account, bool stillAdmin)
    {
        var isAdminNow = account.Enabled && account.Role == Roles.Admin;
        if (isAdminNow && !stillAdmin && _accounts.CountEnabledAdmins() <= 1)
        {
            throw new RejectedException(Problem.Conflict, "The last enabled administrator cannot be removed, disabled or demoted.");
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

    private static RejectedException NoSuchAccount() => new(Problem.NotFound, "There is no such account.");

    private static RejectedException Invalid(string message) => new(Problem.Invalid, message);
}
