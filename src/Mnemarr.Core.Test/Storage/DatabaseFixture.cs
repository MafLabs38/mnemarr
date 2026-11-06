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
        Scalar("PRAGMA user_version").Should().Be((long)Schema.Version);
        File.Exists(Path.Combine(_workspace.DataFolder, "mnemarr.db")).Should().BeTrue();
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('accounts', 'api_keys', 'sessions', 'audit_events')").Should().Be(4L);
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('sources', 'notes', 'chunks', 'chunks_fts')").Should().Be(4L);
    }

    [Fact]
    public void should_bring_a_database_made_by_the_previous_version_up_to_date_without_touching_what_it_holds()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO accounts (id, name, kind, role, enabled, created_at) VALUES ('a', 'alice', 'User', 'Reader', 1, 'now');
                DROP TABLE images; DROP TABLE note_tags; DROP TABLE note_links; DROP TABLE chunks_fts; DROP TABLE chunks; DROP TABLE notes; DROP TABLE sources;
                DROP TABLE conversations; DROP TABLE machines; DROP TABLE enrolments;
                PRAGMA user_version = 1;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var tables = upgraded.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('sources', 'notes', 'chunks', 'chunks_fts')";
        tables.ExecuteScalar().Should().Be(4L);
        using var accounts = upgraded.CreateCommand();
        accounts.CommandText = "SELECT COUNT(*) FROM accounts";
        accounts.ExecuteScalar().Should().Be(1L);
    }

    [Fact]
    public void should_add_the_links_and_tags_to_a_database_that_has_sources_and_notes_without_losing_a_note()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO sources (id, name, kind, mode, path, created_at) VALUES ('s', 'courses', 'Knowledge', 'ReadOnly', '/x', 'now');
                INSERT INTO notes (source_id, path, topic, title, content_hash, indexed_at) VALUES ('s', 'a.md', '', 'A', 'h', 'now');
                DROP INDEX notes_by_name; DROP TABLE images; DROP TABLE note_tags; DROP TABLE note_links; ALTER TABLE notes DROP COLUMN name; ALTER TABLE notes DROP COLUMN meta_version;
                ALTER TABLE sources DROP COLUMN repository_url; ALTER TABLE sources DROP COLUMN repository_branch; ALTER TABLE sources DROP COLUMN repository_credential;
                DROP TABLE conversations; DROP TABLE machines; DROP TABLE enrolments;
                PRAGMA user_version = 2;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var command = upgraded.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM notes), (SELECT COUNT(*) FROM note_links) + (SELECT COUNT(*) FROM note_tags), (SELECT name FROM notes)";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)).Should().Be((1L, 0L, string.Empty), "the note stays; the next pass reads its links, as its chunker version is older");
    }

    [Fact]
    public void should_make_a_source_of_a_database_made_before_a_plain_folder_when_the_git_columns_are_added()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO sources (id, name, kind, mode, path, created_at) VALUES ('s', 'courses', 'Knowledge', 'ReadOnly', '/x', 'now');
                ALTER TABLE sources DROP COLUMN repository_url; ALTER TABLE sources DROP COLUMN repository_branch; ALTER TABLE sources DROP COLUMN repository_credential;
                DROP TABLE conversations; DROP TABLE machines; DROP TABLE enrolments;
                PRAGMA user_version = 3;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var command = upgraded.CreateCommand();
        command.CommandText = "SELECT name, repository_url, repository_branch, repository_credential FROM sources";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetString(0), reader.IsDBNull(1), reader.IsDBNull(2), reader.IsDBNull(3)).Should().Be(("courses", true, true, true));
    }

    [Fact]
    public void should_give_the_conversations_of_a_database_made_before_titles_a_first_sentence_title_to_start_from()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO conversations (machine, session, application, format, lines, started_at, updated_at, note_path) VALUES ('pc', 's1', 'app', 'claude-code', 3, 'then', 'now', 'pc/app/x.md');
                ALTER TABLE conversations DROP COLUMN title; ALTER TABLE conversations DROP COLUMN title_source; ALTER TABLE conversations DROP COLUMN summary; ALTER TABLE conversations DROP COLUMN title_tries;
                PRAGMA user_version = 7;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var check = upgraded.CreateCommand();
        check.CommandText = "SELECT title, title_source, summary, title_tries FROM conversations";
        using var reader = check.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.IsDBNull(0), reader.GetString(1), reader.IsDBNull(2), reader.GetInt32(3)).Should().Be((true, "first-sentence", true, 0));
    }

    [Fact]
    public void should_add_the_pause_to_a_database_whose_machines_did_not_have_one_without_touching_them()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO machines (name, kind, account_id, enrolled_at) VALUES ('pc', 'intermittent', 'a', 'then');
                ALTER TABLE machines DROP COLUMN paused;
                ALTER TABLE conversations DROP COLUMN title; ALTER TABLE conversations DROP COLUMN title_source; ALTER TABLE conversations DROP COLUMN summary; ALTER TABLE conversations DROP COLUMN title_tries;
                PRAGMA user_version = 6;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var check = upgraded.CreateCommand();
        check.CommandText = "SELECT name, paused FROM machines";
        using var reader = check.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetString(0), reader.GetInt32(1)).Should().Be(("pc", 0));
    }

    [Fact]
    public void should_add_the_machines_and_the_enrolments_to_a_database_that_has_conversations_without_touching_them()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO conversations (machine, session, application, format, lines, started_at, updated_at, note_path) VALUES ('pc', 's1', 'app', 'claude-code', 3, 'then', 'now', 'pc/app/x.md');
                DROP TABLE machines; DROP TABLE enrolments;
                ALTER TABLE conversations DROP COLUMN title; ALTER TABLE conversations DROP COLUMN title_source; ALTER TABLE conversations DROP COLUMN summary; ALTER TABLE conversations DROP COLUMN title_tries;
                PRAGMA user_version = 5;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var check = upgraded.CreateCommand();
        check.CommandText = "SELECT (SELECT COUNT(*) FROM conversations), (SELECT COUNT(*) FROM sqlite_master WHERE name IN ('machines', 'enrolments'))";
        using var reader = check.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetInt32(0), reader.GetInt32(1)).Should().Be((1, 2));
    }

    [Fact]
    public void should_add_the_conversations_to_a_database_made_before_collectors_without_touching_its_sources()
    {
        using (var connection = _workspace.Database.Open())
        {
            using var back = connection.CreateCommand();
            back.CommandText = """
                INSERT INTO sources (id, name, kind, mode, path, created_at) VALUES ('s', 'courses', 'Knowledge', 'ReadOnly', '/x', 'now');
                DROP TABLE conversations; DROP TABLE machines; DROP TABLE enrolments;
                PRAGMA user_version = 4;
                """;
            back.ExecuteNonQuery();
        }

        using var upgraded = new Database(_workspace.Options).Open();

        using var command = upgraded.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM sources), (SELECT COUNT(*) FROM conversations)";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        (reader.GetInt64(0), reader.GetInt64(1)).Should().Be((1L, 0L));
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
