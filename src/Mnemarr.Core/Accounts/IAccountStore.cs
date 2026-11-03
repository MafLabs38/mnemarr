namespace Mnemarr.Core.Accounts;

public interface IAccountStore
{
    /// <summary>Adds an account. Throws <see cref="AccountRejectedException"/> (conflict) when the name is taken, whatever its case.</summary>
    void Add(Account account, string? passwordHash);

    Account? Get(Guid id);

    AccountCredentials? GetCredentials(string name);

    IReadOnlyList<Account> List();

    bool Update(Guid id, string role, bool enabled);

    bool SetPasswordHash(Guid id, string passwordHash);

    /// <summary>Removes an account with its keys. False when there was none.</summary>
    bool Remove(Guid id);

    int Count();

    /// <summary>How many enabled accounts hold the Admin role: the last one must stay.</summary>
    int CountEnabledAdmins();
}
