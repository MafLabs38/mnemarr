using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>The schema, versioned with <c>PRAGMA user_version</c>: a step is applied once, in order.</summary>
public static class Schema
{
    private static readonly string[] Steps =
    [
        // 1: accounts, their API keys, their sessions, and the audit log.
        """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            kind TEXT NOT NULL,
            role TEXT NOT NULL,
            enabled INTEGER NOT NULL,
            password_hash TEXT,
            created_at TEXT NOT NULL
        );
        CREATE TABLE api_keys (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            prefix TEXT NOT NULL,
            secret_hash TEXT NOT NULL UNIQUE,
            sources TEXT,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            last_used_at TEXT,
            revoked_at TEXT
        );
        CREATE INDEX api_keys_by_account ON api_keys (account_id);
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE INDEX sessions_by_account ON sessions (account_id);
        CREATE TABLE audit_events (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT NOT NULL,
            detail TEXT NOT NULL
        );
        CREATE INDEX audit_events_by_action ON audit_events (action, id);
        """,
        // 2: sources, notes, chunks, and the full-text index of the chunks kept in step by triggers.
        """
        CREATE TABLE sources (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            mode TEXT NOT NULL,
            path TEXT NOT NULL,
            created_at TEXT NOT NULL,
            managed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE notes (
            id INTEGER PRIMARY KEY,
            source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            topic TEXT NOT NULL,
            title TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            chunker INTEGER NOT NULL DEFAULT 0,
            size INTEGER NOT NULL DEFAULT 0,
            modified_ticks INTEGER NOT NULL DEFAULT 0,
            indexed_at TEXT NOT NULL,
            UNIQUE (source_id, path)
        );
        CREATE TABLE chunks (
            id INTEGER PRIMARY KEY,
            note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            heading TEXT NOT NULL,
            text TEXT NOT NULL
        );
        CREATE INDEX chunks_by_note ON chunks (note_id);
        CREATE VIRTUAL TABLE chunks_fts USING fts5(
            text, heading, content = 'chunks', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2');
        CREATE TRIGGER chunks_after_insert AFTER INSERT ON chunks BEGIN
            INSERT INTO chunks_fts (rowid, text, heading) VALUES (new.id, new.text, new.heading);
        END;
        CREATE TRIGGER chunks_after_delete AFTER DELETE ON chunks BEGIN
            INSERT INTO chunks_fts (chunks_fts, rowid, text, heading) VALUES ('delete', old.id, old.text, old.heading);
        END;
        """,
        // 3: what a note says about other notes (wikilinks, for the reader and its backlinks) and its tags, with the version of the reading that
        // made them, and the image files of a source. The name of a note is its file name without the extension, lower case: what a [[wikilink]]
        // points at.
        """
        ALTER TABLE notes ADD COLUMN name TEXT NOT NULL DEFAULT '';
        ALTER TABLE notes ADD COLUMN meta_version INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX notes_by_name ON notes (name);
        CREATE TABLE note_links (
            note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            target TEXT NOT NULL
        );
        CREATE INDEX note_links_by_note ON note_links (note_id);
        CREATE INDEX note_links_by_target ON note_links (target);
        CREATE TABLE note_tags (
            note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            tag TEXT NOT NULL
        );
        CREATE INDEX note_tags_by_note ON note_tags (note_id);
        CREATE INDEX note_tags_by_tag ON note_tags (tag);
        CREATE TABLE images (
            source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            name TEXT NOT NULL,
            PRIMARY KEY (source_id, path)
        );
        CREATE INDEX images_by_name ON images (source_id, name);
        """,
        // 4: a source can be the checkout of a Git repository (its address, branch, and the name of the credential to use; never the credential).
        """
        ALTER TABLE sources ADD COLUMN repository_url TEXT;
        ALTER TABLE sources ADD COLUMN repository_branch TEXT;
        ALTER TABLE sources ADD COLUMN repository_credential TEXT;
        """,
        // 5: the conversations a collector sent: one row per (machine, session), and how many lines of it were taken (the position the collector resumes from).
        """
        CREATE TABLE conversations (
            machine TEXT NOT NULL,
            session TEXT NOT NULL,
            application TEXT NOT NULL,
            format TEXT NOT NULL,
            lines INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            note_path TEXT NOT NULL,
            PRIMARY KEY (machine, session)
        );
        CREATE INDEX conversations_by_machine ON conversations (machine, updated_at);
        """,
        // 6: the size of the note of a conversation as it was last written, so that new turns are added to the end of it and a failed attempt is cut off, instead of writing the whole note again.
        """
        ALTER TABLE conversations ADD COLUMN note_bytes INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    /// <summary>The version of the schema this code expects: the number of steps.</summary>
    public static int Version => Steps.Length;

    /// <summary>The version of the schema the database has: the steps that were applied to it.</summary>
    public static int CurrentVersion(SqliteConnection connection) =>
        Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    public static void Apply(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode = WAL;");
        var version = CurrentVersion(connection);
        if (version > Steps.Length)
        {
            // A rollback to an older image: its code would read and write a schema it does not know.
            throw new UnsupportedSchemaException(version, Steps.Length);
        }

        for (var step = version; step < Steps.Length; step++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Steps[step], transaction);
            Execute(connection, $"PRAGMA user_version = {step + 1};", transaction);
            transaction.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
