using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>What it means when the database cannot be reached: one answer for every caller that must stay up and say so.</summary>
public static class StorageErrors
{
    /// <summary>
    /// The database or its folder cannot be opened: the driver failed, the folder is missing or cannot be read or written. A caller that can
    /// go on without it (a hosted service, a probe) catches exactly this; anything else is a bug and propagates.
    /// </summary>
    public static bool IsUnavailable(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException;
}
