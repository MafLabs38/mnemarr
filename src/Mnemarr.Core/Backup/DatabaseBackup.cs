using System.Globalization;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Backup;

/// <summary>
/// A consistent copy of the database (ADR 0029). SQLite's <c>VACUUM INTO</c> writes a whole, compact database from one read transaction, while Mnemarr
/// goes on working: it is what a backup tool should copy, not the live file with its write-ahead log. The copy is written under a name that no one
/// copies away (<c>.partial</c>), in a file that only the owner can read from its creation, and renamed when it is whole.
/// </summary>
public sealed class DatabaseBackup
{
    private const string Prefix = "mnemarr-";
    private const string Extension = ".db";
    private const string Moment = "yyyyMMdd-HHmmss";
    private readonly IDatabase _database;
    private readonly IOptions<MnemarrOptions> _data;
    private readonly IOptions<BackupOptions> _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private volatile Failing? _failing;

    public DatabaseBackup(IDatabase database, IOptions<MnemarrOptions> data, IOptions<BackupOptions> options, TimeProvider clock)
    {
        _database = database;
        _data = data;
        _options = options;
        _clock = clock;
    }

    /// <summary>The moment the attempts began to fail.</summary>
    private sealed record Failing(DateTimeOffset Since);

    public bool Enabled => _options.Value.Enabled;

    /// <summary>Where the snapshots are: the <c>backup</c> folder of the data folder.</summary>
    public string Folder => Path.Combine(_data.Value.DataFolder, SnapshotFiles.FolderName);

    /// <summary>Since when the snapshots fail, or null when the last attempt worked (or there was none, or they are turned off): said at once, even when an earlier snapshot is recent.</summary>
    public DateTimeOffset? FailingSince => Enabled ? _failing?.Since : null;

    /// <summary>When the newest snapshot was taken (from its name), or null when there is none.</summary>
    public DateTimeOffset? Latest() => Snapshots().Select(snapshot => snapshot.Moment).Cast<DateTimeOffset?>().Max();

    /// <summary>
    /// Whether a snapshot is due: there is none, or the newest is older than <see cref="BackupOptions.EveryHours"/>. Never when the data folder is not there:
    /// creating it would hide a volume that was never mounted from the readiness check.
    /// </summary>
    public bool IsDue() => Enabled && Directory.Exists(_data.Value.DataFolder) && (Latest() is not { } latest || _clock.GetUtcNow() - latest >= TimeSpan.FromHours(_options.Value.EveryHours));

    /// <summary>Whether snapshots have stopped for too long: the newest is older than twice the interval.</summary>
    public bool IsLate() => Enabled && Latest() is { } latest && _clock.GetUtcNow() - latest > TimeSpan.FromHours(2 * _options.Value.EveryHours);

    /// <summary>Takes a snapshot now, deletes the ones beyond <see cref="BackupOptions.Keep"/>, and says where it is.</summary>
    public string Snapshot()
    {
        // One at a time: Prune deletes the copies that were never finished, which must not be one that another caller is writing.
        lock (_gate)
        {
            try
            {
                var path = TakeSnapshot();
                _failing = null;
                Prune();
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failing ??= new Failing(_clock.GetUtcNow());
                throw;
            }
        }
    }

    private string TakeSnapshot()
    {
        // The database is opened first: it refuses a data folder that does not exist, and nothing is created in its place.
        using var connection = _database.Open();
        SnapshotFiles.EnsureFolder(Folder);
        var path = Path.Combine(Folder, Prefix + _clock.GetUtcNow().ToString(Moment, CultureInfo.InvariantCulture) + Extension);
        if (File.Exists(path))
        {
            // Two snapshots in the same second: the one that is there is whole, and a whole copy is never overwritten.
            return path;
        }

        SnapshotFiles.Write(connection, path);
        return path;
    }

    /// <summary>
    /// What is a snapshot, in one place: a file named after a moment. Anything else in the folder (a stray file, a half-written copy) is not one, for the
    /// newest, for what is kept and for what is deleted. Newest first.
    /// </summary>
    private List<(string Path, DateTimeOffset Moment)> Snapshots()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(Folder, Prefix + "*" + Extension)
            .Select(file => (Path: file, Moment: MomentOf(file)))
            .Where(snapshot => snapshot.Moment is not null)
            .Select(snapshot => (snapshot.Path, Moment: snapshot.Moment!.Value))
            .OrderByDescending(snapshot => snapshot.Moment)];
    }

    /// <summary>Deletes what is beyond <see cref="BackupOptions.Keep"/>. A file that cannot be deleted is left for the next time: the snapshot that was taken is whole.</summary>
    private void Prune()
    {
        try
        {
            foreach (var old in Snapshots().Skip(_options.Value.Keep))
            {
                File.Delete(old.Path);
            }

            // A copy that was never finished (the process was stopped in the middle) is of no use to anyone.
            foreach (var unfinished in Directory.EnumerateFiles(Folder, Prefix + "*" + Extension + SnapshotFiles.Partial))
            {
                File.Delete(unfinished);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tried again after the next snapshot: the one that was taken is whole.
        }
    }

    private static DateTimeOffset? MomentOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
        return DateTimeOffset.TryParseExact(name, Moment, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}
