using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Accounts;

public interface ISessionStore
{
    void Add(string id, Guid accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt);

    /// <summary>The account of a session that exists and has not expired, or null.</summary>
    Guid? AccountOf(string id, DateTimeOffset now);

    void Remove(string id);

    /// <summary>Ends every session of an account, except one (null: none is kept).</summary>
    void RemoveAll(Guid accountId, string? except);

    /// <summary>Forgets the sessions that expired before this moment.</summary>
    void RemoveExpired(DateTimeOffset now);
}

/// <summary>
/// The sessions of the people who signed in (ADR 0016). The cookie carries a session id and the server keeps the session: signing out,
/// changing a password and the end of its absolute lifetime end it for real, and a cookie that was forged or copied from a backup of the
/// key ring has no session behind it.
/// </summary>
public sealed class SessionService
{
    private readonly ISessionStore _store;
    private readonly TimeProvider _clock;
    private readonly IOptions<AuthOptions> _options;

    public SessionService(ISessionStore store, TimeProvider clock, IOptions<AuthOptions> options)
    {
        _store = store;
        _clock = clock;
        _options = options;
    }

    /// <summary>Starts a session for an account and returns its id.</summary>
    public string Start(Guid accountId)
    {
        var now = _clock.GetUtcNow();
        _store.RemoveExpired(now);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _store.Add(id, accountId, now, now.AddDays(_options.Value.SessionMaxDays));
        return id;
    }

    /// <summary>The account a session belongs to, while it is valid.</summary>
    public Guid? AccountOf(string sessionId) => _store.AccountOf(sessionId, _clock.GetUtcNow());

    public void End(string sessionId) => _store.Remove(sessionId);

    public void EndAll(Guid accountId, string? except = null) => _store.RemoveAll(accountId, except);
}
