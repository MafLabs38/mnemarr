using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>
/// How a copy of the database is written (ADR 0029), for the daily snapshot and for the copy made before a migration: into a file that only its owner
/// can read from its creation, under a <c>.partial</c> name that no backup tool copies, renamed when it is whole, in a folder that only its owner opens.
/// </summary>
internal static class SnapshotFiles
{
    public const string Partial = ".partial";
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnly = OwnerReadWrite | UnixFileMode.UserExecute;

    public static void EnsureFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }

        // Also when it exists already: a folder that came back from a backup with wider modes is closed again.
        Directory.CreateDirectory(folder, OwnerOnly);
        File.SetUnixFileMode(folder, OwnerOnly);
    }

    /// <summary>Writes a whole copy of the database of <paramref name="connection"/> to <paramref name="path"/>: nothing is left half written.</summary>
    public static void Write(SqliteConnection connection, string path)
    {
        var partial = path + Partial;
        File.Delete(partial);
        using (OperatingSystem.IsWindows()
            ? new FileStream(partial, FileMode.CreateNew, FileAccess.Write)
            : new FileStream(partial, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerReadWrite }))
        {
            // Created, and closed: SQLite writes into a file that is new or empty.
        }

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", partial);
                command.ExecuteNonQuery();
            }

            File.Move(partial, path);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }
}
