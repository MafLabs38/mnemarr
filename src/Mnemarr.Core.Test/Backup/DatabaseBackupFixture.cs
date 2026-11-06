using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Backup;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Health;
using Mnemarr.Core.Storage;

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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

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

    /// <summary>A snapshot that cannot be written: where the folder should be, there is a file.</summary>
    private void BreakTheFolder() => File.WriteAllText(_backup.Folder, "a file where the folder should be");

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
    public void should_leave_nothing_half_written_where_a_backup_tool_would_copy_it()
    {
        Put("one");

        var snapshot = _backup.Snapshot();

        Directory.GetFiles(_backup.Folder).Should().Equal(snapshot);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void should_make_the_copy_and_its_folder_readable_by_the_owner_only_even_for_a_folder_that_came_back_wider()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        Put("one");
        Directory.CreateDirectory(_backup.Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var snapshot = _backup.Snapshot();

        File.GetUnixFileMode(snapshot).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_backup.Folder).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void should_never_create_the_data_folder_that_is_missing_because_that_would_hide_a_volume_that_was_not_mounted()
    {
        var missing = Path.Combine(_workspace.Root, "not-mounted");
        var options = Options.Create(new MnemarrOptions { DataFolder = missing });
        var backup = new DatabaseBackup(new Database(options), options, Options.Create(_options), _clock);

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
    public void should_ignore_what_is_in_the_folder_and_is_not_a_snapshot_when_it_looks_for_the_newest()
    {
        Put("one");
        _backup.Latest().Should().BeNull("there is no folder yet");
        var snapshot = _backup.Snapshot();
        File.WriteAllText(Path.Combine(_backup.Folder, "mnemarr-foo.db"), "not a moment");
        File.WriteAllText(Path.Combine(_backup.Folder, "notes.txt"), "a stray file");

        _backup.Latest().Should().Be(_clock.GetUtcNow());
        Directory.GetFiles(_backup.Folder, "mnemarr-2*.db").Should().Equal(snapshot);
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
    public void should_never_overwrite_a_whole_copy_taken_in_the_same_second()
    {
        Put("one");

        var first = _backup.Snapshot();
        Put("two");
        var second = _backup.Snapshot();

        second.Should().Be(first);
        ValuesIn(first).Should().Equal("one");
    }

    [Fact]
    public void should_know_since_when_it_fails_and_forget_it_when_a_snapshot_works()
    {
        Put("one");
        BreakTheFolder();

        Assert.ThrowsAny<IOException>(_backup.Snapshot);
        var since = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.ThrowsAny<IOException>(_backup.Snapshot);

        _backup.FailingSince.Should().Be(since, "it is since the first failure, not the last");
        File.Delete(_backup.Folder);
        _backup.Snapshot();
        _backup.FailingSince.Should().BeNull();
    }

    [Fact]
    public void should_leave_no_half_written_copy_behind_when_the_copy_fails()
    {
        Put("one");
        _backup.Snapshot();
        _clock.Advance(TimeSpan.FromDays(1));
        var blocker = Path.Combine(_backup.Folder, "mnemarr-" + _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss") + ".db");
        Directory.CreateDirectory(blocker);

        Assert.ThrowsAny<IOException>(_backup.Snapshot);

        Directory.GetFiles(_backup.Folder, "*.partial").Should().BeEmpty();
    }

    [Fact]
    public void should_never_count_a_stray_file_as_a_snapshot_and_never_delete_a_snapshot_to_make_room_for_one()
    {
        Put("one");
        _options.Keep = 2;
        Directory.CreateDirectory(_backup.Folder);
        File.WriteAllText(Path.Combine(_backup.Folder, "mnemarr-foo.db"), "a stray file that sorts after every moment");
        var kept = new List<string>();
        for (var day = 0; day < 4; day++)
        {
            kept.Add(_backup.Snapshot());
            _clock.Advance(TimeSpan.FromDays(1));
        }

        Directory.GetFiles(_backup.Folder, "mnemarr-2*.db").OrderBy(file => file, StringComparer.Ordinal).Should().Equal(kept.Skip(2));
        File.Exists(Path.Combine(_backup.Folder, "mnemarr-foo.db")).Should().BeTrue("what is not a snapshot is not touched");
    }

    [Fact]
    public void should_not_call_a_snapshot_that_was_taken_a_failure_when_an_old_one_cannot_be_deleted()
    {
        Put("one");
        _options.Keep = 1;
        _backup.Snapshot();
        _clock.Advance(TimeSpan.FromDays(1));
        var old = Directory.GetFiles(_backup.Folder).Single();
        File.Delete(old);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a file in a folder that is named like a snapshot"), "x");

        var snapshot = _backup.Snapshot();

        File.Exists(snapshot).Should().BeTrue();
        _backup.FailingSince.Should().BeNull();
    }

    [Fact]
    public void should_say_the_snapshots_have_stopped_when_the_newest_is_older_than_twice_the_interval()
    {
        _backup.IsLate().Should().BeFalse("it has not been tried yet");
        Put("one");
        _backup.Snapshot();
        _clock.Advance(TimeSpan.FromHours(47));
        _backup.IsLate().Should().BeFalse();
        _clock.Advance(TimeSpan.FromHours(2));
        _backup.IsLate().Should().BeTrue("the newest is 49 hours old");
        _options.Enabled = false;
        _backup.IsLate().Should().BeFalse("it is turned off");
    }

    [Fact]
    public async Task should_degrade_the_health_with_a_reason_as_soon_as_a_snapshot_fails_even_when_the_last_is_recent_and_when_they_stop_and_not_otherwise()
    {
        var check = new BackupHealthCheck(_backup);
        (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.Should().Be(HealthStatus.Healthy, "nothing has been tried yet");
        Put("one");
        _backup.Snapshot();
        _clock.Advance(TimeSpan.FromDays(1));
        var blocker = Path.Combine(_backup.Folder, "mnemarr-" + _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss") + ".db");
        Directory.CreateDirectory(blocker);

        Assert.ThrowsAny<IOException>(_backup.Snapshot);

        var failing = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        failing.Status.Should().Be(HealthStatus.Degraded, "the newest snapshot is a day old and still, the last attempt failed");
        failing.Data[HealthReasons.Key].Should().Be("backup-failing");

        Directory.Delete(blocker);
        _backup.Snapshot();
        (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.Should().Be(HealthStatus.Healthy);
        _clock.Advance(TimeSpan.FromHours(49));
        var late = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        late.Status.Should().Be(HealthStatus.Degraded);
        late.Data[HealthReasons.Key].Should().Be("backup-late");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task should_degrade_the_health_and_not_throw_when_the_folder_of_the_snapshots_cannot_be_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        Put("one");
        _backup.Snapshot();
        var closed = _backup.Folder;
        File.SetUnixFileMode(closed, UnixFileMode.None);
        try
        {
            var result = await new BackupHealthCheck(_backup).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

            result.Status.Should().Be(HealthStatus.Degraded);
            result.Data[HealthReasons.Key].Should().Be("backup-failing");
        }
        finally
        {
            File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void should_take_a_snapshot_when_the_service_looks_and_it_is_due()
    {
        Put("one");
        var service = new BackupService(_backup, _clock, new ListLogger<BackupService>());

        service.TakeIfDue();

        _backup.Latest().Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public void should_log_an_error_and_stop_nothing_when_the_service_cannot_take_a_snapshot()
    {
        Put("one");
        BreakTheFolder();
        var logger = new ListLogger<BackupService>();
        var service = new BackupService(_backup, _clock, logger);

        Record.Exception(service.TakeIfDue).Should().BeNull("a snapshot that fails is tried again later; it stops nothing");

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task should_take_one_at_start_and_another_when_it_is_due_again_and_stop_without_failing()
    {
        Put("one");
        var service = new BackupService(_backup, _clock, new ListLogger<BackupService>());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => _backup.Latest() is not null);
        var first = _backup.Latest();
        _clock.Advance(TimeSpan.FromHours(25));
        await WaitForAsync(() => _backup.Latest() > first);
        await service.StopAsync(TestContext.Current.CancellationToken);

        _backup.Latest().Should().BeAfter(first!.Value);
    }

    [Fact]
    public async Task should_do_nothing_at_start_when_it_is_turned_off()
    {
        Put("one");
        _options.Enabled = false;
        var service = new BackupService(_backup, _clock, new ListLogger<BackupService>());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Directory.Exists(_backup.Folder).Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue("it happened within five seconds");
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

    [Fact]
    public void should_validate_the_settings_of_the_snapshots_and_read_the_defaults_when_none_is_set()
    {
        var invalid = new ServiceCollection().AddMnemarrCore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Keep"] = "0" }).Build()).BuildServiceProvider();
        var valid = new ServiceCollection().AddMnemarrCore(new ConfigurationBuilder().Build()).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IOptions<BackupOptions>>().Value);
        valid.GetRequiredService<IOptions<BackupOptions>>().Value.Keep.Should().Be(7);
    }
}
