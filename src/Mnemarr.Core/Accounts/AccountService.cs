using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// The accounts: creating them, signing in, changing a role, a password or the state of an account. It keeps the rules of ADR 0016:
/// a role is one of the built-in ones and fits the kind of account, a password is long enough, repeated failures lock a name, the last
/// enabled administrator stays, a new password ends the other sessions, and what changes is audited.
/// </summary>
public sealed partial class AccountService
{
    public const int MaxNameLength = 64;

    /// <summary>How many passwords are hashed at the same time: the scarce resource of a small host, and what a flood of sign-ins would exhaust.</summary>
    public const int MaxConcurrentHashes = 4;
    private readonly SemaphoreSlim _hashSlots = new(MaxConcurrentHashes, MaxConcurrentHashes);
    private readonly IAccountStore _accounts;
    private readonly IAuditLog _audit;
    private readonly LoginThrottle _throttle;
    private readonly SessionService _sessions;
    private readonly AuditSampler _sampler;
    private readonly IPasswordHasher<Account> _hasher;
    private readonly TimeProvider _clock;
    private readonly Lock _administrators = new();
    private readonly string _decoyHash;

    /// <summary>How long a sign-in waits for a place to hash its password before it is told to come back later.</summary>
    internal TimeSpan HashWait { get; set; } = TimeSpan.FromSeconds(2);

    public AccountService(
        IAccountStore accounts, IAuditLog audit, LoginThrottle throttle, SessionService sessions, AuditSampler sampler, IPasswordHasher<Account> hasher, TimeProvider clock)
    {
        _accounts = accounts;
        _audit = audit;
        _throttle = throttle;
        _sessions = sessions;
        _sampler = sampler;
        _hasher = hasher;
        _clock = clock;

        // Checked when the name is unknown, so that a wrong name and a wrong password take the same time.
        _decoyHash = hasher.HashPassword(Placeholder("decoy"), Guid.NewGuid().ToString("N"));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")]
    private static partial Regex NamePattern();

    public Account Create(string name, AccountKind kind, string role, string? password, string actor)
    {
        name = name.Trim();
        if (!NamePattern().IsMatch(name))
        {
            throw Invalid($"A name is 2 to {MaxNameLength} letters, digits, dots, dashes or underscores, and starts with a letter or a digit.");
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

    /// <summary>
    /// The user who signed in, or null. A name or a password that cannot be one is refused before anything is counted, hashed or audited.
    /// An attempt is counted before its password is checked; a locked name is refused without checking the password.
    /// </summary>
    public Account? SignIn(string name, string password)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength || password.Length > PasswordPolicy.MaxLength)
        {
            return null;
        }

        var credentials = _accounts.GetCredentials(name);
        var known = credentials is { Account.Kind: AccountKind.User, PasswordHash: not null };
        using var slot = TakeHashSlot();
        if (!_throttle.TryReserve(name, isAccount: credentials is not null))
        {
            if (_sampler.Allow("login.locked", name.ToUpperInvariant()))
            {
                _audit.Append(name, "login.locked", name);
            }

            throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.");
        }

        var result = _hasher.VerifyHashedPassword(credentials?.Account ?? Placeholder(name), known ? credentials!.PasswordHash! : _decoyHash, password);
        if (!known || result == PasswordVerificationResult.Failed || !credentials!.Account.Enabled)
        {
            // Only a name that exists is audited: an unknown name is anybody's guess, and anybody can send a million.
            if (credentials is not null)
            {
                _audit.Append(name, "login.failed", name);
            }

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

    /// <summary>
    /// Whether a person's password is the one they give, for a change of their own password. It does not sign in, but it counts like a
    /// sign-in: whoever holds a stolen session must not be able to guess the password at the speed of the hash.
    /// </summary>
    public bool VerifyPassword(Guid id, string password)
    {
        var account = Existing(id);
        using var slot = TakeHashSlot();
        var budget = PasswordCheckKey(account.Name);
        if (!_throttle.TryReserve(budget, isAccount: true))
        {
            throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.");
        }

        var credentials = _accounts.GetCredentials(account.Name);
        if (credentials?.PasswordHash is null || password.Length > PasswordPolicy.MaxLength
            || _hasher.VerifyHashedPassword(account, credentials.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            _audit.Append(account.Name, "password.verify-failed", account.Name);
            return false;
        }

        _throttle.Succeeded(budget);
        return true;
    }

    /// <summary>The counter of the wrong current passwords of a person is their own: whoever guesses their name at the sign-in cannot stop them changing it.</summary>
    internal static string PasswordCheckKey(string name) => $"password-check:{name}";

    /// <summary>A place to hash a password, or a refusal to come back later: the work waits for a few at a time, and not for long.</summary>
    private SlotReleaser TakeHashSlot()
    {
        if (!_hashSlots.Wait(HashWait))
        {
            throw new AccountRejectedException(AccountProblem.Locked, "The server is busy checking passwords. Try again in a moment.");
        }

        return new SlotReleaser(_hashSlots);
    }

    private readonly struct SlotReleaser : IDisposable
    {
        private readonly SemaphoreSlim _slots;

        public SlotReleaser(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose() => _slots.Release();
    }

    /// <summary>Ends a session: the person signed out.</summary>
    public void EndSession(string sessionId) => _sessions.End(sessionId);

    /// <summary>
    /// Starts the session of a user who has just signed in, and returns its id; null when the account was disabled in the meantime. The
    /// check comes after the start, so that a disabling that ended the sessions a moment earlier cannot leave this one behind.
    /// </summary>
    public string? StartSession(Guid accountId)
    {
        var session = _sessions.Start(accountId);
        if (_accounts.Get(accountId) is { Enabled: true })
        {
            return session;
        }

        _sessions.End(session);
        return null;
    }

    /// <summary>Changes the role and the state of an account in one step: both are checked first, and nothing is written when one is refused.</summary>
    public Account Update(Guid id, string role, bool enabled, string actor)
    {
        lock (_administrators)
        {
            var account = Existing(id);
            CheckRole(role, account.Kind);
            KeepOneAdmin(account, stillAdmin: enabled && role == Roles.Admin);
            _accounts.Update(id, role, enabled);
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

        _accounts.SetPasswordHash(id, _hasher.HashPassword(account, CheckedPassword(password, account.Name)));
        _sessions.EndAll(id, keepSession);
        _throttle.Succeeded(account.Name);
        _throttle.Succeeded(PasswordCheckKey(account.Name));
        _audit.Append(actor, "account.password-changed", account.Name);
    }

    /// <summary>
    /// Break-glass, for the day nobody can sign in: sets the password of a user, enables the account, lifts its lockout and ends its sessions.
    /// </summary>
    public void Reset(string name, string password)
    {
        var credentials = _accounts.GetCredentials(name.Trim()) ?? throw new AccountRejectedException(AccountProblem.NotFound, $"There is no account named '{name}'.");
        var account = credentials.Account;
        if (account.Kind != AccountKind.User)
        {
            throw Invalid("Only a user has a password to reset.");
        }

        lock (_administrators)
        {
            _accounts.SetPasswordHash(account.Id, _hasher.HashPassword(account, CheckedPassword(password, account.Name)));
            _accounts.Update(account.Id, account.Role, enabled: true);
        }

        _sessions.EndAll(account.Id);
        _throttle.Succeeded(account.Name);
        _throttle.Succeeded(PasswordCheckKey(account.Name));
        _audit.Append("system", "account.reset", account.Name);
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

    internal Account Existing(Guid id) =>
        _accounts.Get(id) ?? throw new AccountRejectedException(AccountProblem.NotFound, "There is no such account.");

    /// <summary>Refuses a change that would leave no enabled administrator. Called with the administrators lock held.</summary>
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
