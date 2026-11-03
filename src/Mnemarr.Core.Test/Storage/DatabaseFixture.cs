using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Storage;

public sealed class DatabaseFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private object? Scalar(string sql)
    {
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void should_create_the_schema_in_the_data_folder_with_its_version()
    {
        Scalar("PRAGMA user_version").Should().Be(1L);
        File.Exists(Path.Combine(_workspace.DataFolder, "mnemarr.db")).Should().BeTrue();
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('accounts', 'api_keys', 'audit_events')").Should().Be(3L);
    }

    [Fact]
    public void should_use_a_write_ahead_log_and_check_foreign_keys_on_every_connection()
    {
        Scalar("PRAGMA journal_mode").Should().Be("wal");
        Scalar("PRAGMA foreign_keys").Should().Be(1L);
    }

    [Fact]
    public void should_not_apply_the_schema_again_when_the_database_is_opened_again()
    {
        Scalar("PRAGMA user_version").Should().Be(1L);

        using var connection = new Database(_workspace.Options).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        command.ExecuteScalar().Should().Be(1L);
    }

    [Fact]
    public void should_refuse_a_key_of_an_account_that_does_not_exist()
    {
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO api_keys (id, account_id, name, prefix, secret_hash, created_at) VALUES ('k', 'nope', 'n', 'p', 'h', 'now')";

        var insert = () => command.ExecuteNonQuery();

        insert.Should().Throw<SqliteException>();
    }

    [Fact]
    public void should_not_create_a_data_folder_that_is_missing()
    {
        var folder = Path.Combine(_workspace.Root, "never", "mounted");
        var subject = new Database(Options.Create(new MnemarrOptions { DataFolder = folder }));

        var open = () => subject.Open();

        open.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(folder).Should().BeFalse();
        Directory.Exists(Path.Combine(_workspace.Root, "never")).Should().BeFalse();
    }
}
