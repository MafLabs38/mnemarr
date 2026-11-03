using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>
/// One client per kind of caller, built once for a test class: signing in costs a password hash, and the matrix has dozens of rows.
/// People are signed in with a cookie, services come with a key.
/// </summary>
public sealed class Callers : IAsyncDisposable
{
    public const string Password = "a long enough password";
    private readonly MnemarrFactory _factory;
    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly Dictionary<string, Guid> _ids = [];

    public Callers(MnemarrFactory factory)
    {
        _factory = factory;
    }

    public HttpClient Admin { get; private set; } = null!;

    public HttpClient Anonymous => _factory.CreateBrowserClient();

    public HttpClient this[string role] => _clients[role];

    public Guid IdOf(string role) => _ids[role];

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    public async Task StartAsync()
    {
        Admin = await _factory.SignedInClientAsync();
        foreach (var role in new[] { "Operator", "Reader" })
        {
            var name = Unique(role.ToLowerInvariant());
            _ids[role] = await CreateAsync(Admin, new { name, kind = "User", role, password = Password });
            _clients[role] = await _factory.SignedInClientAsync(name, Password);
        }

        foreach (var role in new[] { "Assistant", "Collector" })
        {
            var id = await CreateAsync(Admin, new { name = Unique(role.ToLowerInvariant()), kind = "Service", role });
            _ids[role] = id;
            _clients[role] = _factory.CreateKeyClient(await CreateKeyAsync(Admin, id));
        }

        _clients["Admin"] = Admin;
    }

    public static async Task<Guid> CreateAsync(HttpClient admin, object account)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/accounts", account, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    public static async Task<string> CreateKeyAsync(HttpClient admin, Guid accountId)
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/accounts/{accountId}/keys", new { name = "test" }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("secret").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        await Task.CompletedTask;
    }
}
