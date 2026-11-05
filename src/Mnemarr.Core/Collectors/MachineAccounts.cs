using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// The service account behind a machine and its keys: made when the machine first joins, kept when it joins again, disabled when it is revoked.
/// It also owns the one lock that the changes around a machine take, so that a code is not exchanged while the machine is being revoked.
/// </summary>
public sealed class MachineAccounts
{
    private const string AccountPrefix = "collector-";
    private readonly IAccountStore _accounts;
    private readonly AccountService _accountService;
    private readonly ApiKeyService _keys;

    public MachineAccounts(IAccountStore accounts, AccountService accountService, ApiKeyService keys)
    {
        _accounts = accounts;
        _accountService = accountService;
        _keys = keys;
    }

    internal Lock Gate { get; } = new();

    /// <summary>The account for a machine that joins: a new one, or the one it had, with the keys it had revoked and the account enabled again.</summary>
    public Account ForJoining(Machine? existing, string machine, string actor)
    {
        if (existing is null)
        {
            return New(machine);
        }

        var account = _accounts.Existing(existing.AccountId);
        RevokeKeys(account.Id, actor);
        return account.Enabled ? account : _accountService.Update(account.Id, account.Role, enabled: true, actor);
    }

    public string NewKey(Account account, DateTimeOffset now, string actor) =>
        _keys.Create(account.Id, $"enrolled {now:yyyy-MM-dd HH:mm}", sources: null, expiresAt: null, actor).Secret;

    /// <summary>Its keys stop working and its account is disabled.</summary>
    public void Disable(Guid accountId, string actor)
    {
        RevokeKeys(accountId, actor);
        var account = _accounts.Get(accountId);
        if (account is not null)
        {
            _accountService.Update(account.Id, account.Role, enabled: false, actor);
        }
    }

    private Account New(string machine)
    {
        var name = AccountPrefix + machine;
        if (_accounts.List().Any(account => string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            // An account of that name that belongs to no machine is somebody else's: a person decides what to do with it.
            throw new RejectedException(Problem.Conflict, $"An account named '{name}' exists already and belongs to no machine.");
        }

        return _accountService.Create(name, AccountKind.Service, Roles.Collector, password: null, actor: AuditActors.System);
    }

    private void RevokeKeys(Guid accountId, string actor)
    {
        foreach (var key in _keys.List(accountId).Where(key => key.RevokedAt is null))
        {
            _keys.Revoke(accountId, key.Id, actor);
        }
    }
}
