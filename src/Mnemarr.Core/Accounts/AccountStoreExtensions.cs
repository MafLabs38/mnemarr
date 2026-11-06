namespace Mnemarr.Core.Accounts;

public static class AccountStoreExtensions
{
    /// <summary>The account, or the refusal that says there is none: one wording for every caller.</summary>
    public static Account Existing(this IAccountStore store, Guid id) =>
        store.Get(id) ?? throw new RejectedException(Problem.NotFound, "There is no such account.");
}
