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
    public void should_let_only_its_owner_read_the_database_and_its_log_files_from_the_first_start()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Needs a Unix permission model.");
        var folder = Path.Combine(_workspace.Root, "fresh");
        Directory.CreateDirectory(folder);
        var database = new Database(Options.Create(new MnemarrOptions { DataFolder = folder }));

        using var connection = database.Open();

        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.GetUnixFileMode(Path.Combine(folder, "mnemarr.db")).Should().Be(owner);
        // The connection is still open: the log files exist, and were made with the mode of the database.
        foreach (var log in new[] { "mnemarr.db-wal", "mnemarr.db-shm" }.Select(name => Path.Combine(folder, name)).Where(File.Exists))
        {
            File.GetUnixFileMode(log).Should().Be(owner);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_restrict_a_database_that_was_restored_with_another_mode()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Needs a Unix permission model.");
        var folder = Path.Combine(_workspace.Root, "restored");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "mnemarr.db");
        File.WriteAllBytes(file, []);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using var connection = new Database(Options.Create(new MnemarrOptions { DataFolder = folder })).Open();

        File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void should_refuse_a_database_written_by_a_newer_version_rather_than_read_a_schema_it_does_not_know()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var newer = connection.CreateCommand();
            newer.CommandText = $"PRAGMA user_version = {Schema.Version + 1}";
            newer.ExecuteNonQuery();
        }

        var open = () => new Database(_workspace.Options).Open();

        open.Should().Throw<UnsupportedSchemaException>().Which.Message.Should().Contain($"{Schema.Version + 1}");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(UnsupportedSchemaException))]
    public void should_call_a_missing_unreadable_or_too_new_database_unavailable(Type exception)
    {
        var instance = exception == typeof(UnsupportedSchemaException) ? new UnsupportedSchemaException(2, 1) : (Exception)Activator.CreateInstance(exception)!;

        StorageErrors.IsUnavailable(instance).Should().BeTrue();
    }

    [Fact]
    public void should_not_call_a_bug_unavailable()
    {
        StorageErrors.IsUnavailable(new InvalidOperationException()).Should().BeFalse();
    }
}
