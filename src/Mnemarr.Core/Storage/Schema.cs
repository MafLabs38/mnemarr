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
        // 5: the conversations a collector sent: one row per (machine, session), how many lines of it were taken (the position the collector resumes from), and the size of its note as last written (new turns are added to the end of it, and a failed attempt is cut off, instead of writing the whole note again), and the name of the account that sent the last batch (the key itself is not recorded: a key per machine, and so what a revoked key wrote, comes with enrolment).
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
            note_bytes INTEGER NOT NULL DEFAULT 0,
            account TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (machine, session)
        );
        CREATE INDEX conversations_by_machine ON conversations (machine, updated_at);
        """,
        // 6: the machines that joined (a kind, the account that sends for each, when it was last heard from and what it said) and the codes that join them (kept as hashes).
        """
        CREATE TABLE machines (
            name TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL,
            account_id TEXT NOT NULL UNIQUE,
            enrolled_at TEXT NOT NULL,
            last_seen_at TEXT,
            version TEXT,
            pending_lines INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            revoked_at TEXT
        );
        CREATE TABLE enrolments (
            id TEXT NOT NULL PRIMARY KEY,
            machine TEXT NOT NULL,
            kind TEXT NOT NULL,
            state TEXT NOT NULL,
            code_hash TEXT NOT NULL UNIQUE,
            requested_at TEXT NOT NULL,
            requested_by TEXT NOT NULL,
            approved_at TEXT,
            approved_by TEXT,
            expires_at TEXT NOT NULL
        );
        """,
        // 7: a collector that a person paused says so with its sign of life (ADR 0027 §16).
        """
        ALTER TABLE machines ADD COLUMN paused INTEGER NOT NULL DEFAULT 0;
        """,
        // 8: the title of a conversation is its own: what it says, where it comes from (the first sentence, a model, a person), a short summary, and how many times a model was tried (ADR 0028).
        """
        ALTER TABLE conversations ADD COLUMN title TEXT;
        ALTER TABLE conversations ADD COLUMN title_source TEXT NOT NULL DEFAULT 'first-sentence';
        ALTER TABLE conversations ADD COLUMN summary TEXT;
        ALTER TABLE conversations ADD COLUMN title_tries INTEGER NOT NULL DEFAULT 0;
        """,
        // 9: the machines that ask to be linked (ADR 0030): a short code and a secret of their own, kept as hashes, and the name a person chose when they approved.
        """
        CREATE TABLE pairings (
            id TEXT NOT NULL PRIMARY KEY,
            hostname TEXT NOT NULL,
            state TEXT NOT NULL,
            machine TEXT,
            kind TEXT,
            user_code_hash TEXT NOT NULL UNIQUE,
            device_hash TEXT NOT NULL UNIQUE,
            requested_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            approved_by TEXT
        );
        """,
        // 10: a machine is a computer (ADR 0031): the hash of the identifier the system made for itself, kept with the machine, and sent with a request to be linked.
        """
        ALTER TABLE machines ADD COLUMN computer_id TEXT;
        CREATE UNIQUE INDEX machines_by_computer ON machines (computer_id) WHERE computer_id IS NOT NULL;
        ALTER TABLE pairings ADD COLUMN computer_id TEXT;
        """,
        // 11: which program a machine runs (the system it is for, and the hash of the program), so that a person is told when this Mnemarr carries another (ADR 0031 §7).
        """
        ALTER TABLE machines ADD COLUMN platform TEXT;
        ALTER TABLE machines ADD COLUMN program TEXT;
        """,
        // 12: the computer a conversation came from (the identifier of the machine that sent it, when it had one): a conversation is only taken over by the same computer (ADR 0031 §4).
        """
        ALTER TABLE conversations ADD COLUMN computer_id TEXT;
        """,
        // 13: the names a machine had before it was renamed (a collector goes on sending the name in its own configuration, and its key is what says who it is), and a lookup of a conversation by its session.
        """
        CREATE TABLE machine_aliases (
            alias TEXT NOT NULL PRIMARY KEY,
            machine TEXT NOT NULL
        );
        CREATE INDEX conversations_by_session ON conversations (session);
        """,
        // 14: how often a collector makes a pass, as it says it: a machine is taken to be sending while its last sign of life is younger than three passes (ADR 0031 §3).
        """
        ALTER TABLE machines ADD COLUMN interval_seconds INTEGER;
        """,
        // 15: whether a collector was installed with the installer and can replace itself: only those are told that an update is available (ADR 0032).
        """
        ALTER TABLE machines ADD COLUMN can_update INTEGER NOT NULL DEFAULT 0;
        """,
        // 16: why the last attempt of a collector to update failed, when it did: said on the machines page, so that nobody has to log in to the machine to know (ADR 0032).
        """
        ALTER TABLE machines ADD COLUMN update_note TEXT;
        """,
        // 17: which model computed the vector of a chunk (ADR 0034): a chunk that goes takes its row with it, and a chunk with no row, or a row of another model, is computed again.
        """
        CREATE TABLE chunk_embeddings (
            chunk_id INTEGER NOT NULL PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
            model TEXT NOT NULL
        );
        CREATE INDEX chunk_embeddings_by_model ON chunk_embeddings (model);
        """,
        // 18: the chat (ADR 0035): the conversations of the chat belong to a person (the runner keeps their content), and the runner reaches the MCP server with a token that belongs
        // to a person and a conversation and expires: only its hash is kept.
        """
        CREATE TABLE chat_conversations (
            id TEXT NOT NULL PRIMARY KEY,
            account_id TEXT NOT NULL,
            owner_name TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX chat_conversations_by_account ON chat_conversations (account_id);
        CREATE TABLE chat_tokens (
            token_hash TEXT NOT NULL PRIMARY KEY,
            account_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            absolute_expires_at TEXT NOT NULL
        );
        CREATE INDEX chat_tokens_by_conversation ON chat_tokens (conversation_id);
        """,
        // 19: what a collector found where it looks for conversations (a sentence with no path), so that a machine that sends nothing says why on the machines page.
        """
        ALTER TABLE machines ADD COLUMN watching TEXT;
        """,
        // 20: the consolidation (ADR 0036): how far each conversation was turned into notes of the memory vault (the lines held and the turns read), how many times it failed, and why.
        """
        CREATE TABLE consolidations (
            machine TEXT NOT NULL,
            session TEXT NOT NULL,
            lines_done INTEGER NOT NULL DEFAULT 0,
            turns_done INTEGER NOT NULL DEFAULT 0,
            notes INTEGER NOT NULL DEFAULT 0,
            tries INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            last_at TEXT NOT NULL,
            PRIMARY KEY (machine, session)
        );
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
