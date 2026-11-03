using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Test.Accounts;

/// <summary>The sign-in the tests read most easily, through the very path that ships: the places to hash, the session and its check.</summary>
internal static class SignInServiceExtensions
{
    public static Account? SignIn(this SignInService service, string name, string password) =>
        service.SignInAsync(name, password, CancellationToken.None).GetAwaiter().GetResult()?.Account;

    public static bool VerifyPassword(this SignInService service, Guid id, string password) =>
        service.VerifyPasswordAsync(id, password, CancellationToken.None).GetAwaiter().GetResult();
}
