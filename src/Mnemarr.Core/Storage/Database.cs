using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Storage;

public sealed class Database : IDatabase
{
    private readonly string _folder;
    private readonly string _connectionString;
    private readonly Lock _gate = new();
    private volatile bool _ready;

    public Database(IOptions<MnemarrOptions> options)
    {
        _folder = options.Value.DataFolder;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_folder, "mnemarr.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        // SQLite would create the folder itself: a volume that was never mounted would then look like a fresh, empty one.
        if (!Directory.Exists(_folder))
        {
            throw new DirectoryNotFoundException("The data folder does not exist.");
        }

        // SQLite would create the file with the umask of the process and the WAL files after it: it is made for its owner first.
        CreateRestrictedFile();

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        if (_ready)
        {
            return;
        }

        lock (_gate)
        {
            if (_ready)
            {
                return;
            }

            CopyBeforeMigration(connection);
            Schema.Apply(connection);
            _ready = true;
        }
    }

    /// <summary>
    /// A migration is not undone, and a deployment that follows the versions applies it at start (ADR 0029): when a database that exists is about to be
    /// migrated, a whole copy of it is kept first, in the <c>backup</c> folder, the last three. A copy that cannot be written stops the start: a migration is
    /// not made without the way back.
    /// </summary>
    private void CopyBeforeMigration(SqliteConnection connection)
    {
        var from = Schema.CurrentVersion(connection);
        if (from == 0 || from >= Schema.Version)
        {
            return;
        }

        var folder = Path.Combine(_folder, "backup");
        SnapshotFiles.EnsureFolder(folder);
        SnapshotFiles.Write(connection, Path.Combine(folder, $"before-migration-v{from}-to-v{Schema.Version}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.db"));
        foreach (var old in Directory.EnumerateFiles(folder, "before-migration-*.db").OrderByDescending(file => file, StringComparer.Ordinal).Skip(3))
        {
            File.Delete(old);
        }
    }

    /// <summary>
    /// The database holds the hashes of passwords, sessions and keys: only the owner reads it, from its creation (SQLite gives the WAL files
    /// the mode of the main file) and also when it was restored from a backup with another mode.
    /// </summary>
    private void CreateRestrictedFile()
    {
        if (_ready || OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(_folder, "mnemarr.db");
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!File.Exists(file))
        {
            try
            {
                using var created = new FileStream(file, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerOnly });
            }
            catch (IOException) when (File.Exists(file))
            {
                // Another connection created it first: it was made by this method too.
            }
        }

        File.SetUnixFileMode(file, OwnerOnly);
    }
}
