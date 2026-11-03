using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// The accounts: creating them, signing in, changing a role, a password or the state of an account. It keeps the rules of ADR 0016:
/// a role is one of the built-in ones and fits the kind of account, a password is long enough, repeated failures lock a name, the last
/// enabled administrator stays, a new password ends the other sessions, and what changes is audited.
/// </summary>
public sealed partial class AccountService
{
    public const int MaxNameLength = 64;

    /// <summary>How many passwords are hashed at the same time: one per core, two to eight. The scarce resource of the host, and what a flood of sign-ins would exhaust.</summary>
    public static readonly int MaxConcurrentHashes = Math.Clamp(Environment.ProcessorCount, 2, 8);
    private static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(5);
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
    internal Account? SignIn(string name, string password) => Check(name, password)?.Account;

    /// <summary>The account that signed in and the password hash it was checked against, which is the one the sessions are bound to.</summary>
    private (Account Account, string Hash)? Check(string name, string password)
    {
        name = name.Trim();
        // Only what a name can be is counted: a counter is keyed by the name, and the other budgets cannot be reached with a forged one.
        if (!NamePattern().IsMatch(name) || password.Length > PasswordPolicy.MaxLength)
        {
            return null;
        }

        var credentials = _accounts.GetCredentials(name);
        if (!_throttle.TryReserve(name, isAccount: credentials is not null))
        {
            if (_sampler.Allow("login.locked", name.ToUpperInvariant()))
            {
                _audit.Append(name, "login.locked", name);
            }

            throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.", LoginThrottle.Window);
        }

        // An unknown name, a service account and an account without a password are checked against the decoy: the answer takes the same time.
        var (account, hash) = credentials is { Account.Kind: AccountKind.User, PasswordHash: { } stored } ? (credentials.Account, stored) : (Placeholder(name), _decoyHash);
        var result = _hasher.VerifyHashedPassword(account, hash, password);
        if (credentials is not { Account: { Enabled: true, Kind: AccountKind.User } } || hash == _decoyHash || result == PasswordVerificationResult.Failed)
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
            hash = _hasher.HashPassword(account, password);
            _accounts.SetPasswordHash(account.Id, hash);
        }

        _audit.Append(account.Name, "login.succeeded", account.Name);
        return (account, hash);
    }

    /// <summary>
    /// Signs a user in, once a place to hash the password is free (waiting for one holds no thread, and not for long), and starts their
    /// session. The session is checked after it is started: an account disabled, or a password changed, while the password was being
    /// hashed ends it again, so that a new password or a disabling that ended the sessions a moment earlier cannot leave this one behind.
    /// </summary>
    public async Task<SignedIn?> SignInAsync(string name, string password, CancellationToken cancellationToken)
    {
        using var slot = await TakeHashSlotAsync(cancellationToken);
        if (Check(name, password) is not var (account, hash))
        {
            return null;
        }

        var session = _sessions.Start(account.Id);
        if (_accounts.GetCredentials(account.Name) is { Account.Enabled: true, PasswordHash: { } current } && current == hash)
        {
            return new SignedIn(account, session);
        }

        _sessions.End(session);
        return null;
    }

    /// <summary>
    /// Whether a person's password is the one they give, for a change of their own password. It does not sign in, but it counts like a
    /// sign-in: whoever holds a stolen session must not be able to guess the password at the speed of the hash.
    /// </summary>
    internal bool VerifyPassword(Guid id, string password)
    {
        var account = Existing(id);
        var budget = PasswordCheckKey(account.Name);
        if (!_throttle.TryReserve(budget, isAccount: true))
        {
            throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.", LoginThrottle.Window);
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

    public async Task<bool> VerifyPasswordAsync(Guid id, string password, CancellationToken cancellationToken)
    {
        using var slot = await TakeHashSlotAsync(cancellationToken);
        return VerifyPassword(id, password);
    }

    /// <summary>
    /// The counter of the wrong current passwords of a person is their own: whoever guesses their name at the sign-in cannot stop them
    /// changing it. A name cannot hold the colon, and the sign-in refuses what a name cannot be, so the two kinds of key never meet.
    /// </summary>
    internal static string PasswordCheckKey(string name) => $"password-check:{name}";

    /// <summary>A place to hash a password, or a refusal to come back later: the work waits for a few at a time, and not for long.</summary>
    private async Task<SlotReleaser> TakeHashSlotAsync(CancellationToken cancellationToken)
    {
        if (!await _hashSlots.WaitAsync(HashWait, cancellationToken))
        {
            throw new AccountRejectedException(AccountProblem.Locked, "The server is busy checking passwords. Try again in a moment.", BusyRetryAfter);
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

        var hash = _hasher.HashPassword(account, CheckedPassword(password, account.Name));
        // The sessions end before the password changes (if the process stopped between the two, nobody keeps a session) and after (a sign-in
        // that checked the old password and started its session in between does not keep it).
        _sessions.EndAll(id, keepSession);
        _accounts.SetPasswordHash(id, hash);
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

        var hash = _hasher.HashPassword(account, CheckedPassword(password, account.Name));
        lock (_administrators)
        {
            // The sessions go first: a reset is for the day a cookie may be stolen, and it must not leave one if it stops half way.
            _sessions.EndAll(account.Id);
            _accounts.SetPasswordHash(account.Id, hash);
            _accounts.Update(account.Id, account.Role, enabled: true);
            _sessions.EndAll(account.Id);
        }

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
