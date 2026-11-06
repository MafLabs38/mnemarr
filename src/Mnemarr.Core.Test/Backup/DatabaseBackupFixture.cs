using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Backup;

namespace Mnemarr.Core.Test.Backup;

public sealed class DatabaseBackupFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly BackupOptions _options = new();
    private readonly DatabaseBackup _backup;

    public DatabaseBackupFixture()
    {
        _backup = new DatabaseBackup(_workspace.Database, _workspace.Options, Options.Create(_options), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private void Put(string value)
    {
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS probe (value TEXT); INSERT INTO probe VALUES ($value)";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static List<string> ValuesIn(string snapshot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM probe ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    [Fact]
    public void should_write_a_whole_copy_of_the_database_that_has_what_was_in_it_and_is_named_after_the_moment()
    {
        Put("one");
        Put("two");

        var snapshot = _backup.Snapshot();

        Path.GetFileName(snapshot).Should().Be("mnemarr-20261106-100000.db");
        Path.GetDirectoryName(snapshot).Should().Be(Path.Combine(_workspace.DataFolder, "backup"));
        ValuesIn(snapshot).Should().Equal("one", "two");
    }

    [Fact]
    public void should_leave_nothing_half_written_where_a_backup_tool_would_copy_it_and_be_readable_by_its_owner_only()
    {
        Put("one");

        var snapshot = _backup.Snapshot();

        Directory.GetFiles(_backup.Folder).Should().Equal(snapshot);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(snapshot).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(_backup.Folder).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void should_never_create_the_data_folder_that_is_missing_because_that_would_hide_a_volume_that_was_not_mounted()
    {
        var missing = Path.Combine(_workspace.Root, "not-mounted");
        var options = Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = missing });
        var backup = new DatabaseBackup(new Mnemarr.Core.Storage.Database(options), options, Options.Create(_options), _clock);

        backup.IsDue().Should().BeFalse();
        Assert.Throws<DirectoryNotFoundException>(backup.Snapshot);
        Directory.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public void should_keep_a_copy_as_it_was_when_the_database_changes_afterwards()
    {
        Put("before");
        var first = _backup.Snapshot();
        _clock.Advance(TimeSpan.FromHours(25));
        Put("after");

        var second = _backup.Snapshot();

        ValuesIn(first).Should().Equal("before");
        ValuesIn(second).Should().Equal("before", "after");
    }

    [Fact]
    public void should_be_due_when_there_is_none_or_the_last_is_old_and_never_when_it_is_turned_off()
    {
        Put("one");
        _backup.IsDue().Should().BeTrue("there is none");

        _backup.Snapshot();
        _backup.Latest().Should().Be(_clock.GetUtcNow());
        _backup.IsDue().Should().BeFalse();
        _clock.Advance(TimeSpan.FromHours(23));
        _backup.IsDue().Should().BeFalse();
        _clock.Advance(TimeSpan.FromHours(2));
        _backup.IsDue().Should().BeTrue("the last is 25 hours old");

        _options.Enabled = false;
        _backup.IsDue().Should().BeFalse();
    }

    [Fact]
    public void should_keep_the_newest_copies_and_forget_the_rest_and_the_copies_that_were_never_finished()
    {
        Put("one");
        _options.Keep = 3;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_backup.Folder).FullName, "mnemarr-20260101-000000.db.partial"), "half");
        var kept = new List<string>();
        for (var day = 0; day < 5; day++)
        {
            kept.Add(_backup.Snapshot());
            _clock.Advance(TimeSpan.FromDays(1));
        }

        Directory.GetFiles(_backup.Folder).OrderBy(file => file, StringComparer.Ordinal).Should().Equal(kept.Skip(2));
    }

    [Fact]
    public void should_not_take_two_copies_in_the_same_second()
    {
        Put("one");

        var first = _backup.Snapshot();
        Put("two");
        var second = _backup.Snapshot();

        second.Should().Be(first);
        ValuesIn(first).Should().Equal("one");
    }

    [Fact]
    public void should_be_taken_by_the_service_when_due_and_say_nothing_when_it_cannot_be()
    {
        Put("one");
        var service = new BackupService(_backup, _clock, NullLogger<BackupService>.Instance);

        service.TakeIfDue();
        _backup.Latest().Should().Be(_clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromHours(30));
        _options.Folder = Path.Combine(_workspace.Root, "no", "such", "root", new string('x', 10));
        File.WriteAllText(Path.Combine(_workspace.Root, "no"), "a file where a folder should be");
        var failing = new BackupService(_backup, _clock, NullLogger<BackupService>.Instance);

        Record.Exception(failing.TakeIfDue).Should().BeNull("a snapshot that fails is logged and tried again later; it stops nothing");
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(168, 60, true)]
    [InlineData(0, 7, false)]
    [InlineData(169, 7, false)]
    [InlineData(24, 0, false)]
    [InlineData(24, 61, false)]
    public void should_keep_the_settings_within_their_bounds(int everyHours, int keep, bool valid) =>
        new BackupOptionsValidator().Validate(null, new BackupOptions { EveryHours = everyHours, Keep = keep }).Succeeded.Should().Be(valid);
}
