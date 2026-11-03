using Microsoft.AspNetCore.Identity;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Signing in and checking a person's current password (ADR 0016): the lockout, the decoy that makes a wrong name and a wrong password
/// take the same time, the places to hash a password, and the session that starts once the password is right. What happens to an account
/// (its role, its password, its state) is <see cref="AccountService"/>.
/// </summary>
public sealed class SignInService
{
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
    private readonly string _decoyHash;

    public SignInService(IAccountStore accounts, IAuditLog audit, LoginThrottle throttle, SessionService sessions, AuditSampler sampler, IPasswordHasher<Account> hasher)
    {
        _accounts = accounts;
        _audit = audit;
        _throttle = throttle;
        _sessions = sessions;
        _sampler = sampler;
        _hasher = hasher;

        // Checked when the name is unknown, so that a wrong name and a wrong password take the same time.
        _decoyHash = hasher.HashPassword(Account.Placeholder("decoy"), Guid.NewGuid().ToString("N"));
    }

    /// <summary>How long a sign-in waits for a place to hash its password before it is told to come back later.</summary>
    public TimeSpan HashWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The counter of the wrong current passwords of a person is their own: whoever guesses their name at the sign-in cannot stop them
    /// changing it. A name cannot hold the colon, and the sign-in refuses what a name cannot be, so the two kinds of key never meet.
    /// </summary>
    public static string PasswordCheckKey(string name) => $"password-check:{name}";

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
    public async Task<bool> VerifyPasswordAsync(Guid id, string password, CancellationToken cancellationToken)
    {
        using var slot = await TakeHashSlotAsync(cancellationToken);
        return VerifyPassword(id, password);
    }

    /// <summary>Ends a session: the person signed out.</summary>
    public void EndSession(string sessionId) => _sessions.End(sessionId);

    private bool VerifyPassword(Guid id, string password)
    {
        var account = _accounts.Existing(id);
        var budget = PasswordCheckKey(account.Name);
        Reserve(budget, isAccount: true, actor: account.Name, action: "password.locked");
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

    /// <summary>The account that signed in and the password hash it was checked against, which is the one the sessions are bound to.</summary>
    private (Account Account, string Hash)? Check(string name, string password)
    {
        name = name.Trim();
        // Only what a name can be is counted: a counter is keyed by the name, and the other budgets cannot be reached with a forged one.
        if (!AccountNames.IsValid(name) || password.Length > PasswordPolicy.MaxLength)
        {
            return null;
        }

        var credentials = _accounts.GetCredentials(name);
        Reserve(name, isAccount: credentials is not null, actor: name, action: "login.locked");

        // An unknown name, a service account and an account without a password are checked against the decoy: the answer takes the same time.
        var isDecoy = credentials is not { Account.Kind: AccountKind.User, PasswordHash: not null };
        var (account, hash) = isDecoy ? (Account.Placeholder(name), _decoyHash) : (credentials!.Account, credentials.PasswordHash!);
        var result = _hasher.VerifyHashedPassword(account, hash, password);
        if (isDecoy || !credentials!.Account.Enabled || result == PasswordVerificationResult.Failed)
        {
            // Only a name that exists is audited: an unknown name is anybody's guess, and anybody can send a million.
            if (credentials is not null)
            {
                _audit.Append(name, "login.failed", name);
            }

            return null;
        }

        return Succeeded(account, hash, result, password);
    }

    /// <summary>Counts an attempt before its password is checked, or refuses it: a locked name is not checked at all.</summary>
    private void Reserve(string counter, bool isAccount, string actor, string action)
    {
        if (_throttle.TryReserve(counter, isAccount))
        {
            return;
        }

        if (_sampler.Allow(action, counter.ToUpperInvariant()))
        {
            _audit.Append(actor, action, actor);
        }

        throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.", _throttle.LockedFor(counter) ?? LoginThrottle.Window);
    }

    private (Account Account, string Hash) Succeeded(Account account, string hash, PasswordVerificationResult result, string password)
    {
        _throttle.Succeeded(account.Name);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            hash = _hasher.HashPassword(account, password);
            _accounts.SetPasswordHash(account.Id, hash);
        }

        _audit.Append(account.Name, "login.succeeded", account.Name);
        return (account, hash);
    }

    /// <summary>A place to hash a password, or a refusal to come back later: the work waits for a few at a time, and not for long.</summary>
    private async Task<SlotReleaser> TakeHashSlotAsync(CancellationToken cancellationToken)
    {
        if (!await _hashSlots.WaitAsync(HashWait, cancellationToken))
        {
            throw new AccountRejectedException(AccountProblem.Busy, "The server is busy checking passwords. Try again in a moment.", BusyRetryAfter);
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
}
