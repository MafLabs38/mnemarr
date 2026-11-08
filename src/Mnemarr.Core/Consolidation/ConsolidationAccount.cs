using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Consolidation;

/// <summary>
/// The account the consolidation acts as (ADR 0036): a service account of its own, with the role that reads notes and conversations and writes notes, no key and no password. The model
/// reaches Mnemarr with a token of this account that lasts for the run, and the log says <c>consolidation (via job:…)</c>.
/// </summary>
public sealed class ConsolidationAccount
{
    public const string Name = "consolidation";

    private readonly IAccountStore _accounts;
    private readonly AccountService _service;
    private readonly object _lock = new();

    public ConsolidationAccount(IAccountStore accounts, AccountService service)
    {
        _accounts = accounts;
        _service = service;
    }

    /// <summary>The account, made when there is none. One that was disabled or given another role by hand stays so: the consolidation then finds itself refused, and says so.</summary>
    public Account Ensure()
    {
        lock (_lock)
        {
            return _accounts.List().FirstOrDefault(account => string.Equals(account.Name, Name, StringComparison.OrdinalIgnoreCase))
                ?? _service.Create(Name, AccountKind.Service, Roles.Consolidator, null, "mnemarr");
        }
    }
}
