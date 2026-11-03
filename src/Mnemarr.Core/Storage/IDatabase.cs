using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>Mnemarr's SQLite database: sources, notes, chunks and their full-text index (D-25).</summary>
public interface IDatabase
{
    /// <summary>An open connection, with the schema in place. The caller disposes it.</summary>
    SqliteConnection Open();
}
