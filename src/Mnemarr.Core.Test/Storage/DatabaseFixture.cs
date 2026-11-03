using System.Runtime.Versioning;
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
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('accounts', 'api_keys', 'sessions', 'audit_events')").Should().Be(4L);
    }

    [Fact]
    public void should_use_a_write_ahead_log_and_check_foreign_keys_on_every_connection()
    {
        Scalar("PRAGMA journal_mode").Should().Be("wal");
        Scalar("PRAGMA foreign_keys").Should().Be(1L);
    }

    [Fact]
    public void should_keep_what_it_holds_when_the_database_is_opened_again()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO accounts (id, name, kind, role, enabled, created_at) VALUES ('a', 'alice', 'User', 'Reader', 1, 'now')";
            insert.ExecuteNonQuery();
        }

        using var again = new Database(_workspace.Options).Open();
        using var count = again.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM accounts";

        count.ExecuteScalar().Should().Be(1L);
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

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_let_only_its_owner_read_the_database_because_it_holds_the_hashes_of_passwords_sessions_and_keys()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Needs a Unix permission model.");
        Scalar("PRAGMA user_version");

        File.GetUnixFileMode(Path.Combine(_workspace.DataFolder, "mnemarr.db")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void should_call_a_missing_or_unreadable_database_unavailable_and_nothing_else(Type exception)
    {
        StorageErrors.IsUnavailable((Exception)Activator.CreateInstance(exception)!).Should().BeTrue();
        StorageErrors.IsUnavailable(new InvalidOperationException()).Should().BeFalse();
    }
}
