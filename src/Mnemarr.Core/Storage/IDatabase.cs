using Microsoft.Data.Sqlite;

namespace Mnemarr.Core.Storage;

/// <summary>Mnemarr's SQLite database (D-25): what each vertical keeps in it is a step of its <c>Schema</c>.</summary>
public interface IDatabase
{
    /// <summary>An open connection, with the schema in place. The caller disposes it.</summary>
    SqliteConnection Open();
}
