using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Backup;

/// <summary>
/// A consistent copy of the database (ADR 0029). SQLite's <c>VACUUM INTO</c> writes a whole, compact database from one read transaction, while Mnemarr
/// goes on working: it is what a backup tool should copy, not the live file with its write-ahead log. The copy is written under a name that no one
/// copies away (<c>.partial</c>) and renamed when it is whole, and only the owner can read it, as the database itself.
/// </summary>
public sealed class DatabaseBackup
{
    private const string Prefix = "mnemarr-";
    private const string Extension = ".db";
    private const string Partial = ".partial";
    private const string Moment = "yyyyMMdd-HHmmss";
    private readonly IDatabase _database;
    private readonly IOptions<MnemarrOptions> _data;
    private readonly IOptions<BackupOptions> _options;
    private readonly TimeProvider _clock;

    public DatabaseBackup(IDatabase database, IOptions<MnemarrOptions> data, IOptions<BackupOptions> options, TimeProvider clock)
    {
        _database = database;
        _data = data;
        _options = options;
        _clock = clock;
    }

    public bool Enabled => _options.Value.Enabled;

    public string Folder => _options.Value.Folder ?? Path.Combine(_data.Value.DataFolder, "backup");

    /// <summary>When the newest snapshot was taken (from its name), or null when there is none.</summary>
    public DateTimeOffset? Latest() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, Prefix + "*" + Extension).Select(MomentOf).OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Max()
            : null;

    /// <summary>
    /// Whether a snapshot is due: there is none, or the newest is older than <see cref="BackupOptions.EveryHours"/>. Never when the data folder is not there:
    /// creating it would hide a volume that was never mounted from the readiness check.
    /// </summary>
    public bool IsDue() => Enabled && Directory.Exists(_data.Value.DataFolder) && (Latest() is not { } latest || _clock.GetUtcNow() - latest >= TimeSpan.FromHours(_options.Value.EveryHours));

    /// <summary>Takes a snapshot now, deletes the ones beyond <see cref="BackupOptions.Keep"/>, and says where it is.</summary>
    public string Snapshot()
    {
        // The database is opened first: it refuses a data folder that does not exist, and nothing is created in its place.
        using var connection = _database.Open();
        EnsureFolder();
        var path = Path.Combine(Folder, Prefix + _clock.GetUtcNow().ToString(Moment, CultureInfo.InvariantCulture) + Extension);
        if (File.Exists(path))
        {
            // Two snapshots in the same second: the one that is there is whole.
            return path;
        }

        var partial = path + Partial;
        File.Delete(partial);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", partial);
            command.ExecuteNonQuery();
        }

        connection.Close();
        SqliteConnection.ClearAllPools();
        Restrict(partial);
        File.Move(partial, path);
        Prune();
        return path;
    }

    private void EnsureFolder()
    {
        if (!Directory.Exists(Folder))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Folder);
            }
            else
            {
                Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static void Restrict(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(Folder, Prefix + "*" + Extension).OrderByDescending(file => file, StringComparer.Ordinal).Skip(_options.Value.Keep))
        {
            File.Delete(old);
        }

        // A copy that was never finished (the process was stopped in the middle) is of no use to anyone.
        foreach (var unfinished in Directory.EnumerateFiles(Folder, Prefix + "*" + Extension + Partial))
        {
            File.Delete(unfinished);
        }
    }

    private static DateTimeOffset? MomentOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
        return DateTimeOffset.TryParseExact(name, Moment, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}
