using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Test.Accounts;

/// <summary>The account code over a real SQLite database and a clock the test moves. Hashing is cheap here: its cost is tested once, apart.</summary>
internal sealed class AccountsHarness : IDisposable
{
    public const string Password = "correct horse battery";

    public AccountsHarness(AuthOptions? options = null, IPasswordHasher<Account>? hasher = null, TimeSpan? hashWait = null)
    {
        Options = Microsoft.Extensions.Options.Options.Create(options ?? new AuthOptions());
        Accounts = new SqliteAccountStore(Workspace.Database);
        Keys = new SqliteApiKeyStore(Workspace.Database);
        Audit = new SqliteAuditLog(Workspace.Database, Clock);
        SessionStore = new SqliteSessionStore(Workspace.Database);
        Throttle = new LoginThrottle(Clock);
        Sampler = new AuditSampler(Clock);
        Sessions = new SessionService(SessionStore, Clock, Options);
        var passwordHasher = hasher ?? CheapHasher();
        Service = new AccountService(Accounts, Audit, Throttle, Sessions, passwordHasher, Clock);
        SignInService = new SignInService(Accounts, Audit, Throttle, Sessions, Sampler, passwordHasher) { HashWait = hashWait ?? TimeSpan.FromSeconds(2) };
        ApiKeys = new ApiKeyService(Accounts, Keys, Audit, Sampler, Clock);
    }

    public TestWorkspace Workspace { get; } = new();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    public IOptions<AuthOptions> Options { get; }

    public SqliteAccountStore Accounts { get; }

    public SqliteApiKeyStore Keys { get; }

    public SqliteAuditLog Audit { get; }

    public SqliteSessionStore SessionStore { get; }

    public LoginThrottle Throttle { get; }

    public AuditSampler Sampler { get; }

    public SessionService Sessions { get; }

    public AccountService Service { get; }

    public SignInService SignInService { get; }

    public ApiKeyService ApiKeys { get; }

    public static IPasswordHasher<Account> CheapHasher(int iterations = 1) =>
        new PasswordHasher<Account>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = iterations }));

    public Account Admin(string name = "root") => Service.Create(name, AccountKind.User, Roles.Admin, Password, "test");

    public Account Reader(string name = "alice") => Service.Create(name, AccountKind.User, Roles.Reader, Password, "test");

    public Account Robot(string name = "agent", string role = Roles.Assistant) => Service.Create(name, AccountKind.Service, role, null, "test");

    public void Dispose() => Workspace.Dispose();
}
