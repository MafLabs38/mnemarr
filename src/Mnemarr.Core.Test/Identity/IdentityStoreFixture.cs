using Microsoft.Data.Sqlite;
using Mnemarr.Core.Identity;

namespace Mnemarr.Core.Test.Identity;

/// <summary>The identity row (schema step 24): one row, each write touches its own columns, the content of the logo is read apart.</summary>
public sealed class IdentityStoreFixture : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteIdentityStore _store;

    public IdentityStoreFixture()
    {
        _store = new SqliteIdentityStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void should_say_nothing_is_set_before_anything_is_written()
    {
        _store.Get().Should().Be(new StoredIdentity(null, null, null, null, null, null));
        _store.GetLogo().Should().BeNull();
    }

    [Fact]
    public void should_keep_the_texts_and_the_logo_apart_so_that_a_write_of_one_leaves_the_other()
    {
        _store.SaveTexts("Lab", "#fff", "ink");
        _store.SaveLogo([1, 2, 3], "image/png", "hash", At);
        _store.SaveTexts("Lab 2", null, null);

        _store.Get().Should().Be(new StoredIdentity("Lab 2", null, null, "image/png", "hash", At));
        _store.GetLogo().Should().Equal(1, 2, 3);

        _store.ClearLogo();

        _store.Get().Should().Be(new StoredIdentity("Lab 2", null, null, null, null, null));
        _store.GetLogo().Should().BeNull();
    }

    [Fact]
    public void should_hold_one_row_and_refuse_another()
    {
        _store.SaveTexts("Lab", null, null);
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO identity (id, name) VALUES (2, 'Another')";

        var act = () => command.ExecuteNonQuery();

        act.Should().Throw<SqliteException>().Which.Message.Should().Contain("CHECK");
    }
}
