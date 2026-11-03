using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

/// <summary>The two halves of <see cref="AccountService.Update"/>, for the tests that change only one.</summary>
internal static class AccountServiceExtensions
{
    public static Account ChangeRole(this AccountService service, Guid id, string role, string actor) =>
        service.Update(id, role, service.Existing(id).Enabled, actor);

    public static Account SetEnabled(this AccountService service, Guid id, bool enabled, string actor) =>
        service.Update(id, service.Existing(id).Role, enabled, actor);
}
