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
    private readonly IMachineStore _machines;

    public MachineAccounts(IAccountStore accounts, AccountService accountService, ApiKeyService keys, IMachineStore machines)
    {
        _machines = machines;
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

    /// <summary>The account of a machine that is taken out of the list goes with it, so that the name is free again.</summary>
    public void Delete(Guid accountId, string actor)
    {
        if (_accounts.Get(accountId) is not null)
        {
            _accountService.Delete(accountId, actor);
        }
    }

    private Account New(string machine)
    {
        // The account of a machine keeps the name it had when it was made, so a name that a machine had before it was renamed may be the name of its account:
        // that name is free for a new machine, whose account then gets a number. An account of that name that belongs to no machine is somebody else's.
        var all = _accounts.List();
        var name = AccountPrefix + machine;
        var number = 2;
        while (all.FirstOrDefault(account => string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            if (_machines.GetByAccount(taken.Id) is null)
            {
                throw new RejectedException(Problem.Conflict, $"An account named '{name}' exists already and belongs to no machine.");
            }

            name = $"{AccountPrefix}{machine}-{number}";
            number++;
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
